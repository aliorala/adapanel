using AdaPanel.Infrastructure.Dns;
using AdaPanel.Infrastructure.Firewall;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class WindowsDnsInspectionTests
{
    [Theory]
    [InlineData(5)]
    [InlineData(-1)]
    public void Provider_failure_is_not_accepted_as_an_empty_inventory(object? code)
        => Assert.Throws<IOException>(() => WindowsDnsInspection.CheckResultCode(code));

    [Theory]
    [InlineData(null)]
    [InlineData(0u)]
    public void Successful_provider_result_is_accepted(object? code) => WindowsDnsInspection.CheckResultCode(code);

    [Fact]
    public async Task Cancellation_does_not_start_native_queries()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WindowsDnsInspection.ReadAsync(["example.test"], cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WindowsDnsInspection.ReadPrimaryZoneCountAsync(cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WindowsFirewallInspection.ReadAllowCoverageAsync(cts.Token));
    }

    [Fact]
    public void Native_firewall_wildcard_is_an_all_port_rule()
    {
        var (all, _) = FirewallService.ParseAllowCoverage([("TCP", WindowsFirewallInspection.NormalizePorts("*"))]);
        Assert.True(all);
    }
}
