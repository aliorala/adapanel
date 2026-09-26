using AdaPanel.Infrastructure.Firewall;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class FirewallPortCoverageTests
{
    [Theory]
    [InlineData("ADAPANEL-KURAL=1\n{\"Protocol\":\"TCP\",\"LocalPort\":\"443\"}")]
    [InlineData("\r\nADAPANEL-KURAL=2\r\n[{\"Protocol\":\"TCP\",\"LocalPort\":\"443\"},{\"Protocol\":\"UDP\",\"LocalPort\":\"53\"}]")]
    public void Marked_PowerShell_output_is_read_as_measured_coverage(string output)
    {
        var (all, ranges) = FirewallService.ParseAllowCoverageOutput(output);
        Assert.NotNull(ranges);
        Assert.False(all);
        Assert.True(FirewallService.IsPortCovered(443, all, ranges));
        Assert.False(FirewallService.IsPortCovered(80, all, ranges));
        Assert.False(FirewallService.IsPortCovered(53, all, ranges));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ADAPANEL-KURAL=0\n")]
    [InlineData("ADAPANEL-KURAL=4\n[]")]
    [InlineData("ADAPANEL-KURAL=invalid\n[]")]
    [InlineData("[{\"Protocol\":\"TCP\",\"LocalPort\":\"Any\"}]")]
    public void Missing_or_empty_measurement_stays_unknown(string output)
    {
        Assert.Null(FirewallService.ParseAllowCoverageOutput(output).Ranges);
    }

    [Fact]
    public void Corrupt_measurement_is_not_assumed_to_be_allowed()
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => FirewallService.ParseAllowCoverageOutput("ADAPANEL-KURAL=1\ninvalid"));
    }

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
