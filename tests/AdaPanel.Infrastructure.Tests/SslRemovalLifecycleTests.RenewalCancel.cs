using AdaPanel.Core.Dtos.Ssl;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed partial class SslRemovalLifecycleTests
{
    private static SslCertificateDto Managed(Fixture f, string host, string? renewalId) => new()
    {
        SiteName = f.Name, HostName = host, Port = 443, Thumbprint = "fixture-certificate",
        Source = "letsencrypt", ManagedRenewalId = renewalId, ManagedRenewalSiteId = 1,
        NotAfterUtc = DateTime.UtcNow.AddDays(60)
    };

    [Fact]
    public async Task RemovingASingleBindingCancelsItsManagedRenewalAndVerifiesTheRemoval()
    {
        using var f = await Fixture.Create("");
        await f.Area.DosyaDeposu.WriteAsync("config/ssl.json", new[] { Managed(f, f.Name, "ren-1") }, f.Token);

        var result = await f.Remove();

        Assert.Equal("cancelled", result.RenewalAction);
        Assert.Contains(f.RenewalCommands, arguments => arguments.Contains("--cancel --id \"ren-1\"", StringComparison.Ordinal));
        Assert.Equal(1, f.BindingDeletes);
    }

    [Fact]
    public async Task ACancelCommandThatSucceedsButLeavesTheRenewalListedIsReportedAsFailed()
    {
        using var f = await Fixture.Create("");
        await f.Area.DosyaDeposu.WriteAsync("config/ssl.json", new[] { Managed(f, f.Name, "ren-1") }, f.Token);
        f.RenewalStillListed = true;

        var result = await f.Remove();

        Assert.Equal("cancel_failed", result.RenewalAction);
        // Ham Turkce metin degil, mesaj ANAHTARI doner.
        Assert.Equal("ssl.renewal_cancel_failed", result.RenewalDetail);
        Assert.True(result.BindingRemoved);
    }

    [Fact]
    public async Task ARenewalSharedWithAnotherRecordIsNotCancelled()
    {
        using var f = await Fixture.Create("");
        await f.Area.DosyaDeposu.WriteAsync("config/ssl.json",
            new[] { Managed(f, f.Name, "ren-1"), Managed(f, "other." + f.Name, "ren-1") }, f.Token);

        var result = await f.Remove();

        Assert.Equal("kept_shared", result.RenewalAction);
        Assert.Equal("ssl.renewal_shared", result.RenewalDetail);
        Assert.Empty(f.RenewalCommands);
    }

    [Fact]
    public async Task ARecordWithoutAManagedRenewalNeverRunsWinAcme()
    {
        using var f = await Fixture.Create("");
        var result = await f.Remove();
        Assert.Equal("none", result.RenewalAction);
        Assert.Empty(f.RenewalCommands);
    }
}
