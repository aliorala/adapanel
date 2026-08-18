using System.Text.RegularExpressions;
using System.Xml.Linq;
using AdaPanel.Infrastructure.Mail;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// JMAP VEKILI BIR GUVENLIK YUZEYIDIR: ne gecer, ne reddedilir.
/// </summary>
/// <remarks>
/// <para>
/// NEDEN VAR: site basina posta yedegi icin <c>vandelay</c>'in JMAP ucuna ulasmasi gerekiyor ve
/// Stalwart'in duyurdugu adres HER ZAMAN <c>https://&lt;ad&gt;/jmap/</c> (port eklenemez). AdaPanel
/// ise 8080'i BILEREK loopback'te tutuyor, 443 IIS'te. Yani tek yol IIS'ten vekillemek -- ve
/// vekilin arkasinda Stalwart'in YONETIM API'si (<c>/api/</c>) duruyor. Yanlis yazilmis tek bir
/// kural, yonetim API'sini internete acar.
/// </para>
/// <para>
/// <b>IIS TAKLIT EDILIYOR:</b> bu testler IIS calistirmaz. URL Rewrite'in eslesme kalibi .NET
/// Regex'tir; testler kurallardaki kalibi XML'den OKUYUP ayni Regex ile degerlendirir. Yani
/// dogrulanan sey KURALIN KENDISIDIR (kalip, sira, eylem) -- IIS'in modulu degil. Kalip yanlissa
/// burada yakalanir; IIS modulunun kendi hatasi burada yakalanmaz.
/// </para>
/// </remarks>
public sealed class MailJmapProxyRulesTests
{
    private const string Backend = "http://127.0.0.1:8080";

    // ===========================================================================================
    // 1) NE GECER: yalnizca iki JMAP yolu, ve loopback arka uca
    // ===========================================================================================

    [Theory]
    [InlineData("jmap/", "/jmap/")]
    [InlineData("jmap/session", "/jmap/session")]
    [InlineData("jmap/download/a/b/c.eml", "/jmap/download/a/b/c.eml")]
    [InlineData("jmap/upload/a/", "/jmap/upload/a/")]
    [InlineData(".well-known/jmap", "/.well-known/jmap")]
    public void JMAP_yollari_LOOPBACK_arka_uca_gecirilir(string yol, string beklenenYol)
    {
        var belge = Uygula();

        var kural = EslesenKural(belge, yol);

        Assert.NotNull(kural);
        Assert.Equal("Rewrite", Eylem(kural!, "type"));
        Assert.Equal(Backend + beklenenYol, HedefAdres(kural!, yol));
    }

    // ===========================================================================================
    // 2) NE GECMEZ: Stalwart'in yonetim API'si ve diger her sey vekile HIC girmez
    // ===========================================================================================

    [Theory]
    [InlineData("api/settings")]           // Stalwart yonetim API'si
    [InlineData("api/principal")]
    [InlineData("admin")]
    [InlineData("webadmin/index.html")]
    [InlineData("jmapx/session")]          // onek benzeri ama BASKA bir yol
    [InlineData("notjmap/session")]
    public void Yonetim_API_si_ve_diger_yollar_vekile_HIC_girmez(string yol)
    {
        var belge = Uygula();

        // Panelin HICBIR kurali eslesmemeli: istek IIS'te sitenin kendi icerigine duser, 8080'e DEGIL.
        Assert.Null(EslesenKural(belge, yol));
    }

    /// <summary>
    /// MUSTERININ KENDI <c>/api/</c> uygulamasi BOZULMAZ.
    /// </summary>
    /// <remarks>
    /// Bilincli tasarim: kurallar MUSTERININ web.config'ine giriyor. <c>^api/</c> icin toptan bir
    /// RED kurali yazmak, musterinin kendi API'sini oldururdu -- vekille hic ilgisi olmayan bir
    /// yolu kapatmak, cozdugunden buyuk bir ariza uretirdi. Stalwart'in <c>/api/</c>'sine giden TEK
    /// olasi yol <c>/jmap/</c> onekinden dizin cikmaktir; onu da asagidaki RED kurali keser.
    /// </remarks>
    [Fact]
    public void Musterinin_kendi_api_yolu_ne_vekillenir_ne_reddedilir()
    {
        var belge = Uygula();

        var kural = EslesenKural(belge, "api/siparisler");

        Assert.Null(kural);
    }

