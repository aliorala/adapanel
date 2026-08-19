using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Ftp;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Ftp;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// K6 — FTP KULLANICI ADI BİR SAHİPLİK İDDİASIDIR.
/// </summary>
/// <remarks>
/// <para>
/// Denetlenen açık: <c>CreateFtpUserAsync</c> SİTEYİ doğruluyordu ama mutasyona uğrayan nesne
/// GERÇEK BİR WINDOWS YEREL HESABI'ydı ve onun mevcut sahipliği hiç sorulmuyordu. Hesap zaten
/// varsa oluşturma atlanıyor, akış devam ediyor, <c>ftp-users.json</c> kaydındaki
/// <c>SiteName</c> saldırganın sitesiyle EZİLİYOR ve kapsam denetimi tam da o ezilmiş kayıttan
/// okuduğu için GEÇİYORDU. Son halka <c>net user "&lt;ad&gt;" "&lt;parola&gt;" /y</c> —
/// yani bayi, sunucudaki HERHANGİ bir yerel hesabın parolasını değiştirebiliyordu.
/// </para>
/// <para>
/// Bu dosyanın iki katmanı var, çünkü ikisi ayrı şeyi kanıtlar:
/// <list type="number">
/// <item>Hüküm fonksiyonunun TAM doğruluk tablosu — saf, hızlı, tekrarlanabilir.</item>
/// <item>SALDIRI TESTİ: gerçek <see cref="FtpService"/> üzerinden <c>Administrator</c> talebi.
/// Kurgulanmış bir kopya değil, panelin çağırdığı yolun ta kendisi.</item>
/// </list>
/// </para>
/// </remarks>
public sealed class FtpUserNameOwnershipTests : IDisposable
{
    private readonly string veriKoku = Path.Combine(Path.GetTempPath(), $"ada-ftp-sahiplik-{Guid.NewGuid():N}");

    public FtpUserNameOwnershipTests() => Directory.CreateDirectory(veriKoku);

    public void Dispose()
    {
        try { Directory.Delete(veriKoku, recursive: true); } catch { /* en iyi çaba */ }
    }

    // --- 1) HÜKÜM: doğruluk tablosu ---

    [Fact]
    public void Bos_ad_ve_Windows_hesabi_yoksa_ad_SERBEST()
    {
        // Olağan durum: yeni müşteri, yeni FTP hesabı. Düzeltme bu yolu KIRMAMALI.
        Assert.Equal(
            FtpService.FtpUserNameClaim.Serbest,
            FtpService.ClassifyUserNameClaim("site_ftp", "musteri.com", [], sistemdeVar: false));
    }

    [Fact]
    public void Kendi_sitesinin_hesabi_yeniden_olusturulabilir()
    {
        // Panelde bugün çalışan meşru akış: aynı adla yeniden oluşturarak parola/ana dizin sıfırlama.
        // Geriye dönük uyumun asıl taşıyıcısı bu satır — mevcut kurulumlardaki her hesabın kaydı var.
        Assert.Equal(
            FtpService.FtpUserNameClaim.KendiKaydi,
            FtpService.ClassifyUserNameClaim(
                "site_ftp",
                "musteri.com",
                [("site_ftp", "musteri.com")],
                sistemdeVar: true));
    }

    [Fact]
    public void Kayit_varken_Windows_hesabi_silinmisse_yine_KENDI_kaydidir()
    {
        // Hesap dışarıdan silinmiş olabilir. Kaydın sahibi onu yeniden açabilmeli; hüküm
        // Windows'un o anki durumuna değil, panelin kaydına dayanır.
        Assert.Equal(
            FtpService.FtpUserNameClaim.KendiKaydi,
            FtpService.ClassifyUserNameClaim(
                "site_ftp",
                "musteri.com",
                [("site_ftp", "musteri.com")],
                sistemdeVar: false));
    }

    [Fact]
    public void Baska_sitenin_hesabi_DEVRALINAMAZ()
    {
        // K6 zincirinin 4. adımı: kaydı ezip SiteName'i kendi sitesiyle değiştirme.
        Assert.Equal(
            FtpService.FtpUserNameClaim.BaskaSitenin,
            FtpService.ClassifyUserNameClaim(
                "kurban_ftp",
                "saldirgan.com",
                [("kurban_ftp", "kurban.com")],
                sistemdeVar: true));
    }

