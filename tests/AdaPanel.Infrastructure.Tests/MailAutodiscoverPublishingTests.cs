using System.Xml.Linq;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Middleware;
using AdaPanel.Infrastructure.Mail;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Otomatik yapılandırmanın istemciye GERÇEKTEN ulaşmasını sabitler.
/// </summary>
/// <remarks>
/// <para>
/// Yaşanan arıza şuydu: <c>/autodiscover/autodiscover.xml</c> ucu yazıldı, test edildi ve
/// çalışıyordu — ama istemciler oraya HİÇ gelmiyordu. Müşterinin alan adı IIS'te 80/443'ten
/// servis ediliyor, panel ise ayrı bir portta. Yani ölçülmesi gereken şey ucun doğruluğu değil,
/// isteğin o uca DÜŞÜP DÜŞMEDİĞİDİR.
/// </para>
/// <para>
/// Bu dosya zincirin kırılabilecek her halkasını ayrı ayrı kilitler: kuralın yeri, kuralın hedefi,
/// vekilin taşıdığı alan adı ve kuralın yayınladığı yolların panelin gerçekten dinlediği yollarla
/// aynı olması.
/// </para>
/// </remarks>
public sealed class MailAutodiscoverPublishingTests
{
    private const string Alan = "ornek.com";
    private const string ArkaUc = "https://localhost:8890";

    // --- 1) Kuralın YERİ: WordPress'in önüne geçmeli ---

    /// <summary>
    /// WordPress'in IIS kuralı "istenen şey dosya/klasör değilse index.php'ye yaz" biçimindedir ve
    /// <c>stopProcessing</c> ile biter. <c>/autodiscover/autodiscover.xml</c> diskte bir dosya
    /// OLMADIĞI için o kural onu yakalar. Kurallarımız sona eklenseydi WordPress kurulu HER
    /// sitede özellik sessizce ölü olurdu — ve davranış testleri bunu göremezdi.
    /// </summary>
    [Fact]
    public void Kurallar_kullanicinin_yakalayici_kuralindan_ONCE_yazilir()
    {
        var belge = WordPressWebConfig();

        MailAutodiscoverRewriteRules.Apply(belge, Alan, ArkaUc);

        var adlar = Kurallar(belge).Select(kural => (string?)kural.Attribute("name")).ToList();

        Assert.Equal(MailAutodiscoverRewriteRules.ExpectedRuleCount + 1, adlar.Count);
        Assert.All(
            adlar.Take(MailAutodiscoverRewriteRules.ExpectedRuleCount),
            ad => Assert.StartsWith(MailAutodiscoverRewriteRules.RuleNamePrefix, ad ?? string.Empty, StringComparison.Ordinal));
        Assert.Equal("WordPress", adlar[^1]);
    }

    // --- 2) Kuralın HEDEFİ: alan adı vekille birlikte taşınmalı ---

    /// <summary>
    /// ARR, arka uca giderken Host başlığını hedefin adıyla (<c>localhost</c>) değiştirir. Panel
    /// sorulan alan adını istek ana adından çözdüğü için, alan adı açıkça taşınmasaydı panel
    /// "localhost" görür, onu bir posta alanı olarak tanımaz ve HER istek 404 dönerdi: kural
    /// yazılmış ama özellik yine ölü olurdu.
    /// </summary>
    [Fact]
    public void Her_kural_alan_adini_vekil_uzerinden_ACIKCA_tasir()
    {
        var belge = BosWebConfig();

        MailAutodiscoverRewriteRules.Apply(belge, Alan, ArkaUc);

        var hedefler = KendiKurallari(belge)
            .Select(kural => (string?)kural.Element("action")?.Attribute("url") ?? string.Empty)
            .ToList();

        Assert.Equal(MailAutodiscoverRewriteRules.ExpectedRuleCount, hedefler.Count);
        Assert.All(hedefler, url => Assert.EndsWith("?domain=" + Alan, url, StringComparison.Ordinal));
        Assert.All(hedefler, url => Assert.StartsWith(ArkaUc + "/", url, StringComparison.Ordinal));
    }

    /// <summary>
    /// Zincirin panel tarafındaki karşılığı: vekilin yazdığı alan adı GERÇEKTEN okunmalı.
    /// Bu olmadan yukarıdaki kural doğru yazılır ama panel yine 404 döner.
    /// </summary>
    [Fact]
    public void Panel_vekilin_yazdigi_alan_adini_ana_ad_localhost_iken_de_cozer()
    {
        Assert.Equal(Alan, MailAutodiscoverController.ResolveDomainFrom(null, "localhost", Alan));
    }

    /// <summary>İstemcinin bildirdiği adres, vekilin ipucundan ÖNCE gelir (mevcut davranış korunur).</summary>
    [Fact]
    public void Istemcinin_bildirdigi_adres_vekil_ipucundan_once_gelir()
    {
        Assert.Equal("baska.com", MailAutodiscoverController.ResolveDomainFrom("info@baska.com", "localhost", Alan));
    }

