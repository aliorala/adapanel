using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Git;
using AdaPanel.Infrastructure.Htaccess.Hosting;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Scheduler;
using AdaPanel.Infrastructure.Seo;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Windows AYRILMIŞ AYGIT ADLARI (<c>CON</c>, <c>NUL</c>, <c>PRN</c>, <c>AUX</c>, <c>COM1..9</c>,
/// <c>LPT1..9</c>) — kuralın kendisi ve onu kullanan BÜTÜN ad üreticileri.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ölçülen arıza:</b> deponun bütün temizleyicileri <c>Path.GetInvalidFileNameChars()</c>
/// tabanlıydı ve o liste aygıt adlarını BİLMEZ — "CON" içinde geçersiz KARAKTER yoktur. Sonuç
/// sessiz veri kaybı değil GÜRÜLTÜLÜ ÇÖKMEYDİ: "CON" adlı bir site açılabiliyor, o siteye ait
/// yedek / zamanlanmış görev / SEO / Git işlemleri sert hatayla düşüyordu.
/// </para>
/// <para>
/// <b>Testlerin çoğu DİSKE YAZAR ve bu bilinçlidir.</b> Aynı sınıftan bir testin daha önce
/// mutasyonda hayatta kaldığı ölçüldü (<c>HtaccessHostingPathTests</c>): "ad ayrılmış değil"
/// diye bakmak yetmiyor, çünkü adı kurtaran şey başka bir adım olabiliyor. Tek güvenilir ölçüt
/// NİHAİ GERÇEKTİR: bu adla dosya/klasör AÇILABİLİYOR MU.
/// </para>
/// </remarks>
public sealed class WindowsReservedNameTests
{
    // --- 1) Kuralın kendisi ---

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("CoN")]
    [InlineData("NUL")]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("CLOCK$")]
    [InlineData("COM1")]
    [InlineData("COM9")]
    [InlineData("LPT1")]
    [InlineData("LPT9")]
    public void AygitAdlariTaniniyor(string ad)
    {
        Assert.True(WindowsReservedNames.IsReserved(ad));
    }

    /// <summary>
    /// UZANTILI biçim de aygıttır — kural, İLK noktadan önceki parçaya bakar.
    /// </summary>
    /// <remarks>
    /// Bu, düzeltmenin en kolay kaçırılan tarafı ve pratikte EN SIK karşılaşılan hâli: panelin
    /// depoları adın sonuna her zaman bir uzantı ekliyor (<c>.json</c>, <c>.log</c>). Yani "CON"
    /// adlı bir site için gerçekte denenen ad hiçbir zaman "CON" değil, "CON.json" oluyor. Son
    /// noktaya bakan bir kontrol ("CON.json"un uzantısını at → "CON") tesadüfen doğru cevabı verir
    /// ama "CON.a.b" için kaçırırdı.
    /// </remarks>
    [Theory]
    [InlineData("con.txt")]
    [InlineData("CON.json")]
    [InlineData("nul.log")]
    [InlineData("com1.switch")]
    [InlineData("con.a.b")]
    [InlineData("con.")]
    public void UzantiliBicimDeAygittir(string ad)
    {
        Assert.True(WindowsReservedNames.IsReserved(ad));
    }

    /// <summary>
    /// Baştaki/sondaki boşlukla atlatılamaz: Windows dosya adının kenar boşluklarını yok sayar,
    /// yani "CON " açılmaya çalışıldığında yine aygıta gider.
    /// </summary>
    [Theory]
    [InlineData("CON ")]
    [InlineData(" con")]
    [InlineData("  NUL  ")]
    public void BoslukEkleyerekAtlatilamaz(string ad)
    {
        Assert.True(WindowsReservedNames.IsReserved(ad));
    }

    /// <summary>
    /// AŞIRI GENİŞ olmamalı. Kural bir KULLANILABİLİRLİK düzeltmesidir; masum adları reddetmek,
    /// çözdüğünden daha çok kullanıcı engellerdi.
    /// </summary>
    [Theory]
    [InlineData("COM0")]
    [InlineData("LPT0")]
    [InlineData("COM10")]
    [InlineData("COMA")]
    [InlineData("CONSOLE")]
    [InlineData("CONS")]
    [InlineData("XCON")]
    [InlineData("con-a1b2c3d4")]
    [InlineData("my.con")]
    [InlineData("")]
    [InlineData("   ")]
    public void AygitOlmayanAdlarKabulEdilir(string ad)
    {
        Assert.False(WindowsReservedNames.IsReserved(ad));
    }

    [Fact]
    public void NullAygitDegildir()
    {
        Assert.False(WindowsReservedNames.IsReserved(null));
        Assert.Equal(string.Empty, WindowsReservedNames.MakeSafe(null));
    }

    /// <summary>Aygıt olmayan ad AYNEN döner: temizleyici masum adı bozmamalı.</summary>
    [Theory]
    [InlineData("ornek.com")]
    [InlineData("CONSOLE")]
    [InlineData("gece-yedegi")]
    public void AygitOlmayanAdDegistirilmez(string ad)
    {
        Assert.Equal(ad, WindowsReservedNames.MakeSafe(ad));
    }

    /// <summary>Ek UZANTIDAN ÖNCE gelir; sona konsaydı JSON deposu dosyayı bir daha bulamazdı.</summary>
    [Fact]
    public void EkUzantidanOnceGelir()
    {
        Assert.Equal("con_.json", WindowsReservedNames.MakeSafe("con.json"));
        Assert.Equal("CON_", WindowsReservedNames.MakeSafe("CON"));
        Assert.EndsWith(".json", WindowsReservedNames.MakeSafe("con.json"), StringComparison.Ordinal);
    }

    /// <summary>Sonuç bir daha ayrılmış OLMAMALI — aksi hâlde düzeltme hiçbir şey düzeltmezdi.</summary>
    [Theory]
    [InlineData("CON")]
    [InlineData("nul.log")]
    [InlineData("COM1")]
    public void SonucArtikAygitDegildir(string ad)
    {
        Assert.False(WindowsReservedNames.IsReserved(WindowsReservedNames.MakeSafe(ad)));
    }

    // --- 2) NİHAİ GERÇEK: bu adla diskte dosya/klasör açılabiliyor mu? ---

    /// <summary>
    /// ÖLÇÜM: ham adla yazılan veri KAYBOLUYOR, düzeltilmiş adla KALICI oluyor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Bu testin ilk sürümü YANLIŞTI ve ölçüm düzeltti.</b> "Ham ad istisna atar" diye
    /// yazılmıştı; çalıştırıldığında 7 girdinin 5'i (CON, nul, aux, COM1, con.json) İSTİSNA
    /// ATMADI — <c>File.WriteAllText</c> "başarılı" döndü ve dosya HİÇ OLUŞMADI. Yani düz yazımda
    /// etki gürültülü çökme değil SESSİZ VERİ KAYBI. (PRN ve LPT9 bu makinede bağlı aygıt
    /// olmadığından gürültülü düştü — hangi tarafa düştüğü DONANIMA bağlı, kurala değil.)
    /// </para>
    /// <para>
    /// Bu yüzden ölçüt "istisna attı mı" DEĞİL, kullanıcının umursadığı şey: <b>veri orada mı?</b>
    /// İki davranışı da tek kural kapsar: yazma ya patlar ya sessizce yutar; sonuçta dosya YOKTUR.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("PRN")]
    [InlineData("aux")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    [InlineData("con.json")]
    public void HamAdaYazilanVeriKayboluyor_DuzeltilmisAdaYazilanKaliyor(string ad)
    {
        var dizin = Directory.CreateTempSubdirectory("adapanel-aygit-ad");
        try
        {
            var ham = Path.Combine(dizin.FullName, ad);
            try
            {
                File.WriteAllText(ham, "x");
            }
            catch (Exception)
            {
                // Gürültülü taraf: bağlı aygıtı olmayan adlar (PRN, LPT9) burada düşer.
            }

            Assert.False(File.Exists(ham), $"AYGIT ADI ARTIK DOSYA OLUYOR: {ad}");

            var guvenli = Path.Combine(dizin.FullName, WindowsReservedNames.MakeSafe(ad));
            File.WriteAllText(guvenli, "x");
            Assert.True(File.Exists(guvenli));
            Assert.Equal("x", File.ReadAllText(guvenli));
        }
        finally
        {
            dizin.Delete(recursive: true);
        }
    }

    /// <summary>
    /// KLASÖR tarafı gürültülü: <c>CreateDirectory</c> aygıt adında istisna atar. Yedek klasörü
    /// tam olarak bu yoldan düşüyordu.
    /// </summary>
    [Theory]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("COM1")]
    [InlineData("con.json")]
    public void HamAdlaKlasorOlusturulamiyor(string ad)
    {
        var dizin = Directory.CreateTempSubdirectory("adapanel-aygit-dizin");
        try
        {
            Assert.ThrowsAny<Exception>(() => Directory.CreateDirectory(Path.Combine(dizin.FullName, ad)));

            var guvenli = Path.Combine(dizin.FullName, WindowsReservedNames.MakeSafe(ad));
            Directory.CreateDirectory(guvenli);
            Assert.True(Directory.Exists(guvenli));
        }
        finally
        {
            dizin.Delete(recursive: true);
        }
    }

    /// <summary>
    /// ATOMİK YAZIM (geçici dosya + <c>File.Move</c>) — panelin JSON depolarının gerçekten
    /// kullandığı yol. Burada etki gürültülüdür ve önceki denetimin kaydettiği hata budur.
    /// </summary>
    [Theory]
    [InlineData("CON.json")]
    [InlineData("nul.json")]
    public void HamAdaAtomikYazimDusuyor(string dosyaAdi)
    {
        var dizin = Directory.CreateTempSubdirectory("adapanel-aygit-atomik");
        try
        {
            Assert.ThrowsAny<Exception>(() =>
            {
                var hedef = Path.Combine(dizin.FullName, dosyaAdi);
                var gecici = hedef + ".tmp";
                File.WriteAllText(gecici, "{}");
                File.Move(gecici, hedef, overwrite: true);
            });

            var guvenliHedef = Path.Combine(dizin.FullName, WindowsReservedNames.MakeSafe(dosyaAdi));
            var guvenliGecici = guvenliHedef + ".tmp";
            File.WriteAllText(guvenliGecici, "{}");
            File.Move(guvenliGecici, guvenliHedef, overwrite: true);
            Assert.True(File.Exists(guvenliHedef));
        }
        finally
        {
            dizin.Delete(recursive: true);
        }
    }

    // --- 3) Ad ÜRETEN her yer bu kuraldan geçiyor mu? (uçtan uca, diske yazarak) ---

    /// <summary>
    /// Yedek klasörü: "CON" adlı bir sitenin yedeği <c>CreateDirectory</c> istisnasıyla düşüyordu.
    /// </summary>
    [Theory]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("LPT3")]
    [InlineData("con.yedek")]
    public void YedekYolParcasiGercektenOlusturulabilir(string siteAdi)
    {
        var kok = Directory.CreateTempSubdirectory("adapanel-yedek-ad");
        try
        {
            var yol = Path.Combine(kok.FullName, BackupPathName.Sanitize(siteAdi, "yedek"));
            Directory.CreateDirectory(yol);
            Assert.True(Directory.Exists(yol));
        }
        finally
        {
            kok.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Görev günlüğü. Özet SON EKİ tuzağı çoğu durumda zaten kırıyordu ("CON" →
    /// "CON-a1b2c3d4.log"); NOKTALI görev adlarında KIRMIYORDU ve asıl açık oradaydı.
    /// </summary>
    [Theory]
    [InlineData("CON")]
    [InlineData("CON.txt")]
    [InlineData("nul.")]
    [InlineData("com1.gece")]
    public void GorevGunlukAdiGercektenYazilabilir(string gorevAdi)
    {
        var kok = Directory.CreateTempSubdirectory("adapanel-gorev-ad");
        try
        {
            var yol = Path.Combine(kok.FullName, SchedulerService.ResolveLogFileName(gorevAdi));
            File.WriteAllText(yol, "cikti");
            Assert.True(File.Exists(yol));
        }
        finally
        {
            kok.Delete(recursive: true);
        }
    }

    /// <summary>
    /// SEO raporu ve Git ayarı: ikisi de adın SONUNA ".json" ekliyor, yani "CON" adlı bir site
    /// için gerçekte denenen ad "CON.json" — ve o da aygıttır.
    /// </summary>
    [Theory]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("COM4")]
    public void SeoVeGitDosyaAdlariGercektenYazilabilir(string siteAdi)
    {
        var kok = Directory.CreateTempSubdirectory("adapanel-depo-ad");
        try
        {
            // Depolar sanal yol döndürür ("config/seo/<ad>.json"); diske yazılan şey SON parçadır.
            foreach (var sanalYol in new[] { SeoReportStore.Path(siteAdi), GitDeploymentService.ConfigPath(siteAdi) })
            {
                var dosyaAdi = sanalYol[(sanalYol.LastIndexOf('/') + 1)..];
                var yol = Path.Combine(kok.FullName, dosyaAdi);
                File.WriteAllText(yol, "{}");
                Assert.True(File.Exists(yol));
            }
        }
        finally
        {
            kok.Delete(recursive: true);
        }
    }

    // --- 4) Asıl kapı: aygıt adlı SİTE hiç açılmasın ---

    /// <summary>
    /// Yeni site adı aygıt adıysa REDDEDİLİR. Aşağı akıştaki etkisizleştirme ikinci savunma
    /// hattıdır; kullanıcının doğru cevabı almasının yeri burasıdır.
    /// </summary>
    [Theory]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("COM1")]
    [InlineData("con.tr")]
    public void YeniSiteAdiAygitOlamaz(string ad)
    {
        var hata = Assert.Throws<AppException>(() =>
            WebAdministrationSiteService.NormalizeSiteName(ad, yeniAd: true));

        Assert.Equal("sites.reserved_device_name", hata.Message);
    }

    /// <summary>
    /// VAR OLAN aygıt adlı site okunabilir ve SİLİNEBİLİR olmalı.
    /// </summary>
    /// <remarks>
    /// Bu, düzeltmenin kendisinden daha önemli bir kural: kontrol her yolda çalışsaydı, bu kural
    /// konmadan ÖNCE açılmış "CON" adlı bir site panelden silinemez hâle gelirdi — kullanıcı,
    /// kurtulmak istediği bozuk siteye kilitlenirdi. Yani düzeltme, düzeltmeye çalıştığı sorunun
    /// daha kötü bir sürümünü üretirdi.
    /// </remarks>
    [Theory]
    [InlineData("CON")]
    [InlineData("COM1")]
    public void VarOlanAygitAdliSiteHalaCozulebilir(string ad)
    {
        Assert.Equal(ad, WebAdministrationSiteService.NormalizeSiteName(ad));
    }

    [Fact]
    public void BosSiteAdiHalaGecersiz()
    {
        Assert.Throws<AppException>(() => WebAdministrationSiteService.NormalizeSiteName("   ", yeniAd: true));
        Assert.Throws<AppException>(() => WebAdministrationSiteService.NormalizeSiteName("   "));
    }

    /// <summary>
    /// SAPMA NÖBETÇİSİ: site OLUŞTURMA yolu kontrolü gerçekten AÇIYOR mu?
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kontrol varsayılan olarak KAPALI (<c>yeniAd: false</c>) — çünkü okuma/silme yollarını
    /// kırmaması gerekiyor. Bu, sessiz bir sapma riski demektir: birisi oluşturma yolundaki
    /// <c>yeniAd: true</c>'yu düşürürse KİMSE FARK ETMEZ, bütün testler yeşil kalır ve özellik
    /// hiç çalışmadan geri döner. Tam olarak bu depoda "üç ekip paralel" koşullarında yaşanan
    /// hata sınıfı.
    /// </para>
    /// <para>
    /// Çağrı IIS gerektirdiği için (<c>Microsoft.Web.Administration</c> bu makinede yok)
    /// davranış koşulamaz; ölçülebilen tek şey KAYNAKTAKİ bağdır. Kaba ama gerçek bir nöbetçi:
    /// bağ koptuğunda kırmızıya döner.
    /// </para>
    /// </remarks>
    [Fact]
    public void OlusturmaYoluAygitAdiKontrolunuAciyor()
    {
        var kaynak = File.ReadAllText(KaynakDosyaYolu("src/AdaPanel.Infrastructure/Iis/WebAdministrationSiteService.cs"));
        Assert.Contains("NormalizeSiteName(request.Name, yeniAd: true)", kaynak, StringComparison.Ordinal);
    }

    // --- 5) TEK KAYNAK NÖBETÇİSİ ---

    /// <summary>
    /// Ayrılmış ad listesi depoda TEK YERDE yazılı olmalı.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden bu testin yerini bir "parite" testi TUTMUYOR:</b> burada eskiden
    /// <c>HtaccessHostingPaths.IsReservedDeviceName</c> ile bu kuralın AYNI cevabı verdiğini
    /// karşılaştıran bir test vardı. O test iki kopyayı hizada tutuyordu ama kopyanın KENDİSİNİ
    /// meşrulaştırıyordu: sapmanın maliyeti ödenmeye devam ediyordu ve nöbetçi yalnız kendisine
    /// verilen sınır girdileri kadar iyiydi. Kopya artık YOK — <c>WindowsReservedNames.cs</c> net48
    /// modül projesine de kaynak olarak bağlandı (<c>AdaPanel.HtaccessModule.csproj</c>), yani
    /// bağlantı düşerse derleme kırılır, sessizce ayrışan bir ikinci liste oluşamaz.
    /// </para>
    /// <para>
    /// Bu test o yüzden farklı bir soruyu sorar: <b>ÜÇÜNCÜ bir kopya doğdu mu.</b> Bu depoda
    /// "elle yazılmış liste kayar" hatası beş kez ısırdı; en ucuz nöbetçi, listenin imzasını
    /// (<c>CLOCK$</c> dalı) kaynakta SAYMAKTIR.
    /// </para>
    /// </remarks>
    [Fact]
    public void AyrilmisAdListesiDepodaTekYerdeYazili()
    {
        // Kural dosyasının kendi yolundan depo köküne çıkılıyor: ayrı bir kök bulucu yazmak,
        // "yol tahmin etme" hatasının bir kopyasını daha üretmek olurdu.
        var kaynakKok = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(KaynakDosyaYolu("src/AdaPanel.Core/Common/WindowsReservedNames.cs"))!,
            "..",
            ".."));

        var kopyalar = Directory
            .EnumerateFiles(kaynakKok, "*.cs", SearchOption.AllDirectories)
            // Derleme çıktıları ELENİR: obj/ altındaki üretilmiş dosyalar kaynak değildir ve
            // makineden makineye değişir — nöbetçinin cevabı makineye bağlı olmamalı.
            .Where(yol => !yol.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                       && !yol.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(yol => File.ReadAllText(yol).Contains("\"CLOCK$\"", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .OrderBy(ad => ad, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "WindowsReservedNames.cs" }, kopyalar);
    }

    /// <summary>Depo kökünü test derlemesinin yerinden bulur (çıktı klasörü değişse de çalışır).</summary>
    private static string KaynakDosyaYolu(string goreliYol)
    {
        var dizin = new DirectoryInfo(AppContext.BaseDirectory);
        while (dizin is not null && !File.Exists(Path.Combine(dizin.FullName, "AdaPanel.sln")))
        {
            dizin = dizin.Parent;
        }

        Assert.NotNull(dizin);
        var yol = Path.Combine(dizin!.FullName, goreliYol.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(yol), $"kaynak dosya bulunamadı: {yol}");
        return yol;
    }
}
