using AdaPanel.Infrastructure.Htaccess.Hosting;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Site adı -> anahtar dosyası adı eşlemesi.
///
/// Bu eşleme İKİ AYRI SÜREÇTE çalışıyor: panel (yazan) ve modül (okuyan). Uyuşmazlık,
/// "panel açtım der, modül hiç görmez" şeklinde SESSİZ bir arıza üretir. Bu yüzden buradaki
/// kurallar test altındadır.
/// </summary>
public sealed class HtaccessHostingPathTests
{
    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("NUL")]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    public void AYRILMIS_AYGIT_ADLI_SITE_ICIN_DOSYA_GERCEKTEN_OLUSTURULABILIR(string siteName)
    {
        // "con.switch" adli bir dosya Windows'ta OLUSTURULAMAZ. Koruma olmasaydi "con" adli bir
        // site icin panel dosya yazamaz, kullaniciya "acildi" derdi ve modul hicbir sey gormezdi.
        //
        // BU TEST DISKE YAZAR ve bu BILINCLI bir karar. Ilk surumu yalnizca
        // IsReservedDeviceName(key) == false diye bakiyordu ve MUTASYON TESTINDE HAYATTA KALDI:
        // ayrilmis-ad dali kaldirildiginda bile yesil kaliyordu. Sebep ogreticiydi -- adi
        // gercekte kurtaran sey o dal DEGIL, sonuna eklenen OZET ("con" -> "con-<ozet>", ki bu
        // artik ayrilmis bir ad degildir). Yani test yanlis seyi olcuyordu.
        // Simdi olculen sey nihai gercek: dosya ACILABILIYOR MU.
        var key = HtaccessHostingPaths.SafeSiteKey(siteName);
        var dizin = Directory.CreateTempSubdirectory("adapanel-htaccess-ad");
        try
        {
            var yol = Path.Combine(dizin.FullName, HtaccessHostingPaths.SiteSwitchFileName(siteName));
            File.WriteAllText(yol, "enabled=1\n");

            Assert.True(File.Exists(yol), $"'{siteName}' -> '{key}' icin dosya olusturulamadi");
            Assert.Equal("enabled=1\n", File.ReadAllText(yol));
        }
        finally
        {
            dizin.Delete(recursive: true);
        }
    }

    [Fact]
    public void AYRILMIS_AD_TESPITI_UZANTIYI_GOZ_ARDI_EDER()
    {
        // Windows icin "con.switch" de "con" kadar ayrilmistir; kontrol NOKTADAN ONCESINE bakmali.
        Assert.True(HtaccessHostingPaths.IsReservedDeviceName("con.switch"));
        Assert.True(HtaccessHostingPaths.IsReservedDeviceName("NUL.txt"));
        Assert.False(HtaccessHostingPaths.IsReservedDeviceName("console"));
        Assert.False(HtaccessHostingPaths.IsReservedDeviceName("com10"));
    }

    [Theory]
    [InlineData("a:b", "a_b")]
    [InlineData("a b", "a-b")]
    [InlineData(@"..\..\evil", "evil")]
    [InlineData("x/y", "x\\y")]
    [InlineData("a?b", "a*b")]
    public void TEMIZLEME_SONRASI_CAKISAN_ADLAR_AYRI_DOSYALARA_DUSER(string first, string second)
    {
        // Temizleme tek basina YETMEZ: "a:b" ve "a_b" ayni ada duserdi ve bir kiracinin anahtari
        // digerininkini EZERDI -- bu bir kiraci izolasyonu acigi olurdu. Ozet cakismayi kirar.
        var keyA = HtaccessHostingPaths.SafeSiteKey(first);
        var keyB = HtaccessHostingPaths.SafeSiteKey(second);

        Assert.NotEqual(keyA, keyB);
    }

    [Fact]
    public void AYNI_AD_HER_ZAMAN_AYNI_ANAHTARI_URETIR()
    {
        // Panel ile modulun ayni dosyayi bulmasinin sarti. Ozet .NET'in randomize edilmis
        // string.GetHashCode'undan DEGIL, elde yazilmis FNV-1a'dan geliyor; aksi halde
        // net8 panel ile net48 modul FARKLI dosya adlari uretirdi.
        const string site = "musteri.example";

        var first = HtaccessHostingPaths.SafeSiteKey(site);
        var second = HtaccessHostingPaths.SafeSiteKey(site);

        Assert.Equal(first, second);

        // HARF DURUMU ve bosluk ONEMSIZ: IIS site adlari harf duyarsizdir, "SITE" ile "site"
        // AYNI sitedir ve AYNI anahtar dosyasina dusmek ZORUNDADIR. (Bunun tersini bekleyen
        // bir test satiri ilk turda yaziImisti ve haklı olarak kirmizi verdi.)
        Assert.Equal(first, HtaccessHostingPaths.SafeSiteKey(" MUSTERI.EXAMPLE "));
        Assert.Equal(
            HtaccessHostingPaths.SafeSiteKey("SITE"),
            HtaccessHostingPaths.SafeSiteKey("site"));
    }

    [Fact]
    public void OZET_SABIT_ve_SURUMDEN_BAGIMSIZDIR()
    {
        // Deger SABITLENDI: degisirse mevcut kurulumlardaki butun site anahtar dosyalari
        // "kayip" olur ve modul sessizce kapanir. Bu testin kirilmasi "dikkat, gocurmen lazim"
        // demektir -- degeri guncelleyip gecmek YANLIS olur.
        // Degerler OLCULDU (ilk kosumda ilki YANLIS tahmin edilmisti; test yakaladi) ve
        // FNV-1a 64-bit'in bilinen referans ciktilariyla eslesiyor:
        //   ""    -> offset basis     = cbf29ce484222325
        //   "foo" -> FNV-1a 64        = dcb27518fed9d577
        // Ilk yazilan "af63dc4c8601ec8c" aslinda FNV-1 (1a DEGIL) ciktisiydi; yani test yalniz
        // sabiti degil, ALGORITMANIN DOGRULUGUNU da tutuyor.
        Assert.Equal("dcb27518fed9d577", HtaccessHostingPaths.ShortDigest("foo"));
        Assert.Equal("cbf29ce484222325", HtaccessHostingPaths.ShortDigest(""));
    }

    [Fact]
    public void ANAHTAR_DOSYASI_YOLU_SITES_KLASORUNUN_ICINDE_KALIR()
    {
        // Yol kacisi: ".." iceren bir site adi dizin disina yazdirabilirdi.
        const string dataRoot = @"C:\ProgramData\AdaPanel";
        var sitesRoot = HtaccessHostingPaths.SitesRoot(dataRoot);

        foreach (var evil in new[] { @"..\..\windows\system32", "../../etc/passwd", @"C:\mutlak\yol" })
        {
            var path = HtaccessHostingPaths.SiteSwitchPath(dataRoot, evil);
            Assert.StartsWith(sitesRoot, path, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void UZUN_SITE_ADI_KIRPILIR_ama_BENZERSIZ_KALIR()
    {
        var uzunA = new string('a', 300);
        var uzunB = new string('a', 299) + "b";

        var keyA = HtaccessHostingPaths.SafeSiteKey(uzunA);
        var keyB = HtaccessHostingPaths.SafeSiteKey(uzunB);

        Assert.True(keyA.Length < 100, "dosya adi sinirlanmali");
        Assert.NotEqual(keyA, keyB); // kirpma benzersizligi BOZMAMALI
    }

    [Fact]
    public void BOS_SITE_ADI_GECERLI_BIR_DOSYA_ADI_URETIR()
    {
        var key = HtaccessHostingPaths.SafeSiteKey(string.Empty);

        Assert.NotEmpty(key);
        Assert.DoesNotContain('\\', key);
        Assert.DoesNotContain('/', key);
    }

    [Fact]
    public void VERI_KOKU_ORTAM_DEGISKENINDEN_OKUNUR()
    {
        // Modulun (w3wp icinde, DI kabi olmadan) veri kokunu bulmasinin tek yolu.
        const string name = "ADAPANEL_DATA_ROOT";
        var previous = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, @"D:\Ozel\AdaPanel");
            Assert.Equal(@"D:\Ozel\AdaPanel", HtaccessHostingPaths.ResolveDataRootFromEnvironment());

            Environment.SetEnvironmentVariable(name, null);
            var fallback = HtaccessHostingPaths.ResolveDataRootFromEnvironment();

            // Sabit "C:\ProgramData" YAZILMAMALI: sistem surucusu D: olan sunucularda yanlis olurdu.
            Assert.EndsWith("AdaPanel", fallback, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AdaPanel"),
                fallback);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }
}
