using System.Net;
using System.Reflection;
using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class BackupNetworkTrustTests
{
    private static readonly Guid Owner = Guid.NewGuid();

    [Theory]
    [InlineData("ftp://NAS.Example.:21/backups", "ftp://nas.example:21/")]
    [InlineData("ftp://nas.example:2121/path", "ftp://nas.example:2121/")]
    [InlineData("ftp://[::ffff:192.168.1.120]/", "ftp://192.168.1.120:21/")]
    public void Endpoint_normalization_is_exact_and_excludes_paths(string raw, string expected) =>
        Assert.Equal(expected, BackupNetworkTrustService.NormalizeTarget(raw));

    [Theory]
    [InlineData("https://nas.example/")]
    [InlineData("ftp://user:SECRET@nas.example/")]
    [InlineData("ftp://nas.example/#SECRET")]
    public void Secrets_and_other_protocols_are_not_accepted(string raw) =>
        Assert.Throws<AppException>(() => BackupNetworkTrustService.NormalizeTarget(raw));

    [Fact]
    public async Task Permissions_persist_without_restart_match_exactly_and_can_be_revoked()
    {
        using var area = new BulutAlani();
        var trust = new BackupNetworkTrustService(area.DosyaDeposu, new Owners());
        Assert.False(await trust.IsTrustedAsync(new Uri("ftp://nas.example/a"), area.Iptal));
        await trust.SetAsync("ftp://nas.example/a", true, Owner, false, area.Iptal);
        var reloaded = new BackupNetworkTrustService(area.DosyaDeposu, new Owners());
        Assert.True(await reloaded.IsTrustedAsync(new Uri("ftp://NAS.example:21/other"), area.Iptal));
        foreach (var uri in new[] { "ftp://other.nas.example/", "ftp://nas.example:2121/", "http://nas.example/" })
            Assert.False(await reloaded.IsTrustedAsync(new Uri(uri), area.Iptal));
        await trust.SetAsync("ftp://nas.example/", false, Owner, false, area.Iptal);
        Assert.False(await reloaded.IsTrustedAsync(new Uri("ftp://nas.example/"), area.Iptal));
    }

    [Theory]
    [InlineData("192.168.1.120", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("fd12::15", true)]
    [InlineData("::ffff:192.168.1.120", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("::ffff:169.254.169.254", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("fd00:ec2::254", false)]
    [InlineData("100.100.100.200", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("fe80::1", false)]
    public async Task Only_private_NAS_ranges_get_the_exception(string address, bool allowed)
    {
        var blocked = await BackupNetworkTrustService.FindBlockedTrustedHostAsync(new Uri("ftp://nas.example/"),
            (_, _) => Task.FromResult(new[] { IPAddress.Parse(address) }), CancellationToken.None);
        Assert.Equal(allowed, blocked is null);
    }

    [Fact]
    public async Task Unresolved_and_mixed_metadata_DNS_fail_closed()
    {
        var target = new Uri("ftp://nas.example/");
        Assert.NotNull(await BackupNetworkTrustService.FindBlockedTrustedHostAsync(target, (_, _) => Task.FromResult(Array.Empty<IPAddress>()), default));
        Assert.NotNull(await BackupNetworkTrustService.FindBlockedTrustedHostAsync(target, (_, _) => throw new InvalidOperationException(), default));
        Assert.NotNull(await BackupNetworkTrustService.FindBlockedTrustedHostAsync(target, (_, _) => Task.FromResult(new[] { IPAddress.Parse("192.168.1.120"), IPAddress.Loopback }), default));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Both_endpoints_reject_nonowners_or_impersonation_and_audit(bool owner, bool impersonating)
    {
        using var area = new BulutAlani();
        var trust = new BackupNetworkTrustService(area.DosyaDeposu, new Owners());
        var actor = owner ? Owner : Guid.NewGuid();
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, actor.ToString()), new(ClaimTypes.Role, "Administrator") };
        if (impersonating) claims.Add(new("impersonator_id", Guid.NewGuid().ToString()));
        var controller = new BackupNetworkTrustController(trust, area.Denetim)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) } } };
        var get = await Assert.ThrowsAsync<AppException>(() => controller.GetAsync("ftp://nas.example/", default));
        Assert.Equal(403, get.HttpStatusCode);
        await Assert.ThrowsAsync<AppException>(() => controller.SetAsync(new("ftp://nas.example/", true), default));
        Assert.False(await trust.IsTrustedAsync(new Uri("ftp://nas.example/"), default));
        Assert.Contains("backup.network_trust.denied", area.Denetim.Kayitlar.Select(entry => entry.Action));
    }

    [Fact]
    public async Task Owner_endpoint_saves_only_origin_and_audits_it()
    {
        using var area = new BulutAlani();
        var trust = new BackupNetworkTrustService(area.DosyaDeposu, new Owners());
        var controller = new BackupNetworkTrustController(trust, area.Denetim)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, Owner.ToString()) }, "test")) } } };
        Assert.IsType<OkObjectResult>((await controller.SetAsync(new("ftp://nas.example:2121/private/path", true), default)).Result);
        Assert.IsType<OkObjectResult>((await controller.GetAsync("ftp://nas.example:2121/", default)).Result);
        var json = await File.ReadAllTextAsync(area.DosyaDeposu.ResolvePath("config/backup-network-trust.json"));
        Assert.Contains("ftp://nas.example:2121/", json);
        Assert.DoesNotContain("private", json);
        Assert.Contains("backup.network_trust.changed", area.Denetim.Kayitlar.Select(entry => entry.Action));
    }

    [Fact]
    public async Task Backup_preflight_uses_persistent_grant_then_blocks_after_revocation()
    {
        using var area = new BulutAlani();
        var trust = new BackupNetworkTrustService(area.DosyaDeposu, new Owners());
        var service = Service(area, trust);
        await Assert.ThrowsAsync<AppException>(() => Preflight(service, "ftp", "ftp://nas.example/path"));
        await trust.SetAsync("ftp://nas.example/", true, Owner, false, area.Iptal);
        await Preflight(service, "ftp", "ftp://nas.example/another");
        await Assert.ThrowsAsync<AppException>(() => Preflight(service, "http", "http://nas.example/"));
        await Assert.ThrowsAsync<AppException>(() => Preflight(service, "ftp", "ftp://nas.example:2121/"));
        await trust.SetAsync("ftp://nas.example/", false, Owner, false, area.Iptal);
        await Assert.ThrowsAsync<AppException>(() => Preflight(service, "ftp", "ftp://nas.example/path"));
    }

    [Fact]
    public async Task Public_backup_and_test_flows_block_untrusted_or_revoked_FTP_before_upload()
    {
        using var area = new BulutAlani();
        var trust = new BackupNetworkTrustService(area.DosyaDeposu, new Owners());
        var service = Service(area, trust);
        var request = new BackupRemoteTestRequest { TargetType = "ftp", RemoteUrl = "ftp://nas.example/backups", RemoteUserName = "user", RemotePassword = "secret" };
        Assert.Equal("backup.remote_target_forbidden", (await service.TestRemoteTargetAsync(request, area.Iptal)).Message);
        await trust.SetAsync(request.RemoteUrl, true, Owner, false, area.Iptal);
        await trust.SetAsync(request.RemoteUrl, false, Owner, false, area.Iptal);
        Assert.Equal("backup.remote_target_forbidden", (await service.TestRemoteTargetAsync(request, area.Iptal)).Message);
        var error = await Assert.ThrowsAsync<AppException>(() => service.BackupSiteAsync(new BackupSiteRequest
        {
            SiteName = BulutAlani.Site, TargetType = "ftp", RemoteUrl = request.RemoteUrl,
            RemoteUserName = "user", RemotePassword = "secret", IncludeFiles = true, IncludeConfig = false
        }, area.Iptal));
        Assert.Equal("backup.remote_target_forbidden", error.MessageKey);
    }

    [Fact]
    public async Task Verification_and_delete_observe_grant_then_revoke_before_FTP_commands()
    {
        using var area = new BulutAlani();
        var trust = new BackupNetworkTrustService(area.DosyaDeposu, new Owners());
        var ftp = new SahteFtp { ListeYaniti = new(true, ["backup.zip"], 226, "OK") };
        var service = Service(area, trust, ftp);
        const string url = "ftp://nas.example/backups";
        await area.UzakHedefiYazAsync("ftp", url, "user", SahteKoruyucu.Onek + "secret");
        var path = Path.Combine(area.Kok, "backup.zip");
        File.WriteAllText(path, "archive");
        await area.KayitlariYazAsync(new BackupRecordDto
        {
            Id = "nas", Name = "backup.zip", Type = "site", Source = BulutAlani.Site,
            Path = path, Format = "zip", TargetType = "ftp", RemoteUri = url + "/backup.zip",
            Status = "created_remote_synced", CreatedAtUtc = DateTime.UtcNow
        });
        await trust.SetAsync(url, true, Owner, false, area.Iptal);
        await service.VerifyBackupsAsync(area.Iptal);
        Assert.NotEmpty(ftp.Komutlar);
        ftp.Komutlar.Clear();
        await trust.SetAsync(url, false, Owner, false, area.Iptal);
        var verification = await service.VerifyBackupsAsync(area.Iptal);
        Assert.Equal(1, verification.Unreachable);
        Assert.Empty(ftp.Komutlar);
        Assert.Equal("backup.remote_delete_failed", (await Assert.ThrowsAsync<AppException>(() => service.DeleteBackupAsync("nas", area.Iptal))).MessageKey);
        Assert.Empty(ftp.Komutlar);
        Assert.True(Assert.Single(await service.GetBackupsAsync(area.Iptal)).RemoteDeleteFailed);
    }

    private static BackupService Service(BulutAlani area, BackupNetworkTrustService trust, IBackupFtpCommands? ftp = null) => new(
        new SahteSiteServisi(area.SiteKoku), null!, null!, new SahteTercihServisi(Path.Combine(area.Kok, "backups")), null!, area.DosyaDeposu,
        new SahteIstemciFabrikasi(new SahteBulut()), networkTrust: trust,
        runtimeOptions: Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = area.Kok }),
        secretProtector: new SahteKoruyucu(),
        outboundResolver: (_, _) => Task.FromResult(new[] { IPAddress.Parse("192.168.1.120") }))
        { FtpCommands = ftp ?? new SahteFtp() };

    private static async Task Preflight(BackupService service, string type, string url)
    {
        var targetType = typeof(BackupService).GetNestedType("BackupTarget", BindingFlags.NonPublic)!;
        var target = Activator.CreateInstance(targetType, type, new Uri(url), "", "", "");
        try { await (Task)typeof(BackupService).GetMethod("EnsureRemoteTargetAllowedAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(service, new[] { target, CancellationToken.None })!; }
        catch (TargetInvocationException exception) when (exception.InnerException is not null) { throw exception.InnerException; }
    }

    private sealed class Owners : IServerOwnerService
    {
        public Task<Guid?> GetServerOwnerIdAsync(CancellationToken cancellationToken = default) => Task.FromResult<Guid?>(Owner);
        public Task<bool> IsServerOwnerAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult(userId == Owner);
    }
}