    /// <summary>Vekil ipucu yoksa davranış değişmez: ana addan çözülür.</summary>
    [Fact]
    public void Vekil_ipucu_yoksa_ana_addan_cozulmeye_devam_eder()
    {
        Assert.Equal(Alan, MailAutodiscoverController.ResolveDomainFrom(null, "autodiscover." + Alan, null));
    }

    // --- 3) Yayınlanan YOLLAR panelin dinlediği yollarla aynı olmalı ---

    /// <summary>
    /// Kuralların eşleştirdiği yollar ile panelin gerçekten servis ettiği yolların AYNI olması.
    /// </summary>
    /// <remarks>
    /// Bu, "kod var ama hiç çalışmıyor" sınıfının doğrudan kilididir: biri uçların yolunu
    /// değiştirdiğinde ya da kuraldaki deseni bozduğunda, her iki taraf da kendi başına doğru
    /// görünmeye devam eder ve yalnız BİRLİKTE ölçüldüklerinde kopukluk ortaya çıkar.
    /// </remarks>
    [Fact]
    public void Yayinlanan_yollar_panelin_ACIK_yollariyla_birebir_ayni()
    {
        var belge = BosWebConfig();
        MailAutodiscoverRewriteRules.Apply(belge, Alan, ArkaUc);

        foreach (var kural in KendiKurallari(belge))
        {
            var hedef = (string?)kural.Element("action")?.Attribute("url") ?? string.Empty;
            var yol = new Uri(hedef).AbsolutePath;

            // (a) Panel bu yolu gizli giriş yolu denetiminden muaf tutuyor mu?
            Assert.True(
                SecureEntryPathMiddleware.IsAlwaysPublicPath(new PathString(yol)),
                yol + " kurala yazıldı ama panelde herkese açık değil; istek 404 alır.");

            // (b) Kuralın eşleştirdiği desen, hedef yolun kendisini gerçekten karşılıyor mu?
            var desen = (string?)kural.Element("match")?.Attribute("url") ?? string.Empty;
            Assert.Matches(desen, yol.TrimStart('/'));
        }
    }

