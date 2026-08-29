using System.Text.RegularExpressions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// MENUDEKI HER BOLUMUN GERCEK BIR IKONU OLMALI — VE BIR GRUBA AIT OLMALI.
///
/// <para>
/// <b>Neden var — olculmus, sessiz duran hata:</b> <c>MainLayout.vue</c> icindeki
/// <c>iconFor()</c> bilinmeyen bir bolum kimligi icin sessizce bos bir daire dondurur
/// (<c>&lt;circle r="7"/&gt;</c>). Bu yedek, kimse fark etmeden bir SESSIZ CUKUR haline gelmisti:
/// 2026-08 olcumunde yonetici menusundeki 21 maddenin 9'u (accounts, sitebuilder, seo, advisor,
/// audit, billing, cluster, ai, account) ve musteri menusundeki 1 maddesi (subusers) ayni bos
/// daireyle ciziliyordu. Ekranda dokuz ayni ikon = sifir bilgi; kullanici her defasinda etiketi
/// okumak zorunda kaliyordu. Hicbir derleme hatasi, hicbir uyari, hicbir test kirmizisi yoktu.
/// </para>
/// <para>
/// <b>Ikinci sessiz cukur (gruplama):</b> menu artik <c>NAV_GROUPS</c> ile basliklara ayriliyor.
/// Grup haritasinda karsiligi olmayan bir bolum kaybolmaz — bilerek "Diger" grubuna duser — ama
/// orada bulunmasi da yanlistir. Bu test o yedegin de bos kalmasini sabitler.
/// </para>
/// <para>
/// <b>Kapsam:</b> bolum listesinin TEK KAYNAGI <c>views/SetupWizardView.vue</c> icindeki
/// <c>SectionItem</c> nesne sabitleridir (rol sizgeci sonradan bu listeden secer). Test o
/// listedeki her kimligi okur, ikon haritasi ve grup haritasiyla karsilastirir. Yeni bir bolum
/// eklenip ikonu/grubu unutuldugunda burada kirmizi yanar.
/// </para>
/// </summary>
public sealed class PanelNavigationIconTests
{
    [Fact]
    public void Menudeki_her_bolumun_GERCEK_bir_ikonu_var()
    {
        var bolumler = BolumKimlikleri();
        var ikonlar = IkonKimlikleri();

        // Kendi kendini denetleyen alt sinir: regex bozulup hicbir sey bulamazsa test
        // "sorun yok" diyerek YESIL kalmasin. 2026-08 olcumu: 24 bolum, 24 ikon.
        Assert.True(bolumler.Count >= 20, $"Bolum taramasi bozulmus olabilir: {bolumler.Count} bolum bulundu.");
        Assert.True(ikonlar.Count >= 20, $"Ikon taramasi bozulmus olabilir: {ikonlar.Count} ikon bulundu.");

        var ikonsuzlar = bolumler.Where(id => !ikonlar.Contains(id)).ToList();

        Assert.True(
            ikonsuzlar.Count == 0,
            "Su bolumlerin NAV_ICONS icinde karsiligi YOK; menude hepsi ayni bos daire olarak "
            + "cizilir ve ikon hicbir sey anlatmaz. MainLayout.vue > NAV_ICONS icine ayni uslupta "
            + "(viewBox 0 0 24 24, cizgi/stroke) AYIRT EDILEBILIR bir ikon ekleyin:\n  "
            + string.Join("\n  ", ikonsuzlar));
    }

    [Fact]
    public void Menudeki_her_bolum_bir_GRUBA_ait()
    {
        var bolumler = BolumKimlikleri();
        var gruplananlar = GruplananKimlikler();

        Assert.True(gruplananlar.Count >= 20, $"Grup taramasi bozulmus olabilir: {gruplananlar.Count} kimlik bulundu.");

        var gruplanmayanlar = bolumler.Where(id => !gruplananlar.Contains(id)).ToList();

        Assert.True(
            gruplanmayanlar.Count == 0,
            "Su bolumler NAV_GROUPS haritasinda YOK. Menuden kaybolmazlar (\"Diger\" grubuna "
            + "duserler) ama kullanicinin onlari arayacagi yerde durmazlar. MainLayout.vue > "
            + "NAV_GROUPS icinde dogru gruba ekleyin:\n  "
            + string.Join("\n  ", gruplanmayanlar));
    }

    /// <summary>
    /// Grup haritasinda MENUDE OLMAYAN bir kimlik kalmamali (olu kayit).
    /// </summary>
    /// <remarks>
    /// Ters yon de bir hatadir: bir bolum yeniden adlandirildiginda grup haritasindaki eski ad
    /// sessizce olu kalir ve bir sonraki okuyan onu gercek saniyordu.
    /// </remarks>
    [Fact]
    public void Grup_haritasinda_olu_kimlik_yok()
    {
        var bolumler = BolumKimlikleri();
        var olulur = GruplananKimlikler().Where(id => !bolumler.Contains(id)).ToList();

        Assert.True(
            olulur.Count == 0,
            "NAV_GROUPS icinde SectionItem listesinde karsiligi olmayan kimlik(ler) var:\n  "
            + string.Join("\n  ", olulur));
    }

