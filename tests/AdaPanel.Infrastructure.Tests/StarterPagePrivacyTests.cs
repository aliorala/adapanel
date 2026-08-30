using System.Reflection;
using AdaPanel.Infrastructure.Iis;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// PANELİN AÇTIĞI SİTELERE KOYDUĞU BAŞLANGIÇ SAYFASI SIR SIZDIRMAZ.
///
/// <para>
/// <b>Neden var — ölçülmüş bilgi ifşası.</b> Panel her yeni PHP sitesine <c>php-info.php</c>
/// yazıyordu ve o dosya <c>?full=1</c> ile <b>kimlik doğrulaması olmadan</b> <c>phpinfo()</c>
/// basıyordu. Site DNS'e bağlandığı anda internete açıktır, yol tahmin edilebilir ve panelin
/// açtığı HER sitede aynıdır.
/// </para>
/// <para>
/// <c>phpinfo()</c> çıktısı mutlak yolları, yüklü eklentileri, php.ini ayarlarının tamamını,
/// <b>ortam değişkenlerini</b> (içinde veritabanı parolası ya da API anahtarı olabilir) ve
/// sunucu sürümlerini döker. Bir sunucuya bakan saldırganın ilk aradığı şeydir.
/// </para>
/// <para>
/// <b>Test üretilen METNİ ölçer, kaynağı değil.</b> Kaynak taraması "phpinfo" kelimesini bu
/// dosyanın kendi gerekçe yorumunda da bulurdu; üstelik başlangıç sayfası yarın başka bir
/// yöntemle üretilse tarama sessizce boşa düşerdi. Ölçülen şey siteye GERÇEKTEN yazılan içerik.
/// </para>
/// </summary>
public sealed class StarterPagePrivacyTests
{
    private static string PhpBaslangicSayfasi()
    {
        // `private static` üretici: davranış dışarıdan yalnız site oluşturma akışıyla tetiklenir
        // ve o akış IIS ister. Ölçülmek istenen şey üretilen METİN olduğu için doğrudan okunur.
        var metot = typeof(SiteProvisioningService).GetMethod(
            "BuildPhpWelcomePage",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(metot);
        var icerik = metot!.Invoke(null, null) as string;
        Assert.False(string.IsNullOrWhiteSpace(icerik), "PHP baslangic sayfasi bos uretildi.");
        return icerik!;
    }

    [Fact]
    public void Baslangic_sayfasi_phpinfo_CAGIRMAZ()
    {
        var icerik = PhpBaslangicSayfasi();

        Assert.DoesNotContain("phpinfo(", icerik, StringComparison.OrdinalIgnoreCase);

        // Tetikleyici de gitmeli: `?full=1` bağlantısı ve onu okuyan koşul.
        Assert.DoesNotContain("full=1", icerik, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$_GET['full']", icerik, StringComparison.Ordinal);
    }

    /// <summary>
    /// SAYFA YİNE DE İŞİNİ YAPMALI — fazla temizlik de bir hatadır.
    /// </summary>
    /// <remarks>
    /// Amaç sayfayı kaldırmak değildi: "PHP çalışıyor mu" sorusunu cevaplamak gerçek bir
    /// ihtiyaç ve sürüm bilgisi tek başına bu sınıfta değil. Bu test, ifşayı kapatırken
    /// özelliğin de kapatılmadığını ölçer.
    /// </remarks>
    [Fact]
    public void Baslangic_sayfasi_hala_PHP_calisiyor_diyor()
    {
        var icerik = PhpBaslangicSayfasi();

        Assert.Contains("phpversion", icerik, StringComparison.Ordinal);
        Assert.Contains("PHP çalışıyor", icerik, StringComparison.Ordinal);

        // Sürüm KAÇIŞLI basılmalı: kendi başlangıç sayfamız da XSS taşıyıcısı olmamalı.
        Assert.Contains("htmlspecialchars($v)", icerik, StringComparison.Ordinal);
    }
}
