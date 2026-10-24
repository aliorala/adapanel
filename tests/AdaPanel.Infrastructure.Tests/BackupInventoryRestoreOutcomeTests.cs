using System.Reflection;
using System.Text.Json;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Ftp;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Backup;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class BackupInventoryRestoreOutcomeTests
{
    [Theory]
    [InlineData("inventory", true)]
    [InlineData("inventory", false)]
    [InlineData("create", true)]
    [InlineData("create", false)]
    [InlineData("binding", true)]
    [InlineData("binding", false)]
    [InlineData("ftp", true)]
    [InlineData("ftp", false)]
    public async Task CallerCancellationStopsAtTheCurrentStage(string stage, bool dependencyThrows)
    {
        using var f = new Fixture { CancelAt = stage, DependencyThrows = dependencyThrows };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stage == "ftp" ? f.RestoreFtp() : f.RestoreSites());
        Assert.Equal(stage, f.Calls.Last());
        Assert.Equal(1, f.Calls.Count(call => call == stage));
        Assert.Empty(f.Warnings);
        Assert.DoesNotContain(f.Report, line => line.Contains("yeniden kuruldu") || line.Contains(": hazır"));
    }

    [Fact]
    public async Task FailedExtraBindingIsNotIncludedInRestoredCount()
    {
        using var f = new Fixture { FailFirstBinding = true };
        await f.RestoreSites();
        Assert.Equal(2, f.Calls.Count(call => call == "binding"));
        Assert.Equal("backup.restore_site_failed", Assert.Single(f.Warnings).MessageKey);
        Assert.Contains(f.Report, line => line.Contains("yeniden kuruldu (2 http bağlama)"));
        Assert.DoesNotContain(f.Report, line => line.Contains("yeniden kuruldu (3 http bağlama)"));
    }

    [Fact]
    public async Task OrdinaryFtpFailureStillAllowsTheNextAccountAndRemainsReported()
    {
        using var f = new Fixture { FailFirstFtp = true };
        await f.RestoreFtp();
        Assert.Equal(new[] { "ftp", "ftp" }, f.Calls);
        Assert.Equal("backup.restore_ftp_failed", Assert.Single(f.Warnings).MessageKey);
        Assert.Contains(f.Report, line => line.Contains("FTP second: hazır"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreCancelledRestoreCannotStartInventoryWork(bool ftp)
    {
        using var f = new Fixture(); f.Cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ftp ? f.RestoreFtp() : f.RestoreSites());
        Assert.Empty(f.Calls);
        Assert.Empty(f.Warnings);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly BulutAlani area = new();
        public readonly CancellationTokenSource Cancellation = new(TimeSpan.FromSeconds(30));
        public readonly List<string> Calls = [], Report = [];
        public readonly List<BackupRestoreWarningDto> Warnings = [];
        public string CancelAt = "";
        public bool DependencyThrows, FailFirstBinding, FailFirstFtp;
        private readonly BackupService service;
        public Fixture()
        {
            var site = new SiteDto { Name = "restore.example.test", PhysicalPath = area.SiteKoku, Bindings =
                Enumerable.Range(0, 3).Select(index => new SiteBindingDto
                    { HostName = $"host{index}.example.test", Protocol = "http", Port = 80 }).ToArray() };
            Directory.CreateDirectory(Path.Combine(area.Kok, "iis"));
            Directory.CreateDirectory(Path.Combine(area.Kok, "config"));
            File.WriteAllText(Path.Combine(area.Kok, "iis", "sites.json"), JsonSerializer.Serialize(new[] { site }));
            File.WriteAllText(Path.Combine(area.Kok, "config", "ftp-users.json"), JsonSerializer.Serialize(
                new[] { "first", "second" }.Select(name => new { UserName = name, HomePath = area.SiteKoku, SiteName = site.Name })));
            var sites = Fake<IIisSiteService>((method, _) =>
            {
                if (method.Name == nameof(IIisSiteService.GetSitesAsync))
                { Enter("inventory"); return Task.FromResult<IReadOnlyCollection<SiteDto>>([]); }
                if (method.Name == nameof(IIisSiteService.CreateSiteAsync))
                { Enter("create"); return Task.FromResult(site); }
                Assert.Equal(nameof(IIisSiteService.AddBindingAsync), method.Name);
                Enter("binding");
                if (FailFirstBinding && Calls.Count(call => call == "binding") == 1) throw new IOException("Fixture binding failure");
                return Task.FromResult(site);
            });
            var ftp = Fake<IFtpService>((method, _) =>
            {
                Assert.Equal(nameof(IFtpService.CreateFtpUserAsync), method.Name);
                Enter("ftp");
                if (FailFirstFtp && Calls.Count(call => call == "ftp") == 1) throw new IOException("Fixture FTP failure");
                return Task.FromResult(new FtpUserDto());
            });
            service = new BackupService(sites, null!, null!, null!, ftp, area.DosyaDeposu, null!);
        }
        private void Enter(string stage)
        {
            Calls.Add(stage);
            if (CancelAt != stage) return;
            Cancellation.Cancel();
            if (DependencyThrows) throw new OperationCanceledException(Cancellation.Token);
        }
        public Task RestoreSites() => service.TryRestoreIisSitesAsync(area.Kok, Report, Warnings, Cancellation.Token);
        public Task RestoreFtp() => service.TryRestoreFtpUsersAsync(area.Kok, Report, Warnings, Cancellation.Token);
        public void Dispose() { Cancellation.Dispose(); area.Dispose(); }
    }
    private static T Fake<T>(Func<MethodInfo, object?[], object?> handler) where T : class
    { var result = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)result).Handler = handler; return result; }
    public class Proxy : DispatchProxy
    { public Func<MethodInfo, object?[], object?> Handler = null!; protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args ?? []); }
}