    /// <summary>
    /// KAYNAGA BAKAN TESTIN SART KOSULU: tutundugumuz dize YORUMLARDA gecmiyor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bu test kaynak metnini tariyor; eger <c>id: "x", label:</c> kalibi bir yorum satirinda da
    /// geciyorsa tarama hayali bir bolum uydurur ve yukaridaki testler yanlis yerde kirmizi/yesil
    /// olur. Olcum: yorumlar temizlenmis metinden cikan kimlik kumesi, HAM metinden cikanla
    /// AYNI olmali.
    /// </para>
    /// <para>
    /// Kirmizi yanarsa anlami: ya bir bolum yoruma alinmis (o zaman gercekten silinmesi gerekir),
    /// ya da bir yorum icinde ornek kod yazilmis (o zaman ornek baska bir bicimde yazilmali).
    /// </para>
    /// </remarks>
    [Fact]
    public void Tutundugumuz_kalip_YORUMLARDA_gecmiyor()
    {
        var ham = File.ReadAllText(SetupWizardYolu());
        var hamKimlikler = BolumKimlikleriniAyikla(ham);
        var temizKimlikler = BolumKimlikleriniAyikla(YorumlariSil(ham));

        Assert.Equal(hamKimlikler, temizKimlikler);
    }

    /// <summary>
    /// Yorum temizleyicisinin kendisi calisiyor mu — sentetik girdiyle olculur.
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
              /* { id: "blokYorumu", label: t("x") } */
            ];
            """;

        Assert.Equal(new[] { "gercek" }, BolumKimlikleriniAyikla(YorumlariSil(Girdi)).ToArray());
        // Ayni girdide temizlik yapilmazsa UCU birden gorunur: olcum, testin bir sey olctugunu kanitlar.
        Assert.Equal(3, BolumKimlikleriniAyikla(Girdi).Count);
    }

    /// <summary>
    /// <c>views/SetupWizardView.vue</c> icindeki <c>SectionItem</c> nesne sabitlerinin kimlikleri.
    /// </summary>
    private static SortedSet<string> BolumKimlikleri()
        => BolumKimlikleriniAyikla(YorumlariSil(File.ReadAllText(SetupWizardYolu())));

    private static SortedSet<string> BolumKimlikleriniAyikla(string kaynak)
        => new(
            Regex.Matches(kaynak, @"id:\s*""([A-Za-z][\w-]*)""\s*,\s*label\s*:")
                .Select(e => e.Groups[1].Value),
            StringComparer.Ordinal);

    /// <summary>
    /// <c>layouts/MainLayout.vue</c> icindeki <c>NAV_ICONS</c> haritasinin anahtarlari.
    /// </summary>
    private static SortedSet<string> IkonKimlikleri()
    {
        var govde = TekBlok(MainLayoutKaynagi(), @"const\s+NAV_ICONS\b[^=]*=\s*\{(.*?)\n\};", "NAV_ICONS");

        // Anahtarlar satir basinda `ad: '...'` seklinde. Deger tek tirnakli SVG oldugu icin
        // `"` iceriyor; anahtari SATIR BASINDAN yakalamak degerin icine kaymayi imkansiz kilar.
        return new SortedSet<string>(
            Regex.Matches(govde, @"^\s*([A-Za-z][\w-]*)\s*:\s*'", RegexOptions.Multiline)
                .Select(e => e.Groups[1].Value),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// <c>layouts/MainLayout.vue</c> icindeki <c>NAV_GROUPS</c> haritasinda gecen tum bolum kimlikleri.
    /// </summary>
    private static SortedSet<string> GruplananKimlikler()
    {
        var govde = TekBlok(MainLayoutKaynagi(), @"const\s+NAV_GROUPS\b[^=]*=\s*\[(.*?)\n\];", "NAV_GROUPS");

        var kimlikler = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match liste in Regex.Matches(govde, @"ids:\s*\[([^\]]*)\]", RegexOptions.Singleline))
        {
            foreach (Match kimlik in Regex.Matches(liste.Groups[1].Value, @"""([A-Za-z][\w-]*)"""))
            {
                kimlikler.Add(kimlik.Groups[1].Value);
            }
        }

        return kimlikler;
    }

    private static string MainLayoutKaynagi()
        => YorumlariSil(File.ReadAllText(Path.Combine(KaynakKoku(), "frontend", "src", "layouts", "MainLayout.vue")));

    private static string SetupWizardYolu()
        => Path.Combine(KaynakKoku(), "frontend", "src", "views", "SetupWizardView.vue");

    private static string TekBlok(string kaynak, string kalip, string ad)
    {
        var eslesme = Regex.Match(kaynak, kalip, RegexOptions.Singleline);
        Assert.True(eslesme.Success, $"{ad} blogu MainLayout.vue icinde bulunamadi (yeniden adlandirildi mi?).");
        return eslesme.Groups[1].Value;
    }

    /// <summary>
    /// Blok yorumlarini, HTML yorumlarini ve TAM SATIR / dize disi satir yorumlarini siler.
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
