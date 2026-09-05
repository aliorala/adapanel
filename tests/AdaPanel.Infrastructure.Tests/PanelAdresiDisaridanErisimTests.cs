using AdaPanel.Core.Dtos.Advisor;
using AdaPanel.Core.Dtos.Antivirus;
using AdaPanel.Core.Dtos.Automation;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Notifications;
using AdaPanel.Core.Dtos.Security;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Dtos.System;
using AdaPanel.Core.Dtos.Update;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Advisor;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Panel, KENDİ adresinin dışarıdan açılamayacağını fark ediyor mu?
/// </summary>
/// <remarks>
/// <para>
/// <b>Ölçülen arıza:</b> panel adresi ayarlanmadığında <c>PanelPreferencesService</c> alanı boş
/// bırakmıyor — makinenin ilk loopback-olmayan IPv4 adresini yazıyor. NAT arkasındaki bir
/// sunucuda bu <c>192.168.1.5</c> gibi bir İÇ adrestir (bu depoda ölçüldü: aday adreslerin
/// TAMAMI özel aralıkta ve ilk sırada bir sanal ağ adaptörü var — yani otomatik çözülen değer
/// yalnızca "iç" değil, çoğu zaman YANLIŞ iç adres). Müşteriye e-posta
/// ile giden parola sıfırlama, hoş geldin ve abonelikten çıkma bağlantıları bu adresi taşır;
/// müşteri açamaz, panel ise "gönderildi" der ve hiçbir şey fark etmez.
/// </para>
/// <para>
/// <b>Seçilen davranış: ENGELLEME YOK, UYARI VAR.</b> İç ağa kurulmuş meşru bir panelde
/// (şirket içi hosting, geliştirme kurulumu) <c>192.168.x.x</c> DOĞRU adrestir; gönderimi
/// kesmek çalışan kurulumu kırardı — kapatılan sorundan kötü. Bu yüzden ölçüm bir bayrağa
/// (<c>PanelPreferencesDto.ServerAddressExternallyReachable</c>) yazılır, bayrak Ayarlar &gt;
/// Genel formunda ve Danışman ekranında yöneticiye gösterilir, posta yolu HİÇ değişmez.
/// </para>
/// <para>
/// <b>Bu dosyanın fazla kapatma nöbetçileri:</b> yapılandırılmış GERÇEK bir alan adı hiçbir
/// uyarı üretmemeli, ve iç adresli bir kurulumda posta YİNE gönderilmeli.
/// </para>
/// </remarks>
public sealed class PanelAdresiDisaridanErisimTests
{
    // ---------------------------------------------------------------------------------
    // 1) SINIFLANDIRMA — hangi adres dışarıdan açılabilir?
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// İnternetten ÇÖZÜLMEYEN adresler: özel aralıklar, loopback, bağlantı-yerel, taşıyıcı NAT,
    /// tek etiketli makine adı ve genel DNS'e delege edilmeyen son ekler.
    /// </summary>
    [Theory]
    [InlineData("192.168.1.5")]      // ev/ofis ağı — otomatik çözülen tipik değer
    [InlineData("192.168.218.1")]    // sanal ağ adaptörü adresi: otomatik çözümün seçtiği değer
                                     // gerçek LAN adresi bile OLMAYABİLİR (ölçüldü)
    [InlineData("10.0.0.5")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("169.254.94.233")]   // bağlantı-yerel (DHCP yokken)
    [InlineData("127.0.0.1")]
    [InlineData("100.64.0.1")]       // taşıyıcı sınıfı NAT (RFC 6598)
    [InlineData("0.0.0.0")]
    [InlineData("255.255.255.255")]
    [InlineData("WIN-SUNUCU01")]     // makine adı: IPv4 hiç bulunamadığında yazılan değer
    [InlineData("MASAUSTU-A1B2C3")]
    [InlineData("localhost")]
    [InlineData("panel.local")]      // mDNS
    [InlineData("panel.internal")]
    [InlineData("panel.home.arpa")]
    [InlineData("::1")]
    [InlineData("[fe80::1]")]
    [InlineData("fd00::1")]          // benzersiz yerel IPv6
    [InlineData("::ffff:192.168.1.5")]
    [InlineData("")]
    [InlineData("   ")]
    public void Icerideki_adresler_DISARIDAN_ACILAMAZ_olarak_isaretlenir(string adres)
    {
        Assert.False(PanelAddress.IsExternallyReachable(adres), $"Bu adres dışarıdan açılamaz sayılmalıydı: {adres}");
    }