    [Fact]
    public void Kayit_yoksa_ama_Windows_hesabi_VARSA_ad_PANEL_DISIDIR()
    {
        // Administrator, servis hesapları, elle açılmış hesaplar. Panelin açmadığı hiçbir hesabın
        // adı talep edilemez.
        Assert.Equal(
            FtpService.FtpUserNameClaim.PanelDisi,
            FtpService.ClassifyUserNameClaim("Administrator", "saldirgan.com", [], sistemdeVar: true));
    }

    [Theory]
    [InlineData("ADMINISTRATOR")]
    [InlineData("administrator")]
    [InlineData("AdMiNiStRaToR")]
    [InlineData("  Administrator  ")]
    public void Buyuk_kucuk_harf_ve_bosluk_denetimi_ATLATAMAZ(string varyasyon)
    {
        // Windows yerel hesap adları büyük/küçük harfe DUYARSIZDIR: "administrator" ile
        // "Administrator" AYNI hesaptır. Duyarlı bir karşılaştırma denetimi tek harf değiştirerek
        // atlatılabilir bir süse çevirirdi.
        Assert.Equal(
            FtpService.FtpUserNameClaim.BaskaSitenin,
            FtpService.ClassifyUserNameClaim(
                varyasyon,
                "saldirgan.com",
                [("Administrator", "kurban.com")],
                sistemdeVar: true));
    }

    [Fact]
    public void Kayit_tarafindaki_bosluk_ve_harf_farki_da_ATLATAMAZ()
    {
        // Kayıt dosyası elle düzenlenmiş ya da eski bir sürüm tarafından yazılmış olabilir;
        // normalleştirme İKİ tarafta da uygulanmalı.
        Assert.Equal(
            FtpService.FtpUserNameClaim.BaskaSitenin,
            FtpService.ClassifyUserNameClaim(
                "kurban_ftp",
                "saldirgan.com",
                [("  KURBAN_FTP  ", "  kurban.com  ")],
                sistemdeVar: false));
    }

    [Fact]
    public void Ayni_site_adinin_yazim_farki_KENDI_kaydi_sayilir()
    {
        // Aynı sitenin adı büyük harfle geldiğinde meşru sahibin kendi hesabı reddedilmemeli —
        // düzeltirken çalışan akışı bozmanın tipik yolu budur.
        Assert.Equal(
            FtpService.FtpUserNameClaim.KendiKaydi,
            FtpService.ClassifyUserNameClaim(
                "site_ftp",
                "MUSTERI.COM",
                [("site_ftp", "musteri.com")],
                sistemdeVar: true));
    }

    [Fact]
    public void Kalabalik_kayit_listesinde_DOGRU_kayit_bulunur()
    {
        // Eşleşmeyen kayıtlar hükmü etkilememeli (ilk kayda bakıp dönen bir uygulama burada düşer).
        Assert.Equal(
            FtpService.FtpUserNameClaim.BaskaSitenin,
            FtpService.ClassifyUserNameClaim(
                "kurban_ftp",
                "saldirgan.com",
                [
                    ("bir_ftp", "bir.com"),
                    ("iki_ftp", "iki.com"),
                    ("kurban_ftp", "kurban.com"),
                    ("uc_ftp", "uc.com")
                ],
                sistemdeVar: true));
    }

    // --- 2) SALDIRI TESTİ: gerçek servis, gerçek Windows hesabı ---

    [Fact]
    public async Task SALDIRI_bayi_Administrator_adini_TALEP_EDEMEZ()
    {
        // Zincirin 1. adımı. Buradan geçilemezse 2-6. adımların hiçbiri kurulamaz: ACL verilmez,
        // junction yönlendirilmez, kayıt ezilmez, kapsam denetimi kandırılamaz ve parola
        // değiştirilemez. Panelin GERÇEK çağrı yolu koşuluyor.
        var servis = ServisKur();

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.CreateFtpUserAsync(new CreateFtpUserRequest
        {
            UserName = "Administrator",
            Password = "SaldirganParola123!",
            SiteName = "saldirgan.com"
        }));

