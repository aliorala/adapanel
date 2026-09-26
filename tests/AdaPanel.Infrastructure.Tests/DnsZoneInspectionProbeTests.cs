using AdaPanel.Infrastructure.Dns;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class DnsZoneInspectionProbeTests
{
    [Fact]
    public async Task Native_success_does_not_start_PowerShell()
    {
        var result = await DnsZoneInspectionProbe.ReadAsync(["one.test"], default,
            (_, _) => throw new Exception("Unnecessary PowerShell"),
            native: (_, _) => Task.FromResult<IReadOnlyDictionary<string, DnsZoneInspectionProbe.Result>>(
                new Dictionary<string, DnsZoneInspectionProbe.Result> { ["one.test"] = new() { Name = "one.test", Available = true, Keys = ["MX|@"] } }));
        Assert.Equal("MX|@", Assert.Single(result["one.test"].Keys!));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Native_failure_or_incomplete_result_uses_verified_fallback(bool throws)
    {
        var calls = 0;
        var result = await DnsZoneInspectionProbe.ReadAsync(["one.test"], default,
            (_, _) => { calls++; return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "[{\"name\":\"one.test\",\"available\":true,\"keys\":[\"A|@\"]}]" }); },
            native: (_, _) => throws ? throw new IOException("Provider unavailable")
                : Task.FromResult<IReadOnlyDictionary<string, DnsZoneInspectionProbe.Result>>(new Dictionary<string, DnsZoneInspectionProbe.Result>()));
        Assert.Equal(1, calls);
        Assert.Equal("A|@", Assert.Single(result["one.test"].Keys!));
    }

    [Fact]
    public async Task Caller_cancellation_in_native_path_does_not_start_fallback()
    {
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DnsZoneInspectionProbe.ReadAsync(["one.test"], cts.Token,
            (_, _) => throw new Exception("Must not fall back"), native: (_, token) =>
            {
                cts.Cancel();
                return Task.FromCanceled<IReadOnlyDictionary<string, DnsZoneInspectionProbe.Result>>(token);
            }));
    }

    [Fact]
    public async Task Actual_powershell_preserves_single_empty_and_missing_zone_results()
    {
        const string stubs = """
            function Get-DnsServerZone { [pscustomobject]@{ ZoneName='one.test'; ZoneType='Primary' } }
            function Get-DnsServerResourceRecord { param($ZoneName) }
            """;
        var single = await DnsZoneInspectionProbe.ReadAsync(["one.test"], default,
            (script, token) => ProcessRunner.RunPowerShellStrictAsync(stubs + "\n" + script, token));
        Assert.Equal("Primary", single["one.test"].ZoneType);
        Assert.Empty(single["one.test"].Keys!);
        var missing = await DnsZoneInspectionProbe.ReadAsync(["missing.test"], default,
            (script, token) => ProcessRunner.RunPowerShellStrictAsync(stubs + "\n" + script, token));
        Assert.True(missing["missing.test"].Available);
        Assert.Empty(missing["missing.test"].ZoneType);
        Assert.Empty(missing["missing.test"].Error);
        Assert.Empty(missing["missing.test"].Keys!);
    }

    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DnsZoneInspectionProbe.ReadAsync(["one.test"], cancellation.Token,
                (_, token) => Task.FromCanceled<CommandResult>(token)));
    }

    [Fact]
    public async Task All_zones_are_read_in_one_process_and_partial_errors_remain_visible()
    {
        var calls = 0;
        var result = await DnsZoneInspectionProbe.ReadAsync(["one.test", "two.test", "ONE.test"], default,
            (script, token) =>
            {
                calls++;
                Assert.Contains("'one.test','two.test'", script);
                return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = """
                    [{"name":"one.test","available":true,"zoneType":"Primary","keys":["MX|@"],"error":""},
                     {"name":"two.test","available":true,"zoneType":"","keys":null,"error":"Access denied"}]
                    """ });
            });
        Assert.Equal(1, calls);
        Assert.Equal("MX|@", Assert.Single(result["one.test"].Keys!));
        Assert.Null(result["two.test"].Keys);
        Assert.Equal("Access denied", result["two.test"].Error);
    }

    [Fact]
    public async Task Incomplete_result_is_not_interpreted_as_a_healthy_zone()
    {
        await Assert.ThrowsAsync<IOException>(() => DnsZoneInspectionProbe.ReadAsync(["one.test"], default,
            (_, _) => Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "[]" })));
    }

    [Fact]
    public async Task Empty_inventory_does_not_start_a_shell()
    {
        var result = await DnsZoneInspectionProbe.ReadAsync([], default,
            (_, _) => throw new InvalidOperationException("must not start"));
        Assert.Empty(result);
    }

    [Fact]
    public void Zone_names_are_escaped_as_literals()
    {
        Assert.Contains("'one''; throw 1; ''.test'", DnsZoneInspectionProbe.BuildScript(["one'; throw 1; '.test"]));
    }
}
