using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.AppStore;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// F4/F5 — WordPress klon/kopyalama yollarının DÜRÜSTLÜĞÜ.
/// </summary>
/// <remarks>
/// <para>
/// <b>F4:</b> URL arama-değiştir başarısız olduğunda ne çıkış kodu okunuyordu ne de <c>catch</c>
/// bir şey yapıyordu. Sonuç ağır: WordPress site adresini VERİTABANINDAN okur, dolayısıyla adresi
/// değişmemiş klon ÜRETİM ADRESİNİ servis eder — staging'de yapılan her işlem canlı siteye gider.
/// Panel ise "Klon tamamlandı" diyordu.
/// </para>
/// <para>
/// <b>F5:</b> yıkıcı <c>robocopy /MIR</c>'dan önce alınan yedeğin sonucu hiç denetlenmiyordu.
/// Yedek patlarsa (disk dolu, kilitli dosya, zaman aşımı) aynalama yine de hedefi eziyor ve geri
/// dönülecek hiçbir şey kalmıyordu.
/// </para>
/// <para>
/// Ortak kök: <b>çıkış kodunu okumamak</b>. Bu dosya o kökü iki yerde de kapatan yüklemleri ve
/// bayrağı doğrular.
/// </para>
/// </remarks>
public sealed class WordPressCopyHonestyTests
{
    // --- robocopy çıkış kodu yüklemi (F5'in ve klon kopyalamasının ortak temeli) ---

    [Theory]
    [InlineData(0)]  // değişiklik yok
    [InlineData(1)]  // dosyalar kopyalandı
    [InlineData(2)]  // hedefte fazladan dosya/klasör var
    [InlineData(3)]  // 1 + 2
    [InlineData(7)]  // en yüksek başarı kodu
    public void Robocopy_0_ile_7_arasi_BASARIDIR(int cikisKodu)
    {
        // robocopy'nin bit maskeli çıkış kodları: 0-7 normal sonuçlar. Bunları hata saymak meşru
        // kopyalamaları reddederdi — düzeltirken özelliği kapatmanın tipik yolu.
        Assert.True(WordPressToolkitService.RobocopyBasarili(cikisKodu));
    }

    [Theory]
    [InlineData(8)]   // en az bir dosya/klasör kopyalanamadı
    [InlineData(16)]  // ciddi hata, hiçbir şey kopyalanmadı
    public void Robocopy_8_ve_ustu_HATADIR(int cikisKodu)
    {
        Assert.False(WordPressToolkitService.RobocopyBasarili(cikisKodu));
    }

    [Fact]
    public void Robocopy_NEGATIF_cikis_kodu_da_HATADIR()
    {
        // ESKİ `>= 8` DENETİMİNİN KAÇIRDIĞI DURUM: ProcessRunner zaman aşımında süreci öldürüp
        // ExitCode = -1 döner. 30 dakikada bitmeyen bir yedek/aynalama "başarılı" sayılıyordu —
        // tam da işin yarım kaldığı, yani korumanın en çok gerektiği durum.
        Assert.False(WordPressToolkitService.RobocopyBasarili(-1));
    }

    // --- F4: adres değiştirilemediğinde sonuç bunu SÖYLER ---

    [Fact]
    public void Klon_sonucunda_UrlRewritten_alani_VARDIR_ve_varsayilani_dogrudur()
    {
        // Bayrak olmasaydı denetleyici "wp.clone_done" ile "adres değişmedi" durumunu ayıramazdı.
        // Varsayılan true: adres değiştirme GEREKMEYEN durumlar (kaynak ve hedef adres aynı) da
        // dürüstçe "sorun yok" demeli.
        Assert.True(new WordPressCloneResultDto().UrlRewritten);
        Assert.True(new WordPressCopyDataResultDto().UrlRewritten);
    }

    [Fact]
    public void Klon_ucu_adres_degismediginde_FARKLI_mesaj_anahtari_donduruyor()
    {
        // Kaynak denetimi: bayrağın DTO'da durması yetmez, denetleyicinin onu okuyup kullanıcıya
        // farklı bir şey söylemesi gerekir. Aksi hâlde bilgi toplanır ama hiç kimseye ulaşmaz.
        var kaynak = File.ReadAllText(Path.Combine(DepoKoku(), "src", "AdaPanel.Api", "Controllers", "WordPressController.cs"));

        Assert.Contains("wp.clone_url_not_replaced", kaynak);
        Assert.Contains("wp.copy_url_not_replaced", kaynak);
        Assert.Contains("result.UrlRewritten", kaynak);
    }

