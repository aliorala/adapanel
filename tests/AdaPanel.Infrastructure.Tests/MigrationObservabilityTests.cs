using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Migration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// <c>FileMigrationService</c> gözlemlenebilirliği + SIR SIZINTISI koruması.
/// </summary>
/// <remarks>
/// <para>
/// Göç tek seferliktir ve geri alınamaz: kullanıcı bittiğini sanıp DNS'i çevirdikten sonra eksik
/// gelen dosyalar geri getirilemez. Bu yüzden burada iki şey birlikte ölçülüyor: (1) başarısızlık
/// ve KISMİ başarı sunucuda iz bırakıyor mu, (2) o iz FTP KİMLİK BİLGİSİNİ sızdırıyor mu.
/// </para>
/// <para>
/// İkincisi gerçek bir risk: bu servisin isteği kullanıcı adı ve parola taşır ve günlük dosyası
/// yanıtın aksine uzun ömürlüdür (yedeklere de girer). Denetim kaydındaki merkezî temizleyici
/// <c>AuditSecretRedactor</c> ILogger yolunda ÇALIŞMAZ; koruma çağrı yerinde kurulmak zorundadır.
/// </para>
/// </remarks>
public sealed class MigrationObservabilityTests
{
    // --- Günlüğe yazılan adres: kimlik bilgisi İÇERMEZ ---

    [Fact]
    public void Gunluk_adresi_sema_konak_port_ve_yolu_tasir()
    {
        var etiket = FileMigrationService.GunlugeUygunHedef("sftp", "ornek.test", 2222, "/httpdocs");

        Assert.Equal("sftp://ornek.test:2222/httpdocs", etiket);
    }

    /// <summary>
    /// Kullanıcı konak alanına "kullanici:parola@sunucu" yazabilir — FTP adreslerinde YAYGIN bir
    /// biçim. Olduğu gibi yazılsaydı parola günlüğe düşerdi.
    /// <para>MUTASYON: <c>@</c> ayıklaması kaldırılırsa bu test düşer.</para>
    /// </summary>
    [Fact]
    public void Konak_alanina_gomulu_kimlik_bilgisi_adresten_DUSURULUR()
    {
        var etiket = FileMigrationService.GunlugeUygunHedef("ftp", "kullanici:CokGizliParola@ornek.test", 21, "/");

        Assert.Equal("ftp://ornek.test:21/", etiket);
        Assert.DoesNotContain("CokGizliParola", etiket, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("kullanici", etiket, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Port_verilmemisse_adreste_port_parcasi_olmaz()
    {
        Assert.Equal("ftp://ornek.test/", FileMigrationService.GunlugeUygunHedef("ftp", "ornek.test", 0, "/"));
    }

    // --- Gerçek akış: bağlantı reddedilince ne yazılıyor, ne YAZILMIYOR ---

    /// <summary>
    /// Bağlanılamayan göç: istisna HTTP yanıtına gider ve orada biter; yığın izi (asıl teşhis
    /// malzemesi) yalnız günlükte kalabilir.
    /// <para>
    /// AYNI TESTTE SIR DENETİMİ: kullanıcı adı ve parola günlüğün HİÇBİR satırında geçmemeli.
    /// MUTASYON: başlangıç/hata satırlarında <c>hedefEtiket</c> yerine <c>request</c> yazılırsa
    /// (ya da kullanıcı adı eklenirse) bu test düşer.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Baglanti_kurulamayinca_HATA_yazilir_ama_kimlik_bilgisi_SIZMAZ()
    {
        var hedefKok = Path.Combine(Path.GetTempPath(), "ada-goc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(hedefKok);

        try
        {
            var gunlukcu = new KayitTutanGunlukcu<FileMigrationService>();
            var servis = new FileMigrationService(new SahteGocSiteServisi(hedefKok), gunlukcu);

            // 127.0.0.1:1 — loopback'te KESİN reddedilen port; ağ beklemesi olmaz.
            var istek = new RemoteFileMigrationRequest
            {
                SiteName = "ornek.test",
                Protocol = "ftp",
                Host = "127.0.0.1",
                Port = 1,
                UserName = "gocKullanicisi",
                Password = "CokGizliParola123!",
                RemotePath = "/httpdocs"
            };

            await Assert.ThrowsAsync<AppException>(() => servis.PullAsync(istek));

            // 1) Başlangıç ve hata kaydedilmiş mi?
            Assert.True(
                gunlukcu.Icerir(LogLevel.Information, "ftp://127.0.0.1:1/httpdocs"),
                $"Goc baslangici yazilmadi. Yazilanlar:\n{gunlukcu.TumMetin}");
            Assert.NotEmpty(gunlukcu.Seviyede(LogLevel.Error));

            // 2) SIR DENETİMİ — parola ve kullanıcı adı hiçbir satırda geçmemeli.
            Assert.DoesNotContain("CokGizliParola123!", gunlukcu.TumMetin, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("gocKullanicisi", gunlukcu.TumMetin, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(hedefKok, recursive: true);
        }
    }

    /// <summary>
    /// Günlükçü İSTEĞE BAĞLI: yeni bir zorunlu parametre mevcut dar kapsamlı kurulumları kırardı.
    /// Günlükçüsüz çalışırken hiçbir yol <c>NullReferenceException</c> atmamalı.
    /// </summary>
    [Fact]
    public async Task Gunlukcusuz_kurulum_calismaya_devam_eder()
    {
        var hedefKok = Path.Combine(Path.GetTempPath(), "ada-goc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(hedefKok);

        try
        {
            var servis = new FileMigrationService(new SahteGocSiteServisi(hedefKok));

            var hata = await Assert.ThrowsAsync<AppException>(() => servis.PullAsync(new RemoteFileMigrationRequest
            {
                SiteName = "ornek.test",
                Protocol = "ftp",
                Host = "127.0.0.1",
                Port = 1,
                RemotePath = "/"
            }));

            Assert.StartsWith("migration.", hata.MessageKey, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(hedefKok, recursive: true);
        }
    }

    private sealed class SahteGocSiteServisi(string fizikselYol) : IIisSiteService
    {
        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SiteDto { Name = siteName, PhysicalPath = fizikselYol });

        public Task<IReadOnlyCollection<SiteDto>> GetSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> CreateSiteAsync(CreateSiteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> UpdatePhysicalPathAsync(string siteName, string physicalPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> StartSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> StopSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> AddBindingAsync(string siteName, SiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> RemoveBindingAsync(string siteName, RemoveSiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<SiteDto>> ImportExistingSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