    /// <summary>
    /// Kullanıcıya gösterilen doğrulama adresleri de aynı yolları kullanmalı; farklı olsalardı
    /// "elle denedim, çalışıyor" ile "istemcide çalışmıyor" arasında sessiz bir sapma doğardı.
    /// </summary>
    [Fact]
    public void Dogrulama_adresleri_kurallarla_ayni_yollari_gosterir()
    {
        var belge = BosWebConfig();
        MailAutodiscoverRewriteRules.Apply(belge, Alan, ArkaUc);

        var kuralYollari = KendiKurallari(belge)
            .Select(kural => new Uri((string?)kural.Element("action")!.Attribute("url")!).AbsolutePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var probYollari = MailAutodiscoverRewriteRules.ProbeUrls(Alan)
            .Select(url => new Uri(url).AbsolutePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(kuralYollari, probYollari);
    }

    // --- 4) Müşterinin kendi yapılandırmasına zarar vermemeli ---

    /// <summary>
    /// <c>/mail/config-v1.1.xml</c> Thunderbird'ün YALNIZ <c>autoconfig.&lt;alan&gt;</c> ana adında
    /// denediği yoldur. Koşulsuz bırakılsaydı müşterinin kendi <c>/mail/config-v1.1.xml</c>
    /// dosyası panelin XML'iyle değiştirilirdi.
    /// </summary>
    [Fact]
    public void Musterinin_mail_yolu_yalniz_autoconfig_ana_adinda_ele_alinir()
    {
        var belge = BosWebConfig();
        MailAutodiscoverRewriteRules.Apply(belge, Alan, ArkaUc);

        var kural = KendiKurallari(belge)
            .Single(item => ((string?)item.Element("match")?.Attribute("url"))?.Contains("^mail/", StringComparison.Ordinal) == true);

        var kosul = kural.Element("conditions")?.Element("add");
        Assert.NotNull(kosul);
        Assert.Equal("{HTTP_HOST}", (string?)kosul!.Attribute("input"));
        Assert.Equal(@"^autoconfig\.", (string?)kosul.Attribute("pattern"));
    }

    /// <summary>Tekrar uygulamak kural çoğaltmaz ve kullanıcının kuralına dokunmaz.</summary>
    [Fact]
    public void Uygulama_idempotenttir_ve_kullanici_kurallarini_korur()
    {
        var belge = WordPressWebConfig();

        MailAutodiscoverRewriteRules.Apply(belge, Alan, ArkaUc);
        MailAutodiscoverRewriteRules.Apply(belge, Alan, ArkaUc);

        Assert.Equal(MailAutodiscoverRewriteRules.ExpectedRuleCount, MailAutodiscoverRewriteRules.Count(belge));
        Assert.Single(Kurallar(belge).Where(kural => (string?)kural.Attribute("name") == "WordPress"));
    }

    /// <summary>Geri alma YALNIZ panelin kurallarını kaldırır.</summary>
    [Fact]
    public void Geri_alma_yalniz_panelin_kurallarini_kaldirir()
    {
        var belge = WordPressWebConfig();
        MailAutodiscoverRewriteRules.Apply(belge, Alan, ArkaUc);

        var kaldirilan = MailAutodiscoverRewriteRules.Remove(belge);

        Assert.Equal(MailAutodiscoverRewriteRules.ExpectedRuleCount, kaldirilan);
        Assert.Equal(0, MailAutodiscoverRewriteRules.Count(belge));
        Assert.Single(Kurallar(belge));
        Assert.Equal("WordPress", (string?)Kurallar(belge).Single().Attribute("name"));
    }

    // --- 5) BAYAT kural yeşil görünmemeli ---

    /// <summary>
    /// Panelin portu değiştiğinde kural yerinde kalır ama 502 üretir. "Üç kural da var" ölçümü
    /// bunu yeşil gösterseydi kullanıcı bozuk bir kurulumu çalışıyor sanardı.
    /// </summary>
    [Theory]
    [InlineData("https://localhost:9999", Alan)]
    [InlineData(ArkaUc, "baska.com")]
    public void Hedefi_bayatlamis_kural_YAYINDA_sayilmaz(string olculenArkaUc, string olculenAlan)
    {
        var belge = BosWebConfig();
        MailAutodiscoverRewriteRules.Apply(belge, Alan, ArkaUc);

        Assert.False(MailAutodiscoverRewriteRules.TargetsBackend(belge, olculenAlan, olculenArkaUc));
    }

    [Fact]
    public void Taze_yazilmis_kural_YAYINDA_sayilir()
    {
        var belge = BosWebConfig();
        MailAutodiscoverRewriteRules.Apply(belge, Alan, ArkaUc);

        Assert.True(MailAutodiscoverRewriteRules.TargetsBackend(belge, Alan, ArkaUc));
    }

    /// <summary>Eksik kural yayında sayılmaz: iki istemciden biri ayar alamıyor demektir.</summary>
    [Fact]
    public void Eksik_kural_YAYINDA_sayilmaz()
    {
        var belge = BosWebConfig();
        MailAutodiscoverRewriteRules.Apply(belge, Alan, ArkaUc);
        KendiKurallari(belge).First().Remove();

        Assert.False(MailAutodiscoverRewriteRules.TargetsBackend(belge, Alan, ArkaUc));
    }

    // --- 6) Alan adı doğrulaması ---

    /// <summary>
    /// Alan adı hem <c>web.config</c>'e hem IIS bağlamasına gider. Katı doğrulama olmasaydı,
    /// XML'e ya da bağlama dizgisine kaçış gerektiren metin girebilirdi.
    /// </summary>
    [Theory]
    [InlineData("ornek.com")]
    [InlineData("alt.ornek.com.tr")]
    public void Gecerli_alan_adlari_kabul_edilir(string alan)
    {
        Assert.Equal(alan, MailAutodiscoverPublisher.NormalizeDomain(alan.ToUpperInvariant() + "."));
    }

    [Theory]
    [InlineData("")]
    [InlineData("localhost")]
    [InlineData("*.ornek.com")]
    [InlineData("ornek.com/../..")]
    [InlineData("ornek.com\" />")]
    [InlineData("-ornek.com")]
    public void Gecersiz_alan_adlari_REDDEDILIR(string alan)
    {
        Assert.Throws<AdaPanel.Core.Common.AppException>(() => MailAutodiscoverPublisher.NormalizeDomain(alan));
    }

    // --- Yardımcılar ---

    private static XDocument BosWebConfig() => new(new XElement("configuration"));

    /// <summary>WordPress'in IIS'e yazdığı tipik yakalayıcı kural.</summary>
    private static XDocument WordPressWebConfig() => new(
        new XElement(
            "configuration",
            new XElement(
                "system.webServer",
                new XElement(
                    "rewrite",
                    new XElement(
                        "rules",
                        new XElement(
                            "rule",
                            new XAttribute("name", "WordPress"),
                            new XAttribute("stopProcessing", "true"),
                            new XElement("match", new XAttribute("url", "^(.*)$")),
                            new XElement(
                                "conditions",
                                new XElement("add", new XAttribute("input", "{REQUEST_FILENAME}"), new XAttribute("matchType", "IsFile"), new XAttribute("negate", "true")),
                                new XElement("add", new XAttribute("input", "{REQUEST_FILENAME}"), new XAttribute("matchType", "IsDirectory"), new XAttribute("negate", "true"))),
                            new XElement("action", new XAttribute("type", "Rewrite"), new XAttribute("url", "index.php"))))))));

    private static List<XElement> Kurallar(XDocument belge) =>
        belge.Element("configuration")!.Element("system.webServer")!.Element("rewrite")!.Element("rules")!
            .Elements("rule").ToList();

    private static List<XElement> KendiKurallari(XDocument belge) =>
        Kurallar(belge)
            .Where(kural => ((string?)kural.Attribute("name"))?.StartsWith(MailAutodiscoverRewriteRules.RuleNamePrefix, StringComparison.Ordinal) == true)
            .ToList();
}
