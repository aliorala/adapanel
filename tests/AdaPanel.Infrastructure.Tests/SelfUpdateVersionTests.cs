using AdaPanel.Infrastructure.SelfUpdate;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Sürüm karşılaştırması ARA SÜRÜM GEREKTİRMEZ: kurulum paketi tam kurulumdur, fark yaması değildir
/// ve güncelleme kontrolü GitHub'ın "latest" sürümünü sorar. 0.2.27'de kalmış bir sunucu, 0.2.28'i
/// hiç kurmadan doğrudan 0.2.29'a geçebilmelidir.
///
/// Karşılaştırmanın SAYISAL olması şart: metin karşılaştırması "0.2.9" > "0.2.10" derdi ve iki
/// haneli sürüme geçildiği anda tüm sunucular güncellemeyi görmez olurdu.
/// </summary>
public sealed class SelfUpdateVersionTests
{
    [Theory]
    [InlineData("0.2.27", "0.2.29")]   // arada 0.2.28 atlanıyor
    [InlineData("0.2.20", "0.2.29")]
    [InlineData("0.2.28", "0.2.29")]
    [InlineData("0.1.99", "0.2.0")]
    [InlineData("0.2.9", "0.2.10")]    // metin karşılaştırmasında YANLIŞ sonuç verirdi
    public void IsNewerVersion_OffersTheLatestRelease_EvenWhenVersionsWereSkipped(string current, string latest)
    {
        Assert.True(SelfUpdateService.IsNewerVersion(current, latest));
    }

    // Aynı ya da daha eski sürüm için güncelleme ÖNERİLMEZ: yoksa panel her açılışta kendini
    // yeniden kurmaya çalışırdı.
    [Theory]
    [InlineData("0.2.29", "0.2.29")]
    [InlineData("0.2.29", "0.2.28")]
    [InlineData("0.3.0", "0.2.29")]
    [InlineData("0.2.29", "")]
    public void IsNewerVersion_IsFalse_ForSameOrOlderReleases(string current, string latest)
    {
        Assert.False(SelfUpdateService.IsNewerVersion(current, latest));
    }

    // Sürüm etiketi ön-sürüm/derleme eki taşıyabilir ("0.2.29-beta", "0.2.29+1"); karşılaştırma
    // sayısal çekirdek üzerinden yapılmalı.
    [Theory]
    [InlineData("0.2.27", "0.2.29-beta1", true)]
    [InlineData("0.2.29", "0.2.29+build7", false)]
    public void IsNewerVersion_IgnoresPreReleaseAndBuildSuffixes(string current, string latest, bool expected)
    {
        Assert.Equal(expected, SelfUpdateService.IsNewerVersion(current, latest));
    }
}
