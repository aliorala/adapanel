using System.Text.RegularExpressions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// SITE ARAC IKONLARI: her aracin GERCEK bir ikonu olmali, her ikonun da bir kullanicisi.
///
/// <para>
/// <b>Neden var — olculmus, sessiz duran hata.</b> Site araclari uc ayri ekranda listelenir
/// (<c>SitesView</c> satir kisayollari, <c>SiteDetailView</c> arac kartlari,
/// <c>WebsiteWorkspaceView</c> dikey arac listesi) ve ucu de ayni <c>siteToolIcon()</c>
/// fonksiyonunu cagirir. O fonksiyon, kimligi haritada olmayan bir arac icin SESSIZCE bos bir
/// daire dondurur (<c>&lt;circle r="7"/&gt;</c>) — tipki menudeki <c>iconFor</c> gibi. Menude bu
/// yedek fark edilmeden 10 maddeye yayilmisti (bkz. <see cref="PanelNavigationIconTests"/>);
/// arac tarafinda ayni cukurun acilmasini bu test engeller.
/// </para>
/// <para>
/// <b>Ters yon de olculur.</b> Bir arac yeniden adlandirildiginda ya da kaldirildiginda haritadaki
/// eski anahtar olu kalir. Olu ikon zarar vermez ama bir sonraki okuyanin onu gercek sanmasina yol
/// acar; ayrica "bu ikon nerede cikiyor?" sorusunun cevabi olmayan bir kayit birikir.
/// </para>
/// </summary>
public sealed class SiteToolIconTests
{
    [Fact]
    public void Her_aracin_GERCEK_bir_ikonu_var()
    {
        var araclar = KullanilanKimlikler();
        var ikonlar = IkonKimlikleri();

        // Kendi kendini denetleyen alt sinir: regex bozulup hicbir sey bulamazsa test "sorun yok"
        // diyerek YESIL kalmasin. 2026-08 olcumu: 23 kullanilan kimlik, 23 ikon.
        Assert.True(araclar.Count >= 20, $"Arac taramasi bozulmus olabilir: {araclar.Count} kimlik bulundu.");
        Assert.True(ikonlar.Count >= 20, $"Ikon taramasi bozulmus olabilir: {ikonlar.Count} ikon bulundu.");

        var ikonsuzlar = araclar.Where(id => !ikonlar.Contains(id)).ToList();

        Assert.True(
            ikonsuzlar.Count == 0,
            "Su araclarin SITE_TOOL_ICONS icinde karsiligi YOK; ekranda hepsi ayni bos daire olarak "
            + "cizilir ve ikon hicbir sey anlatmaz. utils/siteToolIcons.ts icine ayni uslupta "
            + "(viewBox 0 0 24 24, dolgusuz, currentColor cizgi) AYIRT EDILEBILIR bir ikon ekleyin:\n  "
            + string.Join("\n  ", ikonsuzlar));
    }

    [Fact]
    public void Ikon_haritasinda_olu_kayit_yok()
    {
        var kullanilan = KullanilanKimlikler();
        var olulur = IkonKimlikleri().Where(id => !kullanilan.Contains(id)).ToList();

        Assert.True(
            olulur.Count == 0,
            "SITE_TOOL_ICONS icinde hicbir ekranin cizmedigi ikon(lar) var. Arac kaldirildiysa "
            + "ikonu da kaldirin; kaldirilmadiysa hangi listede kimlik degistigini bulun:\n  "
            + string.Join("\n  ", olulur));
    }

    /// <summary>
    /// KAYNAGA BAKAN TESTIN SART KOSULU: tutundugumuz kalip YORUMLARDA gecmiyor.
    /// </summary>
    /// <remarks>
    /// Bu dosyalarin yorumlari uzun ve ornek kod iceriyor (ikon secim gerekceleri). Bir yorumda
    /// <c>id: "x", label:</c> kalibi gecerse tarama hayali bir arac uydurur ve yukaridaki iki test
    /// yanlis yerde kirmizi/yesil olur.
    /// </remarks>
    [Fact]
    public void Tutundugumuz_kalip_YORUMLARDA_gecmiyor()
    {
        foreach (var yol in AracListesiDosyalari())
        {
            var ham = File.ReadAllText(yol);
            Assert.Equal(AracKimlikleriniAyikla(ham), AracKimlikleriniAyikla(YorumlariSil(ham)));
        }
    }

    /// <summary>
    /// Yorum temizleyicinin kendisi calisiyor mu — sentetik girdiyle olculur.
    /// </summary>
    /// <remarks>
    /// Yukaridaki test bugun iki kume de esit oldugu icin gecer; temizleyici HIC CALISMASA da
    /// gecerdi. Kapsamin kendisi olculmeli, yoksa koruma sessizce bosa duser.
    /// </remarks>
    [Fact]
    public void Yorum_temizleyici_gercekten_calisiyor()
    {
        const string Girdi = """
            const a = [
              { id: "gercek", label: t("x") },
              // { id: "satirYorumu", label: t("x") },
              /* { id: "blokYorumu", group: "g", label: t("x") } */
            ];
            """;

        Assert.Equal(new[] { "gercek" }, AracKimlikleriniAyikla(YorumlariSil(Girdi)).ToArray());
        // Ayni girdide temizlik yapilmazsa UCU birden gorunur: olcum, testin bir sey olctugunu kanitlar.
        Assert.Equal(3, AracKimlikleriniAyikla(Girdi).Count);
    }

