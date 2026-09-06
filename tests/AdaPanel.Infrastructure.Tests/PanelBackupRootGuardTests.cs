using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// VARSAYILAN YEDEK KÖKÜ BİR YAZMA HEDEFİDİR — ve doğrulanmıyordu.
/// </summary>
/// <remarks>
/// <para>
/// <b>ÖLÇÜLEN AÇIK:</b> <c>PUT /api/v1/panel-settings</c> gövdesinden gelen
/// <c>DefaultBackupRoot</c> hiçbir denetimden geçmiyordu. Panel servisi <c>LocalSystem</c> olarak
/// koşar ve bu kök, o andan sonra alınan HER yedeğin (site, veritabanı, panel, abonelik, posta)
/// yazılacağı yerdir. Arşiv tamperlemenin kalan yolu buydu: yedeğin NEREYE yazıldığını seçebilen,
/// sonradan geri yüklenecek içeriği de seçebilir.
/// </para>
/// <para>
/// <b>Bu dosya davranış ölçer:</b> gerçek <see cref="PanelPreferencesService"/> gerçek dosya
/// deposuyla çalıştırılır; ölçülen şey isteğin KABUL EDİLİP EDİLMEDİĞİ ve diske ne yazıldığıdır.
/// </para>
/// </remarks>
public sealed class PanelBackupRootGuardTests
{
    // =====================================================================================
    // A — KORUMA
    // =====================================================================================

    /// <summary>
    /// Panelin KENDİ veri kökü hedef olamaz: <c>config\server-owner.json</c>, <c>adapanel.db</c>
    /// ve DataProtection anahtarları oradadır.
    /// </summary>
    [Fact]
    public async Task Panel_veri_koku_yedek_hedefi_OLAMAZ()
    {
        var duzenek = new Duzenek();

        var hata = await Assert.ThrowsAsync<AppException>(
            () => duzenek.Servis.UpdateAsync(duzenek.Istek(Path.Combine(duzenek.VeriKoku, "config"))));

        Assert.Equal("settings.backup_root_forbidden", hata.MessageKey);
        Assert.Equal(400, hata.HttpStatusCode);
    }

    [Fact]
    public async Task Isletim_sistemi_klasoru_yedek_hedefi_OLAMAZ()
    {
        var duzenek = new Duzenek();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        var hata = await Assert.ThrowsAsync<AppException>(
            () => duzenek.Servis.UpdateAsync(duzenek.Istek(Path.Combine(windows, "Temp"))));

        Assert.Equal("settings.backup_root_forbidden", hata.MessageKey);
    }

    /// <summary>
    /// Reddedilen hedef DİSKE DE YAZILMAZ. "400 döndü ama ayar kaydedilmişti" en kötü sonuç
    /// olurdu: kullanıcı hata görür, yedekler yine de oraya gider.
    /// </summary>
    [Fact]
    public async Task Reddedilen_hedef_ayar_dosyasina_YAZILMAZ()
    {
        var duzenek = new Duzenek();
        var yasakli = Path.Combine(duzenek.VeriKoku, "config");

        await Assert.ThrowsAsync<AppException>(() => duzenek.Servis.UpdateAsync(duzenek.Istek(yasakli)));

        var kayit = await duzenek.Servis.GetAsync();
        Assert.NotEqual(yasakli, kayit.DefaultBackupRoot);
        // Geri okunan değer panelin varsayılanıdır: kayıt HİÇ değişmedi.
        Assert.Equal(Path.Combine(duzenek.VeriKoku, "backups"), kayit.DefaultBackupRoot);
    }

    // =====================================================================================
    // B — FAZLA KAPATMA NÖBETÇİLERİ
    //
    // Genel kural panelin veri kökünü (haklı olarak) reddeder; oysa VARSAYILAN yedek kökü tam da
    // orada. Sadece "Evaluate" çağıran bir çözüm, hiçbir şeyi değiştirmeden formu kaydeden her
    // yöneticiyi kilitlerdi. Aşağıdakiler o çözümü KIRMIZI yakar.
    // =====================================================================================

    /// <summary>NÖBETÇİ: panelin KENDİ varsayılan yedek klasörü kabul edilmeli.</summary>
    [Fact]
    public async Task Panelin_KENDI_varsayilan_yedek_klasoru_kabul_edilir()
    {
        var duzenek = new Duzenek();
        var varsayilan = Path.Combine(duzenek.VeriKoku, "backups");

        var sonuc = await duzenek.Servis.UpdateAsync(duzenek.Istek(varsayilan));

        Assert.Equal(varsayilan, sonuc.DefaultBackupRoot);
    }

    /// <summary>NÖBETÇİ: varsayılan kökün ALTI da meşrudur (ör. diske göre ayrılmış alt klasör).</summary>
    [Fact]
    public async Task Varsayilan_yedek_klasorunun_ALTI_da_kabul_edilir()
    {
        var duzenek = new Duzenek();
        var alt = Path.Combine(duzenek.VeriKoku, "backups", "gece");

        var sonuc = await duzenek.Servis.UpdateAsync(duzenek.Istek(alt));

        Assert.Equal(alt, sonuc.DefaultBackupRoot);
    }

    /// <summary>
    /// NÖBETÇİ: yedeği AYRI bir klasörde/diskte tutmak en yaygın meşru kurulumdur ve yasaklanmadı.
    /// </summary>
    [Fact]
    public async Task Panel_disindaki_ayri_klasor_kabul_edilir()
    {
        var duzenek = new Duzenek();
        var ayri = Path.Combine(Path.GetTempPath(), $"adapanel-yedek-{Guid.NewGuid():N}");

        var sonuc = await duzenek.Servis.UpdateAsync(duzenek.Istek(ayri));

        Assert.Equal(ayri, sonuc.DefaultBackupRoot);
        Assert.True(Directory.Exists(ayri));
    }

    private sealed class Duzenek
    {
        public Duzenek()
        {
            VeriKoku = Path.Combine(Path.GetTempPath(), $"adapanel-tercih-{Guid.NewGuid():N}");
            Directory.CreateDirectory(VeriKoku);
            SiteKoku = Path.Combine(VeriKoku, "siteler");

            var secenekler = Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = VeriKoku });
            Servis = new PanelPreferencesService(new JsonFileStore(secenekler), secenekler);
        }

        public string VeriKoku { get; }

        /// <summary>Site kökü bu testin konusu değil; panelin dışında ama geçerli bir klasör.</summary>
        private string SiteKoku { get; }

        public PanelPreferencesService Servis { get; }

        public UpdatePanelPreferencesRequest Istek(string yedekKoku) => new()
        {
            PanelName = "AdaPanel",
            DefaultLanguage = "tr",
            ThemeMode = "light",
            UiDensity = "compact",
            DefaultSiteRoot = SiteKoku,
            DefaultBackupRoot = yedekKoku,
            ServerAddress = "panel.ornek.test",
            PreferredHttpsPort = 8890,
            LoginLockoutMinutes = 15,
            DefaultRuntimeProfile = "auto"
        };
    }
}
