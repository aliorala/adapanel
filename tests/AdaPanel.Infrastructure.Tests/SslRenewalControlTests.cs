using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Ssl;
using AdaPanel.Infrastructure.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>Yenileme kaydini duraklat / surdur / iptal et + sureye gore durum siniflandirmasi.</summary>
public sealed class SslRenewalControlTests : IDisposable
{
    private readonly BulutAlani area = new();
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly AdaPanelDbContext db;
    private readonly string wacsDir = Path.Combine(Path.GetTempPath(), "wacs-rc-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> commands = [];
    private bool cancelOk = true;

    public SslRenewalControlTests()
    {
        connection.Open();
        db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        Directory.CreateDirectory(wacsDir);
        File.WriteAllText(Path.Combine(wacsDir, "wacs.exe"), "x");
        db.InstalledComponents.Add(new AdaPanel.Core.Entities.InstalledComponent
        {
            ComponentId = "win-acme", DisplayName = "win-acme", InstallDir = wacsDir,
            Status = AdaPanel.Core.Enums.ComponentInstallStatus.Installed
        });
        db.SaveChanges();
    }

    public void Dispose()
    {
        db.Dispose(); connection.Dispose(); area.Dispose();
        if (Directory.Exists(wacsDir)) Directory.Delete(wacsDir, true);
    }

    private WinAcmeSslService Create() => new(db, null!, null!, area.DosyaDeposu, null!, NullLogger<WinAcmeSslService>.Instance,
        bindingReader: _ => Task.FromResult<IReadOnlyCollection<IisHttpsBinding>?>([]))
    {
        RenewalCommand = (_, arguments, _) =>
        {
            commands.Add(arguments);
            return Task.FromResult(new CommandResult { ExitCode = cancelOk ? 0 : 1, StandardError = cancelOk ? "" : "boom" });
        },
        RenewalRecordsAsync = (_, _, _) => Task.FromResult<IReadOnlyList<System.Text.Json.JsonElement>>([])
    };

    private Task Seed(params SslCertificateDto[] records) => area.DosyaDeposu.WriteAsync("config/ssl.json", records);

    private static SslCertificateDto Rec(string host, string? renewal, bool paused = false) => new()
    {
        SiteName = "a.com", HostName = host, Port = 443, Thumbprint = "T-" + host, ManagedRenewalId = renewal,
        ManagedRenewalSiteId = 1, Source = "letsencrypt", NotAfterUtc = DateTime.UtcNow.AddDays(60), RenewalPaused = paused
    };

    private async Task<SslCertificateDto[]> Read() =>
        await area.DosyaDeposu.ReadAsync("config/ssl.json", Array.Empty<SslCertificateDto>());

    [Fact]
    public async Task PauseAndResumeToggleTheFlagWithoutRunningWinAcme()
    {
        await Seed(Rec("a.com", "ren-1"));
        var service = Create();

        var paused = await service.SetRenewalStateAsync("a.com", "a.com", 443, "pause");
        Assert.True(paused.RenewalPaused);
        Assert.True(Assert.Single(await Read()).RenewalPaused);

        var resumed = await service.SetRenewalStateAsync("a.com", "a.com", 443, "resume");
        Assert.False(resumed.RenewalPaused);
        Assert.Empty(commands);
    }

    [Fact]
    public async Task CancelRunsWinAcmeCancelVerifiesAndDetachesTheRenewalButKeepsTheCertificateRecord()
    {
        await Seed(Rec("a.com", "ren-1"));
        var updated = await Create().SetRenewalStateAsync("a.com", "a.com", 443, "cancel");

        Assert.Null(updated.ManagedRenewalId);
        Assert.Contains(commands, command => command.Contains("--cancel --id \"ren-1\"", StringComparison.Ordinal));
        var record = Assert.Single(await Read());
        Assert.Null(record.ManagedRenewalId);
    }

    [Fact]
    public async Task AFailedCancelFailsLoudlyAndKeepsTheRenewalId()
    {
        await Seed(Rec("a.com", "ren-1"));
        cancelOk = false;

        var error = await Assert.ThrowsAsync<AppException>(() => Create().SetRenewalStateAsync("a.com", "a.com", 443, "cancel"));

        Assert.Equal("ssl.renewal_cancel_failed", error.MessageKey);
        Assert.Equal("ren-1", Assert.Single(await Read()).ManagedRenewalId);
    }

    [Fact]
    public async Task CancellingASharedRenewalIsRefused()
    {
        await Seed(Rec("a.com", "ren-1"), Rec("www.a.com", "ren-1"));
        var error = await Assert.ThrowsAsync<AppException>(() => Create().SetRenewalStateAsync("a.com", "a.com", 443, "cancel"));
        Assert.Equal("ssl.renewal_shared", error.MessageKey);
        Assert.Empty(commands);
    }

    [Fact]
    public async Task ARecordWithoutAManagedRenewalCannotBeControlled()
    {
        await Seed(Rec("a.com", null));
        var error = await Assert.ThrowsAsync<AppException>(() => Create().SetRenewalStateAsync("a.com", "a.com", 443, "pause"));
        Assert.Equal("ssl.renewal_not_managed", error.MessageKey);
    }

    [Fact]
    public async Task AnUnknownActionIsRejected()
    {
        await Seed(Rec("a.com", "ren-1"));
        var error = await Assert.ThrowsAsync<AppException>(() => Create().SetRenewalStateAsync("a.com", "a.com", 443, "explode"));
        Assert.Equal("ssl.renewal_action_invalid", error.MessageKey);
    }

    [Fact]
    public async Task RenewAllSkipsAPausedRecordAndRenewsTheOthersByIdentifier()
    {
        await Seed(Rec("a.com", "ren-1", paused: true), Rec("b.com", "ren-2"));
        var result = await Create().RenewAllAsync();

        Assert.DoesNotContain(commands, command => command.Contains("ren-1", StringComparison.Ordinal));
        Assert.Contains(commands, command => command.Contains("--renew --id \"ren-2\"", StringComparison.Ordinal));
        Assert.DoesNotContain(commands, command => command == "--renew --baseuri https://acme-v02.api.letsencrypt.org/");
        Assert.Contains("duraklatilmis", result.Output);
    }

    [Fact]
    public async Task RenewAllWithoutPausedRecordsKeepsTheSingleGlobalRenewal()
    {
        await Seed(Rec("a.com", "ren-1"));
        await Create().RenewAllAsync();
        Assert.Contains(commands, command => command == "--renew --baseuri https://acme-v02.api.letsencrypt.org/");
    }

    [Theory]
    [InlineData(-1, "expired")]
    [InlineData(1, "expiring")]
    [InlineData(7, "expiring")]
    [InlineData(20, "renewal_due")]
    [InlineData(30, "renewal_due")]
    [InlineData(31, "valid")]
    [InlineData(90, "valid")]
    public void ExpiryStatusReflectsTheRemainingDays(int daysLeft, string expected)
    {
        var now = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expected, SslExpiry.Classify(now.AddDays(daysLeft).AddMinutes(-1), now));
    }

    [Fact]
    public void ACertificateWithOneDayLeftIsNotShownAsValid()
    {
        var dto = new SslCertificateDto { NotAfterUtc = DateTime.UtcNow.AddDays(1) };
        Assert.NotEqual(SslExpiry.Valid, dto.ExpiryStatus);
    }
}