    [Fact]
    public async Task Arama_degistir_SIFIR_DISI_cikis_kodu_dondururse_BASARISIZ_sayilir()
    {
        // F4'ün ta kendisi. wp-cli komutu çalışır ama başarısız olur (ör. DB'ye bağlanamaz,
        // tablo kilitli): çıkış kodu okunmadığı için panel bunu "tamam" sayıyordu.
        var servis = ServisKur(new SahteHosting(cikisKodu: 1));

        Assert.False(await servis.TryRewriteSiteUrlAsync(
            "hedef-site", "http://kaynak.com", "http://hedef.com", CancellationToken.None));
    }

    [Fact]
    public async Task Arama_degistir_BASARILI_oldugunda_dogru_raporlanir()
    {
        // Olumlu kontrol: düzeltme, çalışan durumu "başarısız" göstermemeli.
        var servis = ServisKur(new SahteHosting(cikisKodu: 0));

        Assert.True(await servis.TryRewriteSiteUrlAsync(
            "hedef-site", "http://kaynak.com", "http://hedef.com", CancellationToken.None));
    }

    [Fact]
    public async Task Arama_degistir_ISTISNA_atarsa_da_BASARISIZ_sayilir()
    {
        // F4'ün ikinci yarısı: boş `catch`. wp-cli hiç çalıştırılamadığında (PHP yok, site WP
        // değil) istisna yutuluyordu ve sonuç "başarılı" görünüyordu.
        var servis = ServisKur(new SahteHosting(istisna: true));

        Assert.False(await servis.TryRewriteSiteUrlAsync(
            "hedef-site", "http://kaynak.com", "http://hedef.com", CancellationToken.None));
    }

    [Theory]
    [InlineData("", "http://hedef.com")]                       // kaynak adres okunamadı
    [InlineData("http://hedef.com", "")]                       // hedef adres okunamadı
    [InlineData("http://ayni.com", "http://ayni.com")]         // adres zaten aynı
    [InlineData("http://AYNI.com", "http://ayni.com")]         // büyük/küçük harf farkı
    public async Task Degistirilecek_bir_sey_yoksa_wp_cli_HIC_calistirilmaz(string kaynakUrl, string hedefUrl)
    {
        // "Değiştirmeye gerek yok" ile "değiştirilemedi" AYNI ŞEY DEĞİLDİR. Gereksiz durumda
        // true dönmeli ama wp-cli'ye hiç gidilmemeli — aksi hâlde her klon boşuna 10 dakikalık
        // bir komut çalıştırır ve hata verirse yanlış uyarı üretir.
        var hosting = new SahteHosting(istisna: true); // çağrılırsa patlar
        var servis = ServisKur(hosting);

        Assert.True(await servis.TryRewriteSiteUrlAsync("hedef-site", kaynakUrl, hedefUrl, CancellationToken.None));
        Assert.Equal(0, hosting.CagriSayisi);
    }

    [Fact]
    public void Arama_degistir_TEK_KAPIDAN_geciyor()
    {
        // F4'ün kök nedeni iki AYRI çağrı yerinde tekrarlanan (ve ikisinde de eksik) hata
        // işlemeydi. Tekrarın tek kaynağa çıkarıldığını korur: aksi hâlde biri düzeltilip diğeri
        // unutulabilir — bu depoda zaten yaşanmış hata sınıfı.
        var kaynak = File.ReadAllText(Path.Combine(
            DepoKoku(), "src", "AdaPanel.Infrastructure", "AppStore", "WordPressToolkitService.cs"));

        // ARANAN DİZGE YORUM DEĞİL KOD OLMALI: ilk yazımda düz "search-replace" arandı ve test
        // dosyanın AÇIKLAMA satırına takıldı.
        Assert.Equal(1, SayimiBul(kaynak, "$\"search-replace "));

        // 1 tanım + 2 çağrı (klon ve veri kopyalama).
        Assert.Equal(3, SayimiBul(kaynak, "TryRewriteSiteUrlAsync("));
    }

    // --- F5: yedek başarısızsa aynalama YAPILMAZ ---

