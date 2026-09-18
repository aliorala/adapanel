using AdaPanel.Infrastructure.Dns;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class DnsAvailabilityProbeTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task One_request_reuses_the_capability_but_installation_invalidates_it(bool initial)
    {
        var calls = 0;
        var probe = new DnsAvailabilityProbe(_ => Task.FromResult(++calls == 1 ? initial : !initial));
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => probe.GetAsync(default)));
        Assert.All(results, result => Assert.Equal(initial, result));
        Assert.Equal(1, calls);
        probe.Invalidate();
        Assert.Equal(!initial, await probe.GetAsync(default));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Failed_probe_is_not_cached_as_dns_absent()
    {
        var calls = 0;
        var probe = new DnsAvailabilityProbe(_ => ++calls == 1
            ? Task.FromException<bool>(new IOException("probe failed")) : Task.FromResult(true));
        await Assert.ThrowsAsync<IOException>(() => probe.GetAsync(default));
        Assert.True(await probe.GetAsync(default));
    }

    [Fact]
    public async Task Canceled_request_stops_before_a_probe_and_does_not_poison_later_requests()
    {
        var calls = 0;
        var probe = new DnsAvailabilityProbe(_ => { calls++; return Task.FromResult(true); });
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.GetAsync(cts.Token));
        Assert.Equal(0, calls);
        Assert.True(await probe.GetAsync(default));
    }
}