    // ===========================================================================================
    // 3) ACIK RED: JMAP onekinden yonetim API'sine kacis
    // ===========================================================================================

    [Theory]
    [InlineData("/jmap/../api/settings")]
    [InlineData("/jmap/%2e%2e/api/settings")]
    [InlineData("/jmap/%252e%252e/api/settings")]
    [InlineData("/jmap/..%5capi")]
    [InlineData("/.well-known/jmap/../api")]
    public void JMAP_onekinden_dizin_cikma_403_ile_KESILIR(string hamAdres)
    {
        var belge = Uygula();
        var red = Kurallar(belge)[0];

        // (a) Red kurali gercekten bu ham adresi yakaliyor mu?
        var kalip = (string)red.Element("conditions")!.Element("add")!.Attribute("pattern")!;
        Assert.Matches(new Regex(kalip), hamAdres);

        // (b) Ve eylemi arka uca GONDERMEK degil, 403 ile kesmek.
        Assert.Equal("CustomResponse", Eylem(red, "type"));
        Assert.Equal("403", Eylem(red, "statusCode"));
    }

    /// <summary>Temiz JMAP istekleri RED kaliba TAKILMAZ (vekil calismaz hale gelmemeli).</summary>
    [Theory]
    [InlineData("/jmap/session")]
    [InlineData("/jmap/download/a/b/mesaj.eml?accept=message/rfc822")]
    [InlineData("/.well-known/jmap")]
    public void Temiz_JMAP_istekleri_RED_kalibina_takilmaz(string hamAdres)
    {
        var belge = Uygula();
        var kalip = (string)Kurallar(belge)[0].Element("conditions")!.Element("add")!.Attribute("pattern")!;

        Assert.DoesNotMatch(new Regex(kalip), hamAdres);
    }

    // ===========================================================================================
    // 4) SIRA: red kurali EN BASTA olmali
    // ===========================================================================================

    /// <summary>
    /// Red kurali gecirici kurallardan SONRA gelirse hic calismaz: gecirici kural
    /// <c>stopProcessing</c> ile isi bitirir. Yani "yazilmis ama olu" bir guvenlik kurali olurdu.
    /// </summary>
    [Fact]
    public void Red_kurali_EN_BASTA_yazilir()
    {
        var belge = Uygula();

        var adlar = Kurallar(belge).Select(kural => (string)kural.Attribute("name")!).ToArray();

        Assert.Equal(MailJmapProxyRules.RuleNamePrefix + "Deny", adlar[0]);
        Assert.Equal(3, adlar.Length);
        Assert.All(Kurallar(belge), kural => Assert.Equal("true", (string?)kural.Attribute("stopProcessing")));
    }

    /// <summary>
    /// MUTASYON: red kurali asagi kaydirilirsa saglik olcumu YESIL KALMAMALI. Yalniz kural SAYISINA
    /// bakan bir olcum bunu goremezdi (sayi yine uc).
    /// </summary>
    [Fact]
    public void Red_kurali_sirasi_bozulursa_saglik_olcumu_YESIL_kalmaz()
    {
        var belge = Uygula();
        Assert.True(MailJmapProxyRules.IsHealthy(belge));

        var kurallar = Kurallar(belge);
        var red = kurallar[0];
        red.Remove();
        kurallar[^1].AddAfterSelf(red);

        Assert.Equal(3, MailJmapProxyRules.Count(belge));
        Assert.False(MailJmapProxyRules.IsHealthy(belge));
    }

    /// <summary>Kural arka ucu degistirilirse (baska bir adrese cevrilirse) saglik olcumu duser.</summary>
    [Fact]
    public void Yanlis_arka_uca_bakan_kural_YESIL_gorunmez()
    {
        var belge = Uygula();
        Kurallar(belge)[^1].Element("action")!.SetAttributeValue("url", "http://kotu.ornek.com/jmap/{R:1}");

        Assert.False(MailJmapProxyRules.IsHealthy(belge));
    }

