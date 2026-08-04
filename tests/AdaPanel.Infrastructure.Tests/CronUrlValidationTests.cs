using AdaPanel.Infrastructure.Scheduler;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// URL gorevinin adres denetimi.
/// <para>
/// KENDI ACTIGIM HATA: kullaniciya yardim etmek icin "buraya dosya yolu yazmissiniz" mesajini
/// ekledim ve denetimi ONCE "dosya yolu mu" diye yapmaya koydum. Oysa HER URL egik cizgi icerir ve
/// egik cizgi Windows'ta da bir dizin ayiricisidir -- yani gecerli https adresleri de reddedildi.
/// Yardim etmek icin eklenen mesaj, calisan ozelligi bozdu.
/// </para>
/// <para>
/// Ders: once GERCEK OLCUM (bu, http/https bir adres mi), yalnizca degilse TAHMIN (dosya yoluna mi
/// benziyor). Bu dosya o sirayi kilitler.
/// </para>
/// </summary>
public sealed class CronUrlValidationTests
{
    [Theory]
    [InlineData("https://alioral.com.tr/iptv/bin/imdb-cron.php")]
    [InlineData("http://ornek.test/cron")]
    [InlineData("https://ornek.test/cron/calistir?key=abc&x=1")]
    [InlineData("https://ornek.test:8443/a/b/c.php")]
    [InlineData("https://192.168.1.10/cron.php")]
    public void RealAddresses_AreAccepted(string adres)
    {
        Assert.True(SchedulerService.IsAcceptableTaskUrl(adres, out var kod), $"reddedildi: {kod}");
    }

    // Dosya yolu yazildiginda GENEL hata degil, dogru secenege yonlendiren mesaj verilmeli.
    [Theory]
    [InlineData(@"C:\inetpub\vhosts\ornek.test\httpdocs\iptv\bin\imdb-cron.php")]
    [InlineData(@"c:/site/cron.php")]
    [InlineData("\\\\sunucu\\paylasim\\cron.php")]
    public void WindowsPaths_GetTheHelpfulMessageInsteadOfGenericInvalidUrl(string yol)
    {
        Assert.False(SchedulerService.IsAcceptableTaskUrl(yol, out var kod));
        Assert.Equal("cron.url_is_a_file_path", kod);
    }

    // Ne adres ne dosya yolu: genel hata dogru olan.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ornek.test/cron")]
    [InlineData("ftp://ornek.test/cron")]
    [InlineData("file:///C:/cron.php")]
    public void NeitherAnAddressNorAPath_FallsBackToTheGenericError(string deger)
    {
        Assert.False(SchedulerService.IsAcceptableTaskUrl(deger, out var kod));
        Assert.Equal("cron.invalid_url", kod);
    }

    // Egik cizgi TEK BASINA dosya yolu isareti degildir -- hatanin kaynagi tam olarak buydu.
    [Fact]
    public void ASlashAloneDoesNotMakeSomethingAWindowsPath()
    {
        Assert.False(SchedulerService.LooksLikeWindowsPath("https://ornek.test/a/b/c.php"));
        Assert.True(SchedulerService.LooksLikeWindowsPath(@"C:\a\b\c.php"));
    }
}
