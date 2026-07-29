using AdaPanel.Infrastructure.Dns;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// 2026-07-23 olayının regresyon testleri: DNS servisi "Running" olmasına rağmen yalnız loopback'e
/// bind kalmıştı ve alan adları altı gün boyunca dışarıdan çözülemedi. Karar mantığı (hangi
/// dinleyici kümesi "dışarıya yanıt veriyor" sayılır) burada saf fonksiyon olarak doğrulanıyor.
/// </summary>
public sealed class DnsServerBindingTests
{
    [Fact]
    public void EvaluateBinding_DetectsTheLoopbackOnlyOutage()
    {
        var (bound, missing) = DnsService.EvaluateBinding(
            ["127.0.0.1", "::1", "fe80::1"],
            ["185.7.242.225"]);

        Assert.False(bound);
        Assert.Equal(["185.7.242.225"], missing);
    }

    [Fact]
    public void EvaluateBinding_AcceptsTheHealthyPublicBinding()
    {
        var (bound, missing) = DnsService.EvaluateBinding(
            ["127.0.0.1", "185.7.242.225", "::1"],
            ["185.7.242.225"]);

        Assert.True(bound);
        Assert.Empty(missing);
    }

    [Fact]
    public void EvaluateBinding_TreatsWildcardAsAllAddresses()
    {
        var (bound, missing) = DnsService.EvaluateBinding(
            ["0.0.0.0"],
            ["10.0.0.5", "185.7.242.225"]);

        Assert.True(bound);
        Assert.Empty(missing);
    }

    [Fact]
    public void EvaluateBinding_ReportsPartialCoverageAsBoundButListsMissing()
    {
        var (bound, missing) = DnsService.EvaluateBinding(
            ["10.0.0.5"],
            ["10.0.0.5", "185.7.242.225"]);

        Assert.True(bound);
        Assert.Equal(["185.7.242.225"], missing);
    }

    // NAT/tek arayüz okunamadığı durumda yanlış alarm ÜRETMEMELİ: beklenen liste boşsa
    // loopback dışı herhangi bir adreste dinlemek yeterli sayılır.
    [Fact]
    public void EvaluateBinding_DoesNotFalseAlarmWhenExpectedListIsEmpty()
    {
        Assert.True(DnsService.EvaluateBinding(["10.0.0.5"], []).PubliclyBound);
        Assert.False(DnsService.EvaluateBinding(["127.0.0.1", "::1"], []).PubliclyBound);
    }

    [Fact]
    public void EvaluateBinding_IgnoresBlankEntries()
    {
        var (bound, missing) = DnsService.EvaluateBinding(
            ["", "  ", "185.7.242.225"],
            ["185.7.242.225", ""]);

        Assert.True(bound);
        Assert.Empty(missing);
    }
}