        Assert.Equal("ftp.user_name_reserved", hata.MessageKey);
        Assert.Equal(409, hata.HttpStatusCode);
    }

    [Fact]
    public async Task SALDIRI_reddedilen_istek_sunucuda_HICBIR_iz_birakmaz()
    {
        // Denetimin KONUMU da davranışın parçası: reddedilen bir istek klasör oluşturmamalı,
        // IIS FTP altyapısını kurmamalı, ACL uygulamamalı ve kayıt yazmamalı. Denetim eskiden
        // bunların ARDINDAN gelseydi saldırgan reddedilse bile sunucuyu değiştirebilirdi.
        var servis = ServisKur();

        await Assert.ThrowsAsync<AppException>(() => servis.CreateFtpUserAsync(new CreateFtpUserRequest
        {
            UserName = "Administrator",
            Password = "SaldirganParola123!",
            SiteName = "saldirgan.com"
        }));

        // ftp-root hiç oluşturulmadı (EnsureFtpInfrastructureAsync çalışsaydı olurdu)...
        Assert.False(Directory.Exists(Path.Combine(veriKoku, "ftp-root")));
        // ...ve panel kaydına hiçbir şey yazılmadı.
        Assert.False(File.Exists(Path.Combine(veriKoku, "config", "ftp-users.json")));
        // IIS'e de hiç sorulmadı: sahte site servisi çağrılsaydı NotSupportedException fırlatırdı
        // ve test AppException yerine onu görürdü.
    }

    [Fact]
    public async Task SALDIRI_bayi_baska_sitenin_FTP_hesabini_DEVRALAMAZ()
    {
        // Kapsam denetimi (FtpController.EnsureFtpUserScopeAsync) sahipliği ftp-users.json'dan
        // okur. Saldırganın o kaydı EZEBİLMESİ açığın kalbiydi; kayıt artık ezilemiyor.
        var servis = ServisKur();
        await KayitYaz(("kurban_ftp", "kurban.com", "C:\\sites\\kurban.com"));

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.CreateFtpUserAsync(new CreateFtpUserRequest
        {
            UserName = "kurban_ftp",
            Password = "SaldirganParola123!",
            SiteName = "saldirgan.com"
        }));

        Assert.Equal("ftp.user_name_taken", hata.MessageKey);
        Assert.Equal(409, hata.HttpStatusCode);

        // KAYIT DEĞİŞMEDİ: kurbanın sahipliği duruyor, dolayısıyla kapsam denetimi hâlâ doğru
        // kaynaktan okuyor.
        var icerik = await File.ReadAllTextAsync(Path.Combine(veriKoku, "config", "ftp-users.json"));
        Assert.Contains("kurban.com", icerik);
        Assert.DoesNotContain("saldirgan.com", icerik);
    }

    [Fact]
    public async Task SALDIRI_SITE_OLUSTURMA_yolundan_gelen_FTP_adi_da_denetleniyor()
    {
        // İKİNCİ GİRİŞ KAPISI. Açığın müşteri varyantı FTP ucundan değil SİTE OLUŞTURMA ucundan
        // geliyordu: `POST /sites` gövdesindeki `FtpUserName` istekten alınıp doğrudan buraya
        // geçiriliyor ve o uç Customer'a açık. Müşteri parola değiştiremez ama kurbanın FTP
        // kaydını/junction'ını ele geçirip erişimini kesebiliyordu.
        //
        // Denetim SERVİSTE durduğu için o kapı da kendiliğinden kapandı: çağıranın hangi
        // denetleyici olduğu fark etmez. Denetimi denetleyiciye koysaydık her yeni çağıran için
        // yeniden yazmak gerekirdi ve biri mutlaka unutulurdu.
        var servis = ServisKur();
        await KayitYaz(("kurban_ftp", "kurban.com", "C:\\sites\\kurban.com"));

        // SitesController'ın kurduğu çağrının birebir aynısı: HomePath boş, SiteName yeni site.
        var hata = await Assert.ThrowsAsync<AppException>(() => servis.CreateFtpUserAsync(new CreateFtpUserRequest
        {
            UserName = "kurban_ftp",
            Password = "SaldirganParola123!",
            SiteName = "saldirgan-yeni-site",
            HomePath = string.Empty
        }));

        Assert.Equal("ftp.user_name_taken", hata.MessageKey);
    }

    /// <remarks>
    /// <b>UYARI — BU TESTİN KORUMASINI MUTASYONLA KALDIRMAYIN.</b> Test geçerken tamamen güvenlidir:
    /// kayıt denetimi <c>net.exe</c>'ye ULAŞMADAN fırlatır. Ama koruma kaldırılırsa akış gerçekten
    /// <c>net user "Administrator" "&lt;parola&gt;" /y</c> çalıştırır ve test koşan makinenin yerleşik
    /// Administrator PAROLASINI DEĞİŞTİRİR.
    /// <para>
    /// Bu satır tahmin değil, ÖLÇÜM: 19.08.2026 tarihinde bu denetim mutasyonla kaldırılıp test
    /// koşuldu ve geliştirme makinesinin Administrator parolası gerçekten değişti ("Parolanın son
    /// ayarlandığı" damgası koşu anını gösterdi). K6'nın kuramsal değil işleyen bir zincir olduğunun
    /// doğrudan kanıtı — ve bu korumanın neden burada durduğunun.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task SALDIRI_panelin_yonetmedigi_hesabin_PAROLASI_degistirilemez()
    {
        // Zincirin son halkası, ikinci kapı. Kayıt başka bir yoldan kirletilse bile panel yalnız
        // KENDİ açtığı hesapların parolasını değiştirir. Yanıt "bulunamadı" — hangi yerel
        // hesapların var olduğunu sızdırmamak için, kayıt yokluğu ile hesap yokluğu aynı görünür.
        var servis = ServisKur();

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.SetFtpPasswordAsync(new SetFtpPasswordRequest
        {
            UserName = "Administrator",
            Password = "SaldirganParola123!"
        }));

        Assert.Equal("ftp.user_not_found", hata.MessageKey);
        Assert.Equal(404, hata.HttpStatusCode);
    }

    [Fact]
    public async Task Gecersiz_ad_deseni_hala_ONCE_reddedilir()
    {
        // Ad doğrulaması sahiplik denetiminden ÖNCE çalışmalı: geçersiz bir ad için sunucuya
        // "bu hesap var mı" diye sormanın anlamı yok.
        var servis = ServisKur();

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.CreateFtpUserAsync(new CreateFtpUserRequest
        {
            UserName = "1-gecersiz ad",
            Password = "Parola123!",
            SiteName = "saldirgan.com"
        }));

        Assert.Equal("ftp.invalid_user_name", hata.MessageKey);
    }

    // --- düzenek ---

    private FtpService ServisKur() => new(
        new PatlayanSiteServisi(),
        new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = veriKoku })),
        new SahteTercihler(),
        Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = veriKoku }));

    private async Task KayitYaz(params (string UserName, string SiteName, string HomePath)[] kayitlar)
    {
        var klasor = Path.Combine(veriKoku, "config");
        Directory.CreateDirectory(klasor);
        var satirlar = kayitlar.Select(k =>
            $$"""{"userName":"{{k.UserName}}","siteName":"{{k.SiteName}}","homePath":"{{k.HomePath.Replace("\\", "\\\\")}}","isEnabled":true,"createdAtUtc":"2026-01-01T00:00:00Z"}""");
        await File.WriteAllTextAsync(Path.Combine(klasor, "ftp-users.json"), "[" + string.Join(",", satirlar) + "]");
    }

    /// <summary>
    /// IIS'e HİÇ SORULMAMALI: sahiplik denetimi site çözümlemesinden önce gelir. Çağrılırsa test
    /// beklediği istisnayı değil bunu görür ve kırılır — sessiz bir gerileme olamaz.
    /// </summary>
    private sealed class PatlayanSiteServisi : IIisSiteService
    {
        public Task<IReadOnlyCollection<SiteDto>> GetSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> CreateSiteAsync(CreateSiteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> UpdatePhysicalPathAsync(string siteName, string physicalPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> StartSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> StopSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> AddBindingAsync(string siteName, SiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> RemoveBindingAsync(string siteName, RemoveSiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<SiteDto>> ImportExistingSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class SahteTercihler : IPanelPreferencesService
    {
        public Task<PanelPreferencesDto> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new PanelPreferencesDto());

        public Task<PanelPreferencesDto> UpdateAsync(UpdatePanelPreferencesRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
