using System.Text.RegularExpressions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Site güvenlik duvarı kuralı: çift kayıt olmaz, site silinince kural da gider.
/// </summary>
/// <remarks>
/// <para>
/// <b>İkisi de CANLI KURULUMDA ölçüldü</b> (0.8.20, yükseltilmiş <c>Get-NetFirewallRule</c>):
/// <c>AdaPanel Site AdaPanel.WebDb.PhpMyAdmin TCP 8896</c> Windows'ta İKİ KEZ kayıtlıydı, ve
/// aylar önce silinmiş <c>alioral.xyz</c> sitesinin <c>TCP 80</c> kuralı hâlâ duruyordu.
/// </para>
/// <para>
/// Sebep: <c>EnsureFirewallRuleAsync</c> doğrudan <c>netsh ... add rule</c> çağırıyordu.
/// <c>netsh add</c> aynı adlı kuralı GÜNCELLEMEZ, yenisini EKLER — metodun adı "ensure",
/// davranışı "append"ti. Silme yolunda ise güvenlik duvarı hiç geçmiyordu.
/// </para>
/// <para>
/// Doğru kalıp deponun kendi <c>FirewallService</c>'inde zaten vardı (önce
/// <c>Remove-NetFirewallRule</c>, sonra <c>New-NetFirewallRule</c>); bu kardeş yola
/// uygulanmamıştı — bu depoda tekrar eden sınıf.
/// </para>
/// </remarks>
public sealed class SiteGuvenlikDuvariKuraliTests
{
    /// <summary>
    /// Kural adı TEK KAYNAKTAN gelir; ekleme ve silme aynı adı üretmek zorundadır.
    /// </summary>
    /// <remarks>
    /// İkinci bir nüsha, adların sessizce ayrışmasına ve kuralın SİLİNEMEZ hâle gelmesine yol
    /// açardı — silme, var olmayan bir adı arardı ve hiçbir şey yapmadan "başarılı" dönerdi.
    /// </remarks>
    [Theory]
    [InlineData("ornek.com", 80, "AdaPanel Site ornek.com TCP 80")]
    [InlineData("bayi-denetim.test", 8099, "AdaPanel Site bayi-denetim.test TCP 8099")]
    [InlineData("AdaPanel.WebDb.PhpMyAdmin", 8896, "AdaPanel Site AdaPanel.WebDb.PhpMyAdmin TCP 8896")]
    public void Kural_adi_beklenen_bicimde(string site, int port, string beklenen)
    {
        Assert.Equal(beklenen, Cagir(site, port));
    }

    /// <summary>
    /// Güvensiz karakterler tireye çevrilir — <c>netsh</c> argümanını bozmasın.
    /// </summary>
    [Fact]
    public void Guvensiz_karakterler_temizlenir()
    {
        var ad = Cagir("kotu\" ad;test", 80);

        Assert.DoesNotContain("\"", ad, StringComparison.Ordinal);
        Assert.DoesNotContain(";", ad, StringComparison.Ordinal);
        Assert.StartsWith("AdaPanel Site ", ad, StringComparison.Ordinal);
        Assert.EndsWith(" TCP 80", ad, StringComparison.Ordinal);
    }

    /// <summary>
    /// EKLEME İDEMPOTENT: önce siler, sonra ekler.
    /// </summary>
    /// <remarks>
    /// Kaynak taraması burada BİLEREK kullanılıyor ve gerekçesi şu: ölçülecek şey bir dış aracın
    /// (<c>netsh</c>) çağrı SIRASI, ve bu makinede kural oluşturmadan sınanamaz — oluşturulan bir
    /// kuralı silmek de bu oturumun "hiçbir şeyi silme" kuralına girer. Yasak bu yüzden ŞEKLE
    /// konuyor: aynı metotta <c>delete rule</c>, <c>add rule</c>'dan ÖNCE geçmeli.
    /// </remarks>
    [Fact]
    public void Ekleme_once_siler_sonra_ekler()
    {
        var govde = MetotGovdesi("EnsureFirewallRuleAsync");

        var sil = govde.IndexOf("firewall delete rule", StringComparison.Ordinal);
        var ekle = govde.IndexOf("firewall add rule", StringComparison.Ordinal);

        Assert.True(sil >= 0, "silme cagrisi yok -- netsh add AYNI ADLI KURALI COGALTIR");
        Assert.True(ekle >= 0, "ekleme cagrisi yok");
        Assert.True(sil < ekle, "silme, eklemeden SONRA geliyor -- kural yine cogalir");
    }

