using AdaPanel.Infrastructure.Firewall;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// "Olcemedik" ile "kapali" AYRI seylerdir.
/// <para>
/// CANLI OLAY: panel "Guvenlik duvarinda gerekli bir port kapali" diye BES ayri uyari verdi
/// (80, 443, 25, 143, 587). Disaridan olculdu: 80, 443, 143, 587 -- hepsi ERISILEBILIR. Yani
/// uyarilarin tamami yanlis alarmdi.
/// </para>
/// <para>
/// Sebep kodun kendi yorumunda yaziyordu: "Hata olursa kapsam bos doner (engelli gorunur)".
/// Yani olcum basarisiz oldugunda panel butun portlari kapali sayiyordu. Olcememeyi sorun
/// saymak, kullaniciyi olmayan bir sorunu kovalamaya iter ve -- daha kotusu -- GERCEK uyarilara
/// olan guveni yok eder.
/// </para>
/// </summary>
public sealed class FirewallUnknownStateTests
{
    /// <summary>
    /// Kapsam null ise OLCUM YAPILAMAMISTIR. Bu durumda port "kapali" sayilmaz.
    /// </summary>
    [Theory]
    [InlineData(80)]
    [InlineData(443)]
    [InlineData(25)]
    [InlineData(143)]
    [InlineData(587)]
    public void WhenTheMeasurementFailed_NoPortIsReportedAsBlocked(int port)
    {
        Assert.True(FirewallService.IsPortCovered(port, coversAllTcp: false, ranges: null));
    }

    // GERCEKTEN olculduyse ve port kapsam disindaysa uyari VERILIR -- duzeltme, gercek
    // bulgulari susturmamali.
    [Fact]
    public void WhenTheMeasurementSucceeded_APortOutsideTheRangesIsStillReportedAsBlocked()
    {
        var kapsam = new List<(int Lo, int Hi)> { (80, 80), (443, 443) };

        Assert.True(FirewallService.IsPortCovered(80, false, kapsam));
        Assert.False(FirewallService.IsPortCovered(587, false, kapsam));
    }

    [Fact]
    public void ARuleCoveringAllTcpPorts_CoversEverything()
    {
        Assert.True(FirewallService.IsPortCovered(587, coversAllTcp: true, ranges: new List<(int, int)>()));
    }

    // Aralik kurallari (ornegin FTP pasif 50000-51000) dogru yorumlanmali.
    [Fact]
    public void RangeRulesCoverEveryPortInside()
    {
        var kapsam = new List<(int Lo, int Hi)> { (50000, 51000) };

        Assert.True(FirewallService.IsPortCovered(50500, false, kapsam));
        Assert.False(FirewallService.IsPortCovered(49999, false, kapsam));
    }
}
