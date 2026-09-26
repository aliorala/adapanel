using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.SiteBuilder;
using AdaPanel.Infrastructure.SiteBuilder;
using Xunit;

// Ortam ve dosya sistemi yardımcıları ORTAK: SiteBuilderTestOrtami.cs.
// Takma ad, testlerdeki `TestOrtami.` çağrılarını olduğu gibi bırakır — kurulum tek kaynağa
// çıkarken testlerin okunuşu değişmesin.
using TestOrtami = AdaPanel.Infrastructure.Tests.SiteBuilderTestOrtami;
using static AdaPanel.Infrastructure.Tests.SiteBuilderTestOrtami;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// "Yayınla" MÜŞTERİNİN DOSYALARININ ÜZERİNE YAZAR — bu testler o işlemin sınırlarını sabitler.
/// </summary>
/// <remarks>
/// <para>
/// Bu depoda özyinelemeli silme bir kez müşteri sitesini silebilecek hataya yol açtı; yayın da
/// aynı hata sınıfının içinde yaşıyor. Korunan dört şey:
/// </para>
/// <list type="number">
/// <item>Hedef klasör KİMİN: yol istekten değil, IIS'ten çözülür.</item>
/// <item>Kapsam dışına yazma yok: <c>..</c> ve mutlak yol reddedilir.</item>
/// <item>Bağlantı noktası (junction/symlink) İZLENMEZ.</item>
/// <item>Var olan dosya, AÇIK ONAY olmadan ezilmez; hiçbir dosya SİLİNMEZ.</item>
/// </list>
/// </remarks>
public sealed class SiteBuilderPublishSafetyTests
{
    // ------------------------------------------------------------------ saf karar

    [Fact]
    public void Var_olan_YABANCI_dosya_onay_ISTER()
    {
        var karar = SiteBuilderPublishPlanner.Decide(
            filesToWrite: ["index.html", "assets/adapanel/site.css"],
            existingRelativePaths: ["index.html"],
            managedFiles: [],
            overwriteConfirmed: false);

        Assert.True(karar.RequiresConfirmation);
        Assert.False(karar.Allowed);
        Assert.Equal(new[] { "index.html" }, karar.Conflicts);
    }

    [Fact]
    public void Onay_verilince_yayin_gecer()
    {
        var karar = SiteBuilderPublishPlanner.Decide(
            filesToWrite: ["index.html"],
            existingRelativePaths: ["index.html"],
            managedFiles: [],
            overwriteConfirmed: true);

        Assert.True(karar.RequiresConfirmation);
        Assert.True(karar.Allowed);
    }

    [Fact]
    public void Kendi_yazdigimiz_dosya_cakisma_SAYILMAZ()
    {
        // Aksi hâlde her yayında uyarı çıkar; her seferinde çıkan uyarı okunmaz hâle gelir ve
        // GERÇEK bir üzerine yazma anında da tıklanıp geçilir.
        var karar = SiteBuilderPublishPlanner.Decide(
            filesToWrite: ["index.html", "assets/adapanel/site.css"],
            existingRelativePaths: ["index.html", "assets/adapanel/site.css"],
            managedFiles: ["index.html", "assets/adapanel/site.css"],
            overwriteConfirmed: false);

        Assert.False(karar.RequiresConfirmation);
        Assert.True(karar.Allowed);
        Assert.Empty(karar.Conflicts);
    }

    [Fact]
    public void Yol_ayirici_farki_cakismayi_GIZLEYEMEZ()
    {
        // Diskte "assets\adapanel\site.css", yazma listesinde "assets/adapanel/site.css".
        // Birleştirilmezse dosya sessizce ezilir — uyarı hiç görünmez.
        var karar = SiteBuilderPublishPlanner.Decide(
            filesToWrite: ["assets/adapanel/site.css"],
            existingRelativePaths: ["assets\\adapanel\\site.css"],
            managedFiles: [],
            overwriteConfirmed: false);

        Assert.True(karar.RequiresConfirmation);
        Assert.Single(karar.Conflicts);
    }