    /// <summary>
    /// <b>FAZLA KAPATMA NÖBETÇİSİ.</b> Gerçek alan adları ve GENEL IP'ler uyarı üretmemeli.
    /// </summary>
    /// <remarks>
    /// Bu liste, uyarının "her adrese kızan" bir gürültüye dönüşmesini engeller. Özellikle
    /// <c>172.32.x</c> ve <c>192.169.x</c> sınır komşularıdır: özel aralık kontrolü
    /// <c>172.</c>/<c>192.</c> ilk baytına bakarak yazılsaydı bu iki GERÇEK genel adres de
    /// yanlışlıkla "iç" sayılırdı.
    /// </remarks>
    [Theory]
    [InlineData("panel.ornek.com")]
    [InlineData("panel.ornek.com.")]   // sondaki nokta (kök) — aynı ad
    [InlineData("PANEL.ORNEK.COM")]
    [InlineData("185.7.242.225")]      // canlı sunucunun gerçek IP'si
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]         // 172.16/12 ARALIĞININ DIŞI — genel
    [InlineData("172.15.255.255")]     // aralığın alt sınırının dışı — genel
    [InlineData("192.169.0.1")]        // 192.168/16 DIŞI — genel
    [InlineData("100.63.255.255")]     // 100.64/10 DIŞI — genel
    [InlineData("100.128.0.1")]        // 100.64/10 DIŞI — genel
    [InlineData("169.253.0.1")]        // 169.254/16 DIŞI — genel
    [InlineData("2001:db8::1")]
    [InlineData("[2606:4700:4700::1111]")]
    public void Gercek_alan_adlari_ve_genel_IPler_UYARI_URETMEZ(string adres)
    {
        Assert.True(PanelAddress.IsExternallyReachable(adres), $"Bu adres dışarıdan açılabilir sayılmalıydı: {adres}");
    }

    // ---------------------------------------------------------------------------------
    // 2) AYAR KAYNAĞI — bayrak gerçekten tercihlerde üretiliyor mu?
    // ---------------------------------------------------------------------------------

    /// <summary>Kayıtta iç IP yazıyorsa okuma katmanı bunu bayrakla bildirir.</summary>
    [Fact]
    public async Task Ic_IP_ayarliyken_tercihler_UYARI_bayragini_kaldirir()
    {
        using var duzenek = new GeciciKok();
        await duzenek.AdresiYazAsync("192.168.1.5");

        var tercihler = await duzenek.Tercihler.GetAsync(CancellationToken.None);

        Assert.Equal("192.168.1.5", tercihler.ServerAddress);
        Assert.False(tercihler.ServerAddressExternallyReachable);
    }

    /// <summary>
    /// <b>FAZLA KAPATMA NÖBETÇİSİ:</b> yapılandırılmış gerçek alan adı hiçbir uyarı üretmez.
    /// </summary>
    [Fact]
    public async Task Yapilandirilmis_GERCEK_alan_adi_UYARI_URETMEZ()
    {
        using var duzenek = new GeciciKok();
        await duzenek.AdresiYazAsync("panel.ornek.com");

        var tercihler = await duzenek.Tercihler.GetAsync(CancellationToken.None);

        Assert.Equal("panel.ornek.com", tercihler.ServerAddress);
        Assert.True(tercihler.ServerAddressExternallyReachable);
    }

    /// <summary>
    /// Kayıtta ESKİ/YANLIŞ bir bayrak dursa bile okuma katmanı onu yeniden hesaplar.
    /// </summary>
    /// <remarks>
    /// Bayrak türetilmiş bir alandır ama <c>UpdateAsync</c> tercih dosyasını DTO'nun tamamıyla
    /// yazdığı için dosyada da görünür. Dosyadaki değere güvenilseydi, adres elle iç bir IP'ye
    /// çevrildiğinde eski "sorun yok" değeri kalır ve uyarı hiç çıkmazdı.
    /// </remarks>
    [Fact]
    public async Task Kayittaki_ESKI_bayrak_okuma_katmaninda_yeniden_hesaplanir()
    {
        using var duzenek = new GeciciKok();
        await duzenek.YazAsync(
            """{"serverAddress":"192.168.1.5","serverAddressExternallyReachable":true,"preferredHttpsPort":8890}""");

        var tercihler = await duzenek.Tercihler.GetAsync(CancellationToken.None);

        Assert.False(tercihler.ServerAddressExternallyReachable);
    }

    /// <summary>Yönetici adresi düzeltince uyarı AYNI YANITTA kalkar (yeniden yükleme beklenmez).</summary>
    [Fact]
    public async Task Adres_duzeltilince_uyari_KAYDETME_YANITINDA_kalkar()
    {
        using var duzenek = new GeciciKok();
        await duzenek.AdresiYazAsync("192.168.1.5");

        var sonuc = await duzenek.Tercihler.UpdateAsync(duzenek.Istek("panel.ornek.com"), CancellationToken.None);

        Assert.True(sonuc.ServerAddressExternallyReachable);
    }

    /// <summary>Yönetici adresi iç bir IP'ye çevirirse kaydetme yanıtı uyarıyı GÖSTERİR.</summary>
    [Fact]
    public async Task Adres_ic_IPye_cevrilirse_kaydetme_yaniti_UYARIR()
    {
        using var duzenek = new GeciciKok();
        await duzenek.AdresiYazAsync("panel.ornek.com");

        var sonuc = await duzenek.Tercihler.UpdateAsync(duzenek.Istek("10.0.0.5"), CancellationToken.None);

        Assert.Equal("10.0.0.5", sonuc.ServerAddress);
        Assert.False(sonuc.ServerAddressExternallyReachable);
    }

    // ---------------------------------------------------------------------------------
    // 3) DANIŞMAN EKRANI — uyarı yöneticinin GÖRDÜĞÜ yere ulaşıyor mu?
    // ---------------------------------------------------------------------------------

    /// <summary>İç adresli kurulumda Danışman raporunda uyarı maddesi çıkar.</summary>
    [Fact]
    public async Task Danisman_ic_adresli_kurulumda_UYARI_MADDESI_uretir()
    {
        using var duzenek = new GeciciKok();
        await duzenek.AdresiYazAsync("192.168.1.5");

        var rapor = await duzenek.Danisman().GetReportAsync(CancellationToken.None);

        var madde = rapor.Items.Single(item => item.Id == "panel-address-not-public");
        Assert.Equal("warning", madde.Severity);
        // Yönetici hangi adresin sorunlu olduğunu görebilmeli; yoksa "git bul" demiş oluruz.
        Assert.Equal("192.168.1.5", madde.Detail);
        // Uyarı yalnız bir cümle değil, GİDİLECEK YER de göstermeli.
        Assert.Equal("settings", madde.ActionSection);
    }

    /// <summary>
    /// <b>FAZLA KAPATMA NÖBETÇİSİ:</b> gerçek alan adıyla kurulmuş panelde Danışman bu maddeyi
    /// HİÇ üretmez — ve kontrol "çalıştırılamadı" listesine de düşmez (sessiz atlama sayılmaz).
    /// </summary>
    [Fact]
    public async Task Danisman_GERCEK_alan_adinda_bu_uyariyi_URETMEZ()
    {
        using var duzenek = new GeciciKok();
        await duzenek.AdresiYazAsync("panel.ornek.com");

        var rapor = await duzenek.Danisman().GetReportAsync(CancellationToken.None);

        Assert.DoesNotContain(rapor.Items, item => item.Id == "panel-address-not-public");
        Assert.DoesNotContain("panelAddress", rapor.FailedChecks);
    }

    // ---------------------------------------------------------------------------------
    // 4) FAZLA KAPATMA — uyarı hiçbir bağlantıyı ÖLDÜRMEZ
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// İç adresli kurulumda bağlantı ÜRETİLMEYE devam eder (yer tutucuya düşmez, boş kalmaz).
    /// </summary>
    /// <remarks>
    /// Şirket içi bir kurulumda <c>https://192.168.1.5:8890/...</c> ÇALIŞAN bir adrestir. Uyarı
    /// bunu bozmamalı; "uyarı varsa bağlantı üretme" bir gün eklenirse burası kırmızıya düşer.
    /// </remarks>
    [Fact]
    public void Uyari_verilen_adres_icin_baglanti_YINE_uretilir()
    {
        Assert.Equal("https://192.168.1.5:8890", PanelAddress.BuildBaseUrl("192.168.1.5", 8890));
        Assert.Equal(
            "https://192.168.1.5:8890/gizli-giris",
            PanelAddress.BuildEntryUrl("192.168.1.5", 8890, "gizli-giris"));
        Assert.DoesNotContain(
            PanelAddress.UnknownHostPlaceholder,
            PanelAddress.BuildBaseUrl("192.168.1.5", 8890),
            StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------
    // Düzenek
    // ---------------------------------------------------------------------------------

    /// <remarks>
    /// Klasör KASITEN silinmez: bu oturumda hiçbir silme işlemi yapılmıyor. Klasörler
    /// <c>%TEMP%\adapanel-adres-erisim-*</c> altında benzersiz adlarla kalır ve elle temizlenebilir.
    /// </remarks>
    private sealed class GeciciKok : IDisposable
    {
        private readonly Microsoft.Extensions.Options.IOptions<PanelRuntimeOptions> _secenekler;

        public GeciciKok()
        {
            Kok = Path.Combine(Path.GetTempPath(), "adapanel-adres-erisim-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Kok);
            _secenekler = Microsoft.Extensions.Options.Options.Create(
                new PanelRuntimeOptions { DataRoot = Kok, PreferredHttpsPort = 8890 });
            Depo = new JsonFileStore(_secenekler);
            Tercihler = new PanelPreferencesService(Depo, _secenekler);
        }

        public string Kok { get; }

        public JsonFileStore Depo { get; }

        public PanelPreferencesService Tercihler { get; }

        public Task AdresiYazAsync(string adres)
            => YazAsync($$"""{"serverAddress":"{{adres}}","preferredHttpsPort":8890}""");

        public async Task YazAsync(string json)
        {
            var klasor = Path.Combine(Kok, "config");
            Directory.CreateDirectory(klasor);
            await File.WriteAllTextAsync(Path.Combine(klasor, "panel-preferences.json"), json);
        }

        /// <summary>Ayarlar formunun gönderdiği isteğin aynısı; yalnız adres değişir.</summary>
        public UpdatePanelPreferencesRequest Istek(string adres) => new()
        {
            PanelName = "AdaPanel",
            DefaultLanguage = "tr",
            ThemeMode = "light",
            UiDensity = "compact",
            DefaultSiteRoot = Path.Combine(Kok, "sites"),
            DefaultBackupRoot = Path.Combine(Kok, "backups"),
            ServerAddress = adres,
            PreferredHttpsPort = 8890,
            AllowHttpFallback = false,
            LoginLockoutMinutes = 15,
            DefaultRuntimeProfile = "auto",
            InstallRuntimeIfMissing = true
        };

        /// <summary>
        /// GERÇEK <see cref="AdvisorService"/>; panel tercihleri de GERÇEK servisten okunur.
        /// </summary>
        /// <remarks>
        /// Diğer bağımlılıklar <c>NotSupportedException</c> atar: bu testin sorusu "panel adresi
        /// kontrolü rapora giriyor mu", diğer kontrollerin sonucu değil. Atan bağımlılıklar
        /// raporun <c>FailedChecks</c> listesine düşer — sessizce "sorun yok" görünmezler, ki
        /// bu davranışın kendisi de burada dolaylı olarak ölçülmüş olur.
        /// </remarks>
        public AdvisorService Danisman() => new(
            new PatlayanServis(),
            new PatlayanServis(),
            new PatlayanServis(),
            new PatlayanServis(),
            new PatlayanServis(),
            new PatlayanServis(),
            Tercihler,
            Depo,
            NullLogger<AdvisorService>.Instance);

        public void Dispose()
        {
            // Bilerek boş: geçici klasör silinmez (yukarıdaki nota bakınız).
        }
    }

    /// <summary>
    /// Danışmanın DİĞER kontrollerini besleyen bağımlılıklar. Hepsi patlar: sahte "sorun yok"
    /// verisi üretmek, ölçülmeyen bir yolu yeşil göstermek olurdu.
    /// </summary>
    private sealed class PatlayanServis :
        ISelfUpdateService,
        ISystemMonitoringService,
        IAutomationService,
        IPanelSecurityService,
        INotificationService,
        IAntivirusService
    {
        public Task<UpdateStatusDto> CheckForUpdateAsync(bool forceRefresh = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UpdateStatusDto> SetGithubRepoAsync(string repo, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UpdateStatusDto> ApplyUpdateAsync(ApplyUpdateRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UpdateStatusDto> InstallPreparedUpdateAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SystemOverviewDto> GetOverviewAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<ServerIpAddressDto>> GetServerIpAddressesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SystemHistorySampleDto>> GetHistoryAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RecordSampleAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AutomationStatusDto> GetStatusAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AutomationStatusDto> UpdateSettingsAsync(UpdateAutomationSettingsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AutomationStatusDto> RunBackupNowAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AutomationStatusDto> RunSslRenewNowAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RunDueJobsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupRemoteTestResultDto> TestRemoteBackupAsync(BackupRemoteTestRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SecuritySettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SecuritySettingsDto> UpdateSettingsAsync(UpdateSecuritySettingsRequest request, Guid actorUserId, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> GetSecureEntryPathAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> IsIpAllowedAsync(string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        Task<NotificationSettingsDto> INotificationService.GetSettingsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        Task<NotificationSettingsDto> INotificationService.UpdateSettingsAsync(UpdateNotificationSettingsRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SendEmailAsync(string subject, string body, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SendEmailToAsync(string toEmail, string subject, string body, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> IsEmailConfiguredAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SendWebhookAsync(string eventName, string body, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task TestAsync(TestNotificationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        Task<AntivirusStatusDto> IAntivirusService.GetStatusAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AntivirusStatusDto> UpdateDefinitionsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AntivirusScanResultDto> ScanAsync(AntivirusScanRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