    // ===========================================================================================
    // 5) IDEMPOTENT + KULLANICININ KURALLARINA DOKUNMAMA
    // ===========================================================================================

    [Fact]
    public void Yeniden_yayinlamak_kurallari_COGALTMAZ_ve_kullanicininkine_dokunmaz()
    {
        var belge = KullaniciKuraliOlanBelge();

        MailJmapProxyRules.Apply(belge);
        MailJmapProxyRules.Apply(belge);
        MailJmapProxyRules.Apply(belge);

        Assert.Equal(3, MailJmapProxyRules.Count(belge));
        Assert.True(MailJmapProxyRules.IsHealthy(belge));

        // Kullanicinin kurali yerinde ve BOZULMAMIS.
        var kullanici = TumKurallar(belge).Single(kural =>
            (string?)kural.Attribute("name") == "MusterininKendiKurali");
        Assert.Equal("index.php", (string?)kullanici.Element("action")!.Attribute("url"));
    }

    [Fact]
    public void Yayindan_alma_YALNIZ_panelin_kurallarini_kaldirir()
    {
        var belge = KullaniciKuraliOlanBelge();
        MailJmapProxyRules.Apply(belge);

        var kaldirilan = MailJmapProxyRules.Remove(belge);

        Assert.Equal(3, kaldirilan);
        Assert.Equal(0, MailJmapProxyRules.Count(belge));
        Assert.Single(TumKurallar(belge));
        Assert.False(MailJmapProxyRules.IsHealthy(belge));
    }

    // ===========================================================================================
    // Yardimcilar -- IIS'in URL Rewrite eslesmesini TAKLIT eder (bkz. sinif ozeti)
    // ===========================================================================================

    private static XDocument Uygula()
    {
        var belge = new XDocument(new XElement("configuration"));
        MailJmapProxyRules.Apply(belge);
        return belge;
    }

    private static XDocument KullaniciKuraliOlanBelge()
    {
        var belge = new XDocument(new XElement("configuration"));
        var kurallar = new XElement("rules",
            new XElement("rule",
                new XAttribute("name", "MusterininKendiKurali"),
                new XElement("match", new XAttribute("url", ".*")),
                new XElement("action", new XAttribute("type", "Rewrite"), new XAttribute("url", "index.php"))));
        belge.Element("configuration")!.Add(new XElement("system.webServer", new XElement("rewrite", kurallar)));
        return belge;
    }

    private static List<XElement> Kurallar(XDocument belge) =>
        [.. TumKurallar(belge).Where(kural =>
            ((string?)kural.Attribute("name"))?.StartsWith(MailJmapProxyRules.RuleNamePrefix, StringComparison.Ordinal) == true)];

    private static List<XElement> TumKurallar(XDocument belge) =>
        [.. belge.Element("configuration")!.Element("system.webServer")!.Element("rewrite")!.Element("rules")!.Elements("rule")];

    /// <summary>
    /// Verilen yolu (bas egik cizgisiz, IIS'in <c>match url</c>'e verdigi bicimde) hangi GECIRICI
    /// kural yakalar? Red kurali kosullu oldugundan burada degerlendirilmez (ayri test eder).
    /// </summary>
    private static XElement? EslesenKural(XDocument belge, string yol) =>
        Kurallar(belge)
            .Where(kural => Eylem(kural, "type") == "Rewrite")
            .FirstOrDefault(kural => new Regex(
                (string)kural.Element("match")!.Attribute("url")!,
                RegexOptions.IgnoreCase).IsMatch(yol));

    private static string? Eylem(XElement kural, string ad) => (string?)kural.Element("action")!.Attribute(ad);

    /// <summary>Kuralin hedef adresini <c>{R:1}</c> yerine gercek yakalamayi koyarak uretir.</summary>
    private static string HedefAdres(XElement kural, string yol)
    {
        var kalip = new Regex((string)kural.Element("match")!.Attribute("url")!, RegexOptions.IgnoreCase);
        var eslesme = kalip.Match(yol);
        var hedef = (string)kural.Element("action")!.Attribute("url")!;
        for (var i = 1; i < eslesme.Groups.Count; i++)
        {
            hedef = hedef.Replace("{R:" + i + "}", eslesme.Groups[i].Value);
        }

        return hedef;
    }
}