    /// <summary>
    /// SİTE SİLİNİNCE kural da kaldırılır.
    /// </summary>
    [Fact]
    public void Site_silme_kurali_kaldirir()
    {
        var govde = MetotGovdesi("DeleteSiteAsync");

        Assert.Contains("RemoveFirewallRulesAsync", govde, StringComparison.Ordinal);
    }

    /// <summary>
    /// Portlar site KALDIRILMADAN ÖNCE okunmalı.
    /// </summary>
    /// <remarks>
    /// FAZLA KAPATMA DEĞİL, SIRA NÖBETÇİSİ: kural kaldırma eklenip portlar site silindikten sonra
    /// okunsaydı liste hep BOŞ olurdu ve düzeltme hiçbir şey yapmadan "yapıldı" görünürdü — yani
    /// tam da kapatmaya çalıştığımız sınıfın yeni bir örneği doğardı.
    /// </remarks>
    [Fact]
    public void Portlar_site_kaldirilmadan_ONCE_okunur()
    {
        var govde = MetotGovdesi("DeleteSiteAsync");

        var oku = govde.IndexOf("kuralPortlari.AddRange", StringComparison.Ordinal);
        var kaldir = govde.IndexOf("InvokeMethod(sites, \"Remove\", site)", StringComparison.Ordinal);

        Assert.True(oku >= 0, "portlar hic okunmuyor");
        Assert.True(kaldir >= 0, "site kaldirma cagrisi bulunamadi");
        Assert.True(oku < kaldir, "portlar site KALDIRILDIKTAN sonra okunuyor -- liste hep bos olur");
    }

    // --- duzenek ---

    private static string Cagir(string siteName, int port)
    {
        // Sinif internal; testler InternalsVisibleTo ile eriseceginden dogrudan cagriliyor.
        var tur = typeof(AdaPanel.Infrastructure.Iis.SiteProvisioningService).Assembly
            .GetType("AdaPanel.Infrastructure.Iis.SiteFirewallRuleName", throwOnError: true)!;
        return (string)tur.GetMethod("For")!.Invoke(null, [siteName, port])!;
    }

    private static string MetotGovdesi(string metotAdi)
    {
        var kaynak = File.ReadAllText(Path.Combine(
            KaynakKoku(), "src", "AdaPanel.Infrastructure", "Iis", "WebAdministrationSiteService.cs"));

        // BILDIRIMI ara, CAGRIYI degil. Ilk yazimim " {ad}(" ariyordu ve `await Ensure...(` cagri
        // yerini yakaladi -- cagri dosyada bildirimden ONCE geciyor, yani yanlis bolge okundu ve
        // test DOGRU kodu kirmizi yakti. Bu oturumda kaynak tarayan testin sekizinci yanilmasi;
        // cozum her seferinde ayni: ARANAN SEYI DARALT.
        var bas = kaynak.IndexOf($"Task {metotAdi}(", StringComparison.Ordinal);
        if (bas < 0) { bas = kaynak.IndexOf($"Task<SiteDto> {metotAdi}(", StringComparison.Ordinal); }
        Assert.True(bas > 0, $"metot BILDIRIMI bulunamadi: {metotAdi}");

        // Sonraki metot bildirimine kadar olan parca yeterli; tam ayrac sayimi bu sinama icin gereksiz.
        var son = kaynak.IndexOf("\n    public ", bas + 1, StringComparison.Ordinal);
        if (son < 0) { son = kaynak.Length; }

        return kaynak[bas..son];
    }

    private static string KaynakKoku()
    {
        var yol = AppContext.BaseDirectory;
        while (yol is not null && !File.Exists(Path.Combine(yol, "AdaPanel.sln")))
        {
            yol = Path.GetDirectoryName(yol);
        }

        Assert.NotNull(yol);
        return yol!;
    }
}