    /// <summary>
    /// <c>siteToolIcon()</c>a gercekten giren butun kimlikler: uc ekranin arac listeleri +
    /// site calisma alaninin ust sekmelerindeki acik <c>icon:</c> secimleri.
    /// </summary>
    private static SortedSet<string> KullanilanKimlikler()
    {
        var kimlikler = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var yol in AracListesiDosyalari())
        {
            var kaynak = YorumlariSil(File.ReadAllText(yol));
            kimlikler.UnionWith(AracKimlikleriniAyikla(kaynak));

            // Ust sekmeler kimliklerini DEGIL ayri bir `icon:` alanini kullanir: sekme kimlikleri
            // grup adlaridir ("pano", "start"), ikon sozlugu ise ortaktir.
            kimlikler.UnionWith(
                Regex.Matches(kaynak, @"icon:\s*""([A-Za-z][\w-]*)""").Select(e => e.Groups[1].Value));
        }

        return kimlikler;
    }

    /// <summary>
    /// Arac nesne sabitleri iki bicimde yazilir: <c>{ id: "x", label: … }</c> (SitesView,
    /// SiteDetailView) ve <c>{ id: "x", group: "y", label: … }</c> (WebsiteWorkspaceView).
    /// <c>label</c> sartini birakmak, ust sekmeleri (<c>id: "pano" as const, icon: …</c>) bilerek
    /// disarida tutar — onlar <c>icon:</c> alanindan okunur.
    /// </summary>
    private static SortedSet<string> AracKimlikleriniAyikla(string kaynak)
        => new(
            Regex.Matches(kaynak, @"id:\s*""([A-Za-z][\w-]*)""\s*,\s*(?:group:\s*""[\w-]*""\s*,\s*)?label\s*:")
                .Select(e => e.Groups[1].Value),
            StringComparer.Ordinal);

    /// <summary>
    /// <c>utils/siteToolIcons.ts</c> icindeki <c>SITE_TOOL_ICONS</c> haritasinin anahtarlari.
    /// </summary>
    private static SortedSet<string> IkonKimlikleri()
    {
        var kaynak = YorumlariSil(File.ReadAllText(Path.Combine(FrontendKoku(), "utils", "siteToolIcons.ts")));
        var eslesme = Regex.Match(
            kaynak,
            @"SITE_TOOL_ICONS[^=]*=\s*\{(.*?)\n\};",
            RegexOptions.Singleline);

        Assert.True(eslesme.Success, "SITE_TOOL_ICONS blogu siteToolIcons.ts icinde bulunamadi (yeniden adlandirildi mi?).");

        // Anahtarlar satir basinda `ad: '...'` ya da `ad:\n  '...'` seklinde. Deger tek tirnakli
        // SVG oldugu icin `"` icerir; anahtari SATIR BASINDAN yakalamak degerin icine kaymayi onler.
        return new SortedSet<string>(
            Regex.Matches(eslesme.Groups[1].Value, @"^\s{2}([A-Za-z][\w-]*)\s*:", RegexOptions.Multiline)
                .Select(e => e.Groups[1].Value),
            StringComparer.Ordinal);
    }

    private static string[] AracListesiDosyalari()
        => new[] { "SitesView.vue", "SiteDetailView.vue", "WebsiteWorkspaceView.vue" }
            .Select(ad => Path.Combine(FrontendKoku(), "views", ad))
            .ToArray();

    /// <summary>
    /// Blok yorumlarini, HTML yorumlarini ve dize disi satir yorumlarini siler.
    /// </summary>
    /// <remarks>
    /// <c>//</c> icin tirnak sayimi yapilir: SVG yollari ve URL'ler dize icinde <c>//</c>
    /// barindirabilir, kaba bir silme onlari kirpar ve tarama sessizce eksik kalirdi.
    /// </remarks>
    private static string YorumlariSil(string kaynak)
    {
        var metin = Regex.Replace(kaynak, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        metin = Regex.Replace(metin, "<!--.*?-->", " ", RegexOptions.Singleline);

        var satirlar = metin.Split('\n').Select(satir =>
        {
            var tirnak = 0;
            for (var i = 0; i + 1 < satir.Length; i++)
            {
                if (satir[i] is '"' or '\'')
                {
                    tirnak++;
                    continue;
                }

                if (satir[i] == '/' && satir[i + 1] == '/' && tirnak % 2 == 0)
                {
                    return satir[..i];
                }
            }

            return satir;
        });

        return string.Join("\n", satirlar);
    }

    private static string FrontendKoku()
        => Path.Combine(KaynakKoku(), "frontend", "src");

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