    [Fact]
    public void Yonetilen_dosya_esleşmesi_buyuk_kucuk_harften_etkilenmez()
    {
        var karar = SiteBuilderPublishPlanner.Decide(
            filesToWrite: ["index.html"],
            existingRelativePaths: ["INDEX.HTML"],
            managedFiles: ["Index.Html"],
            overwriteConfirmed: false);

        Assert.False(karar.RequiresConfirmation);
    }

    [Fact]
    public void KORUMALI_yollar_yazma_listesine_GIREMEZ()
    {
        // .well-known silinmesi/ezilmesi Let's Encrypt doğrulamasını bozar (Plesk/Sitejet
        // tarafında bilinen arıza). web.config ezilirse sitenin yapılandırması gider.
        var karar = SiteBuilderPublishPlanner.Decide(
            filesToWrite: ["index.html", ".well-known/acme-challenge/x", "web.config", "App_Data/db.sdf"],
            existingRelativePaths: [],
            managedFiles: [],
            overwriteConfirmed: true);

        Assert.Equal(new[] { "index.html" }, karar.FilesToWrite);
    }

    [Fact]
    public void Plan_hicbir_SILME_uretmez()
    {
        // Karar nesnesi yalnız YAZILACAK dosyaları taşır; "silinecek" diye bir kavram yok.
        // Eskiyen bir dosyayı diskte bırakmak, yanlış dosyayı silmekten her zaman iyidir.
        var karar = SiteBuilderPublishPlanner.Decide(
            filesToWrite: ["index.html"],
            existingRelativePaths: ["eski-sayfa.html", "musteri-verisi.pdf"],
            managedFiles: ["eski-sayfa.html"],
            overwriteConfirmed: true);

        Assert.Equal(new[] { "index.html" }, karar.FilesToWrite);
        Assert.Empty(karar.Conflicts);
        Assert.DoesNotContain("musteri-verisi.pdf", string.Join(",", karar.FilesToWrite), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ yol kapısı

    [Fact]
    public void Kapsam_disina_cikan_yol_REDDEDILIR()
    {
        var kok = GeciciKlasor();
        try
        {
            foreach (var kotu in new[] { "../disarisi.html", "..\\disarisi.html", "a/../../disarisi.html" })
            {
                Assert.Throws<AppException>(() => SiteBuilderPathGuard.ResolveWriteTarget(kok, kotu));
            }
        }
        finally
        {
            Temizle(kok);
        }
    }

    [Fact]
    public void Kardes_klasor_ON_EK_benzerligiyle_gecemez()
    {
        // "C:\sites\site1" ile "C:\sites\site1-evil" duz StartsWith ile eslesir.
        var taban = GeciciKlasor();
        try
        {
            var kok = Path.Combine(taban, "site1");
            var komsu = Path.Combine(taban, "site1-evil");
            Directory.CreateDirectory(kok);
            Directory.CreateDirectory(komsu);

            Assert.Throws<AppException>(() =>
                SiteBuilderPathGuard.ResolveWriteTarget(kok, "../site1-evil/index.html"));
        }
        finally
        {
            Temizle(taban);
        }
    }

    [Fact]
    public void Korumali_yol_dogrudan_cozulmeye_calisilirsa_REDDEDILIR()
    {
        var kok = GeciciKlasor();
        try
        {
            Assert.Throws<AppException>(() =>
                SiteBuilderPathGuard.ResolveWriteTarget(kok, ".well-known/acme-challenge/x"));
        }
        finally
        {
            Temizle(kok);
        }
    }

    [Fact]
    public void KAVSAK_uzerinden_yazma_REDDEDILIR()
    {
        var taban = GeciciKlasor();
        try
        {
            var kok = Path.Combine(taban, "site");
            var disari = Path.Combine(taban, "baska-musteri");
            Directory.CreateDirectory(kok);
            Directory.CreateDirectory(disari);

            // Site kökünün altındaki "varliklar" aslında BAŞKA bir müşterinin klasörüne bakıyor.
            var kavsak = Path.Combine(kok, "varliklar");
            // ATLANMIYOR, ZORUNLU: Windows'ta kavsak (junction) oluşturmak yükseltilmiş yetki
            // İSTEMEZ (sembolik bağlantı ister, kavşak istemez). Kuramazsak bu korumayı
            // ÖLÇEMEMİŞİZDİR; testi sessizce atlamak, korumanın çalıştığı yanılsamasını bırakırdı.
            Assert.True(KavsakKur(kavsak, disari), "Kavsak olusturulamadi; koruma OLCULEMEDI.");

            var istisna = Assert.Throws<AppException>(() =>
                SiteBuilderPathGuard.ResolveWriteTarget(kok, "varliklar/index.html"));
            Assert.Equal("sitebuilder.target_is_link", istisna.MessageKey);
        }
        finally
        {
            Temizle(taban);
        }
    }

    [Fact]
    public void Site_KOKUNUN_KENDISI_kavsaksa_yayin_hic_baslamaz()
    {
        var taban = GeciciKlasor();
        try
        {
            var gercek = Path.Combine(taban, "gercek");
            var kok = Path.Combine(taban, "kok-baglanti");
            Directory.CreateDirectory(gercek);
            Assert.True(KavsakKur(kok, gercek), "Kavsak olusturulamadi; koruma OLCULEMEDI.");

            var istisna = Assert.Throws<AppException>(() => SiteBuilderPathGuard.EnsureUsableRoot(kok));
            Assert.Equal("sitebuilder.target_is_link", istisna.MessageKey);
        }
        finally
        {
            Temizle(taban);
        }
    }

    [Fact]
    public void Goreli_kok_KABUL_EDILMEZ()
    {
        // Göreli yol, sürecin o anki çalışma dizinine göre çözülür ve sunucuda tahmin edilemez.
        Assert.Throws<AppException>(() => SiteBuilderPathGuard.EnsureUsableRoot("httpdocs"));
        Assert.Throws<AppException>(() => SiteBuilderPathGuard.EnsureUsableRoot(""));
    }

    // ------------------------------------------------------------------ uçtan uca

    [Fact]
    public async Task Yayin_musterinin_dosyasini_ONAYSIZ_ezmez_ve_HICBIR_SEY_silmez()
    {
        await using var ortam = await TestOrtami.KurAsync();

        var musteriDosyasi = Path.Combine(ortam.SiteKoku, "index.html");
        await File.WriteAllTextAsync(musteriDosyasi, "MUSTERININ ELLE YAZDIGI SAYFA");
        var digerDosya = Path.Combine(ortam.SiteKoku, "fatura.pdf");
        await File.WriteAllTextAsync(digerDosya, "onemli veri");

        var istisna = await Assert.ThrowsAsync<AppException>(() =>
            ortam.Servis.PublishAsync(TestOrtami.SiteAdi, new PublishSiteBuilderRequest { OverwriteExistingFiles = false }));

        Assert.Equal("sitebuilder.overwrite_confirmation_required", istisna.MessageKey);

        // Reddedilen yayın DİSKE HİÇ DOKUNMAMIŞ olmalı.
        Assert.Equal("MUSTERININ ELLE YAZDIGI SAYFA", await File.ReadAllTextAsync(musteriDosyasi));
        Assert.True(File.Exists(digerDosya));
        Assert.False(Directory.Exists(Path.Combine(ortam.SiteKoku, "assets")));
    }

    [Fact]
    public async Task Onayla_yayin_yazar_ama_ILGISIZ_dosyalara_dokunmaz()
    {
        await using var ortam = await TestOrtami.KurAsync();

        var musteriDosyasi = Path.Combine(ortam.SiteKoku, "index.html");
        await File.WriteAllTextAsync(musteriDosyasi, "eski");
        var korunan = Path.Combine(ortam.SiteKoku, "fatura.pdf");
        await File.WriteAllTextAsync(korunan, "onemli veri");
        var wellKnown = Path.Combine(ortam.SiteKoku, ".well-known");
        Directory.CreateDirectory(wellKnown);
        var dogrulama = Path.Combine(wellKnown, "acme-challenge.txt");
        await File.WriteAllTextAsync(dogrulama, "le-token");

        var sonuc = await ortam.Servis.PublishAsync(
            TestOrtami.SiteAdi,
            new PublishSiteBuilderRequest { OverwriteExistingFiles = true });

        Assert.Contains("index.html", sonuc.WrittenFiles);
        Assert.Contains("index.html", sonuc.OverwrittenFiles);
        Assert.Contains("<!DOCTYPE html>", await File.ReadAllTextAsync(musteriDosyasi), StringComparison.Ordinal);

        // Yayının DOKUNMADIĞI her şey aynen duruyor.
        Assert.Equal("onemli veri", await File.ReadAllTextAsync(korunan));
        Assert.Equal("le-token", await File.ReadAllTextAsync(dogrulama));
        Assert.True(File.Exists(Path.Combine(ortam.SiteKoku, "assets", "adapanel", "site.css")));
    }

    [Fact]
    public async Task Ikinci_yayin_kendi_dosyamiz_icin_onay_ISTEMEZ()
    {
        await using var ortam = await TestOrtami.KurAsync();

        await ortam.Servis.PublishAsync(TestOrtami.SiteAdi, new PublishSiteBuilderRequest());

        var plan = await ortam.Servis.PlanPublishAsync(TestOrtami.SiteAdi);
        Assert.False(plan.RequiresConfirmation);
        Assert.Empty(plan.Conflicts);

        // Ve gerçekten yeniden yayınlanabiliyor (onaysız).
        var ikinci = await ortam.Servis.PublishAsync(TestOrtami.SiteAdi, new PublishSiteBuilderRequest());
        Assert.Contains("index.html", ikinci.WrittenFiles);
    }

    [Fact]
    public async Task Plan_DISKE_YAZMAZ()
    {
        await using var ortam = await TestOrtami.KurAsync();

        var oncekiler = Directory.GetFileSystemEntries(ortam.SiteKoku);
        var plan = await ortam.Servis.PlanPublishAsync(TestOrtami.SiteAdi);

        Assert.Equal(ortam.SiteKoku, plan.TargetPath);
        Assert.Contains("index.html", plan.FilesToWrite);
        Assert.Equal(oncekiler.Length, Directory.GetFileSystemEntries(ortam.SiteKoku).Length);
    }

    [Fact]
    public async Task Panelin_KENDI_siteleri_kurucuya_kapali()
    {
        await using var ortam = await TestOrtami.KurAsync();

        foreach (var korunanAd in new[] { "AdaPanel.Webmail", "adapanel.WebDb.PhpMyAdmin", "Default Web Site" })
        {
            var istisna = await Assert.ThrowsAsync<AppException>(() =>
                ortam.Servis.PublishAsync(korunanAd, new PublishSiteBuilderRequest { OverwriteExistingFiles = true }));
            Assert.Equal("sitebuilder.site_reserved", istisna.MessageKey);
        }
    }

    [Theory]
    [InlineData("adapanel.host")]
    [InlineData("AdaPanel.example")]
    public async Task Public_domain_with_panel_prefix_can_build_and_publish(string domain)
    {
        await using var ortam = await TestOrtami.KurAsync();
        var project = await ortam.Servis.GetProjectAsync(domain);
        Assert.NotNull(project);
        var result = await ortam.Servis.PublishAsync(domain, new PublishSiteBuilderRequest());
        Assert.Contains("index.html", result.WrittenFiles);
        Assert.True(File.Exists(Path.Combine(ortam.SiteKoku, "index.html")));
    }

    [Fact]
    public async Task Yayin_yalniz_TASARIMDA_KULLANILAN_gorselleri_kopyalar()
    {
        await using var ortam = await TestOrtami.KurAsync();

        var kullanilan = await ortam.GorselYukleAsync();
        var kullanilmayan = await ortam.GorselYukleAsync();

        await ortam.Servis.SaveProjectAsync(TestOrtami.SiteAdi, new SaveSiteBuilderProjectRequest
        {
            TemplateId = "sade",
            SiteTitle = "Ornek",
            Sections =
            [
                new SiteBuilderSectionDto { Type = "hero", Enabled = true, ImageName = kullanilan.Name }
            ]
        });

        await ortam.Servis.PublishAsync(TestOrtami.SiteAdi, new PublishSiteBuilderRequest { OverwriteExistingFiles = true });

        var gorselKlasoru = Path.Combine(ortam.SiteKoku, "assets", "adapanel", "img");
        Assert.True(File.Exists(Path.Combine(gorselKlasoru, kullanilan.Name)));
        Assert.False(File.Exists(Path.Combine(gorselKlasoru, kullanilmayan.Name)));
    }

    [Fact]
    public async Task Baska_bir_sitenin_gorseli_tasarima_YAZILAMAZ()
    {
        await using var ortam = await TestOrtami.KurAsync();
        var yabanci = new string('d', 32) + ".jpg";

        var tasarim = await ortam.Servis.SaveProjectAsync(TestOrtami.SiteAdi, new SaveSiteBuilderProjectRequest
        {
            SiteTitle = "Ornek",
            Sections = [new SiteBuilderSectionDto { Type = "hero", Enabled = true, ImageName = yabanci }]
        });

        Assert.Equal(string.Empty, tasarim.Sections.Single(bolum => bolum.Type == "hero").ImageName);
    }

    [Fact]
    public async Task Yayin_kaydi_yayinlanan_yolu_ve_dosyalari_DOGRU_bildirir()
    {
        await using var ortam = await TestOrtami.KurAsync();

        var sonuc = await ortam.Servis.PublishAsync(TestOrtami.SiteAdi, new PublishSiteBuilderRequest());

        // "Yayınlandı" demek yetmez; hangi dosyaların gerçekten yazıldığı doğrulanır.
        foreach (var göreli in sonuc.WrittenFiles)
        {
            Assert.True(File.Exists(Path.Combine(ortam.SiteKoku, göreli.Replace('/', Path.DirectorySeparatorChar))));
        }

        var tasarim = await ortam.Servis.GetProjectAsync(TestOrtami.SiteAdi);
        Assert.True(tasarim.Published);
        Assert.Equal(ortam.SiteKoku, tasarim.PublishedPath);
    }

    [Fact]
    public void Depolama_anahtari_farkli_site_adlarini_AYIRIR()
    {
        // "a b" ile "a_b" yalnız geçersiz karakter değişimiyle aynı dosyaya düşerdi: bir kiracının
        // tasarımı diğerininkini ezerdi.
        Assert.NotEqual(SiteBuilderService.StorageKey("a b"), SiteBuilderService.StorageKey("a_b"));
        Assert.NotEqual(SiteBuilderService.StorageKey(".."), SiteBuilderService.StorageKey("__"));

        // IIS site adları büyük/küçük harf duyarsızdır: aynı site iki tasarıma bölünmemeli.
        Assert.Equal(SiteBuilderService.StorageKey("Ornek.com"), SiteBuilderService.StorageKey("ornek.COM"));
    }

    // ------------------------------------------------------------------ altyapı
    //
    // Ortam kurulumu (geçici veri kökü + geçici site kökü + sahte IIS) ve kavşak/temizlik
    // yardımcıları ORTAK dosyaya taşındı: SiteBuilderTestOrtami.cs. Aynı kurulum çok sayfa ve
    // iletişim formu testlerinde de kullanılıyor; üç kopya tutulsaydı biri güncellenip diğerleri
    // geride kalır ve geride kalan kopya, gerçekte var olmayan bir dünyayı doğrulayan YEŞİL bir
    // test üretirdi.
}