    [Fact]
    public void Yikici_MIR_oncesi_yedek_denetleniyor_ve_hata_YOLU_kesiyor()
    {
        // Sıra kritik: yedek denetimi `/MIR` çağrısından ÖNCE gelmeli. Sonra gelseydi hedef zaten
        // ezilmiş olurdu ve denetimin hiçbir anlamı kalmazdı.
        var kaynak = File.ReadAllText(Path.Combine(
            DepoKoku(), "src", "AdaPanel.Infrastructure", "AppStore", "WordPressToolkitService.cs"));

        var yedekDenetimi = kaynak.IndexOf("wp.copy_backup_failed", StringComparison.Ordinal);

        // Yorumdaki "/MIR" değil, ÇALIŞTIRILAN argüman dizgesi aranıyor (bkz. yukarıdaki gerekçe).
        var aynalama = kaynak.IndexOf("/MIR /R:1", StringComparison.Ordinal);

        Assert.True(yedekDenetimi > 0, "Yedek sonucu denetlenmiyor: 'wp.copy_backup_failed' yolu yok.");
        Assert.True(aynalama > 0, "Aynalama komutu bulunamadi; test yanlis kurulmus olabilir.");
        Assert.True(
            yedekDenetimi < aynalama,
            "Yedek denetimi yikici /MIR adimindan SONRA geliyor; hedef ezildikten sonra denetlemenin degeri yok.");
    }

    // --- düzenek ---

    /// <summary>
    /// Test edilen yol yalnız <c>hostingSettings</c>'i kullanır. Diğer iki bağımlılık bilerek
    /// <c>null</c>: kullanılsalardı <see cref="NullReferenceException"/> ile test HEMEN kırılır —
    /// yani "kullanılmıyor" varsayımı sessizce yanlış kalamaz.
    /// </summary>
    private static WordPressToolkitService ServisKur(ISiteHostingSettingsService hosting)
        => new(null!, null!, hosting);

    /// <summary>wp-cli çağrısını taklit eder: istenen çıkış kodunu döner ya da istisna atar.</summary>
    private sealed class SahteHosting(int cikisKodu = 0, bool istisna = false) : ISiteHostingSettingsService
    {
        public int CagriSayisi { get; private set; }

        public Task<ComposerRunResultDto> RunWpCliCommandAsync(string siteName, string command, CancellationToken cancellationToken = default)
        {
            CagriSayisi++;
            if (istisna)
            {
                throw new AppException("wpcli.not_wordpress", 409, 409);
            }

            return Task.FromResult(new ComposerRunResultDto { ExitCode = cikisKodu, Output = "cikti", Error = "hata" });
        }

        public Task<SiteHostingSettingsDto> GetAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteHostingSettingsDto> UpdateAsync(string siteName, UpdateSiteHostingSettingsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ComposerRunResultDto> RunComposerInstallAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ComposerRunResultDto> RunComposerCommandAsync(string siteName, string command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ComposerRunResultDto> RunArtisanCommandAsync(string siteName, string command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ComposerRunResultDto> RunNodeCommandAsync(string siteName, string command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteImportResultDto> ImportSiteArchiveAsync(string siteName, string archiveUrl, bool cleanFirst, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<QueueWorkerStatusDto> GetQueueWorkerAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<QueueWorkerStatusDto> StartQueueWorkerAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<QueueWorkerStatusDto> StopQueueWorkerAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<QueueWorkerStatusDto> GetLaravelSchedulerAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<QueueWorkerStatusDto> StartLaravelSchedulerAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<QueueWorkerStatusDto> StopLaravelSchedulerAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static int SayimiBul(string metin, string aranan)
    {
        var sayi = 0;
        var konum = 0;
        while ((konum = metin.IndexOf(aranan, konum, StringComparison.Ordinal)) >= 0)
        {
            sayi++;
            konum += aranan.Length;
        }

        return sayi;
    }

    private static string DepoKoku()
    {
        var klasor = Path.GetFullPath(AppContext.BaseDirectory);
        for (var derinlik = 0; derinlik < 8; derinlik++)
        {
            if (File.Exists(Path.Combine(klasor, "AdaPanel.sln")))
            {
                return klasor;
            }

            klasor = Path.GetFullPath(Path.Combine(klasor, ".."));
        }

        throw new DirectoryNotFoundException("Depo koku bulunamadi.");
    }
}
