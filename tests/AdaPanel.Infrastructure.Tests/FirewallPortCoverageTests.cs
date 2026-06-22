using AdaPanel.Infrastructure.Firewall;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class FirewallPortCoverageTests
{
    [Fact]
    public void ParseAllowCoverage_HandlesSinglePortsRangesAndProtocols()
    {
        var rows = new (string?, string?)[]
        {
            ("TCP", "443"),
            ("TCP", "5000-5100"),
            ("Any", "25"),     // Any protokol TCP olarak sayılır
            ("UDP", "53"),     // TCP değil -> yok sayılır
            ("TCP", "RPC")     // sayısal değil -> yok sayılır
        };

        var (coversAll, ranges) = FirewallService.ParseAllowCoverage(rows);

        Assert.False(coversAll);
        Assert.True(FirewallService.IsPortCovered(443, coversAll, ranges));
        Assert.True(FirewallService.IsPortCovered(25, coversAll, ranges));
        Assert.True(FirewallService.IsPortCovered(5050, coversAll, ranges));   // aralık içi
        Assert.False(FirewallService.IsPortCovered(5200, coversAll, ranges));  // aralık dışı
        Assert.False(FirewallService.IsPortCovered(53, coversAll, ranges));    // UDP yok sayıldı
        Assert.False(FirewallService.IsPortCovered(80, coversAll, ranges));
    }

    [Fact]
    public void ParseAllowCoverage_AnyLocalPortCoversEverything()
    {
        var rows = new (string?, string?)[] { ("TCP", "Any") };

        var (coversAll, ranges) = FirewallService.ParseAllowCoverage(rows);

        Assert.True(coversAll);
        Assert.True(FirewallService.IsPortCovered(1, coversAll, ranges));
        Assert.True(FirewallService.IsPortCovered(65535, coversAll, ranges));
    }

    [Fact]
    public void ParseAllowCoverage_MultiPortTokensAndNullsAreSafe()
    {
        var rows = new (string?, string?)[]
        {
            ("TCP", "25,143 587"),   // virgül + boşluk ile ayrılmış çoklu port
            (null, null),            // null satır -> patlamamalı
            ("TCP", null)            // null port -> patlamamalı
        };

        var (coversAll, ranges) = FirewallService.ParseAllowCoverage(rows);

        Assert.False(coversAll);
        Assert.True(FirewallService.IsPortCovered(25, coversAll, ranges));
        Assert.True(FirewallService.IsPortCovered(143, coversAll, ranges));
        Assert.True(FirewallService.IsPortCovered(587, coversAll, ranges));
        Assert.False(FirewallService.IsPortCovered(465, coversAll, ranges));
    }
}
