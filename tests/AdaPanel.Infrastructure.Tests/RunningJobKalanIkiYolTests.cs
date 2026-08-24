using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Core.Dtos.Antivirus;
using AdaPanel.Core.Dtos.Audit;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Components;
using AdaPanel.Core.Dtos.RunningJobs;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.RunningJobs;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Subscriptions;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// ÇALIŞAN İŞ KAYDININ kapsamadığı SON İKİ uzun yol: zamanlanmış abonelik yedeği ve ClamAV kurulumu.
/// </summary>
/// <remarks>
/// <para>
/// <b>KAPATILAN BOŞLUK.</b> Uzun işlerin sunucu tarafı kaydı (<see cref="IRunningJobService"/>)
/// yedek, geri yükleme, göç ve bileşen kurulumunu kapsıyordu — ama İKİ yol dışarıda kalmıştı:
/// <list type="number">
/// <item><b>Zamanlanmış abonelik yedeği</b>: elle tetiklenen tur (<c>schedules/{id}/run</c>) kaydı
/// açıyordu, arka planda koşan tur (<c>RunDueAsync</c>) açmıyordu. Yani gecenin üçünde başlayan iki
/// saatlik bir yedek sürerken müşteri panele girdiğinde ekran BOŞ görünüyordu.</item>
/// <item><b>ClamAV kurulumu</b>: tarama kapsanıyordu, kurulum kapsanmıyordu — oysa ~250 MB indirme
/// ve servis kaydı içeriyor, yani dakikalarca sürüyor.</item>
/// </list>
/// </para>
/// <para>
/// <b>NE KANITLANIYOR, NE KANITLANMIYOR.</b> Bu makinede AYAKTA ClamAV, gerçek bir yedek motoru ve
/// gerçekten koşan bir zamanlayıcı YOK. Burada kanıtlanan şey panelin DURUM MUHASEBESİDİR: kayıt
/// gerçekten açılıyor mu, iş sürerken sunucudan geri okunabiliyor mu, sahibi doğru mu belirleniyor,
/// iş bitince yuva boşalıyor mu, ikinci iş engelleniyor mu. Kanıtlanmayan: gerçek bir ClamAV
/// kurulumunun ya da gerçek bir arşivlemenin bu kaydın altında tamamlandığı. Zamanlayıcı için
/// <c>SubscriptionBackupWorker</c>'ın on dakikada bir <c>RunDueAsync</c> çağırdığı da ölçülmedi;
/// burada doğrudan <c>RunDueAsync</c> çağrılıyor.
/// </para>
/// </remarks>
public sealed class RunningJobKalanIkiYolTests
{
    // ===============================================================================================
    // 1) ZAMANLANMIS ABONELIK YEDEGI
    // ===============================================================================================

    /// <summary>
    /// ASIL DÜZELTME: arka plan turu SÜRERKEN iş sunucudan geri okunabiliyor, tur bitince yuva
    /// boşalıyor.
    /// </summary>
    /// <remarks>
    /// Ölçüm işin TAM ORTASINDA yapılıyor: sahte yedek servisi çağrıldığı anda çalışan-iş durumunu
    /// okuyor. Turdan SONRA okumak hiçbir şey kanıtlamazdı — kayıt zaten kapanmış olurdu ve
    /// "hiç açılmadı" ile "açılıp kapandı" birbirinden ayırt edilemezdi.
    /// <para>
    /// MUTASYON: <c>RunDueAsync</c> içindeki <c>BeginAsync</c> satırı kaldırılırsa
    /// <c>TurSirasindakiDurum</c> boş kalır ve bu test kırılır.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Zamanlanmis_Tur_Suresince_Calisan_Is_Kaydi_Aciliyor()
    {
        using var alan = new IsAlani();
        var abonelik = Guid.NewGuid();
        await alan.ProgramKurAsync(abonelik);

        await alan.Programlar().RunDueAsync(alan.Iptal);

        var suren = Assert.Single(alan.YedekServisi.TurSirasindakiDurum!.Running);
        Assert.Equal(RunningJobKinds.Backup, suren.Kind);
        Assert.Equal(abonelik.ToString("N"), suren.Title);
        Assert.NotEqual(default, suren.StartedAtUtc);

        // Tur bitti: yuva BOŞALMALI. Boşalmasaydı panel bir daha hiç yedek alamaz (409) ve ekranda
        // sonsuz "sürüyor" şeridi gösterirdi.
        var turdanSonra = await alan.Isler().GetStatusAsync([RunningJobKinds.Backup], null, alan.Iptal);
        Assert.Empty(turdanSonra.Running);
        Assert.Empty(turdanSonra.Interrupted);
    }

    /// <summary>
    /// SAHİP ABONELİKTİR — ve bu ölçülmüş bir karardır, tercih değil.
    /// </summary>
    /// <remarks>
    /// Kayıt kiracı süzgecinden geçiyor: sahipsiz işler (panel yedeği, posta sunucusu yedeği,
    /// bileşen kurulumu) yalnız yöneticide görünür. Zamanlanmış abonelik yedeği sahipsiz
    /// bırakılsaydı müşteri KENDİ aboneliğinin gece yedeğini kendi panelinde göremezdi — yani
    /// düzeltmenin tam da hedeflediği kullanıcı hiçbir şey kazanmazdı. Ters yön de ölçülüyor: BAŞKA
    /// bir kiracı bu işi GÖRMEMELİ, yoksa etiketteki abonelik kimliği sızardı.
    /// </remarks>
    [Fact]
    public async Task Zamanlanmis_Yedegin_Sahibi_Abonelik()
    {
        using var alan = new IsAlani();
        var abonelik = Guid.NewGuid();
        var baskaKiraci = Guid.NewGuid();
        await alan.ProgramKurAsync(abonelik);

        alan.YedekServisi.TurSirasindaOku = async isler =>
        {
            alan.YedekServisi.SahibininGordugu = await isler.GetStatusAsync(
                [RunningJobKinds.Backup], new HashSet<string> { abonelik.ToString("N") }, alan.Iptal);
            alan.YedekServisi.YabancininGordugu = await isler.GetStatusAsync(
                [RunningJobKinds.Backup], new HashSet<string> { baskaKiraci.ToString("N") }, alan.Iptal);
        };

        await alan.Programlar().RunDueAsync(alan.Iptal);

        Assert.Single(alan.YedekServisi.SahibininGordugu!.Running);
        Assert.Empty(alan.YedekServisi.YabancininGordugu!.Running);
    }

    /// <summary>
    /// Başka bir yedek işi sürerken zamanlanmış tur ERTELENİR — ve sessizce KAYBOLMAZ.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kayıt tür başına tek yuvalıdır, dolayısıyla açık bir yedek işi varken arka plan turu 409
    /// alır. Ölçülen KRİTİK şey şu: 409 alındığında program kaydının <c>LastRunUtc</c> damgası
    /// GÜNCELLENMEMELİ. Güncellenseydi tur "yapıldı" sayılır ve o abonelik o gün yedeksiz kalırdı —
    /// yani düzeltme, kapattığından daha kötü bir sessiz veri kaybı üretirdi.
    /// </para>
    /// <para>
    /// MUTASYON: <c>RunDueAsync</c>'teki 409 dalı kaldırılıp genel <c>catch</c>'e bırakılsa test
    /// yine geçer (damga yine yazılmaz) — bu yüzden test damgaya BAKIYOR, log metnine değil.
    /// Asıl mutasyon şudur: <c>BeginAsync</c> <c>CalistirAsync</c>'ten SONRA çağrılsaydı ya da
    /// hiç çağrılmasaydı yedek gerçekten koşar, <c>YedekCagrildi</c> true olur ve test kırılır.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Baska_Yedek_Surerken_Zamanlanmis_Tur_Erteleniyor_Ve_Damga_Yazilmiyor()
    {
        using var alan = new IsAlani();
        var abonelik = Guid.NewGuid();
        await alan.ProgramKurAsync(abonelik);

        // Elle tetiklenmiş bir yedek sürüyormuş gibi yuvayı AÇIK tut.
        await using var elleTetiklenen = await alan.Isler().BeginAsync(
            RunningJobKinds.Backup, "shop.example.com", subjectUserId: null, alan.Iptal);

        // Tur DÜŞMEZ: bir aboneliğin ertelenmesi diğerlerini durdurmamalı.
        await alan.Programlar().RunDueAsync(alan.Iptal);

        Assert.False(alan.YedekServisi.YedekCagrildi, "Yuva doluyken yedek yine de calistirildi.");

        var program = await alan.Programlar().GetScheduleAsync(abonelik, alan.Iptal);
        Assert.Null(program.LastRunUtc);
        Assert.Equal(string.Empty, program.LastStatus);
    }

    // ===============================================================================================
    // 2) CLAMAV KURULUMU
    // ===============================================================================================

    /// <summary>
    /// ClamAV kurulumu SÜRERKEN kayıt sunucudan okunabiliyor ve türü <c>software</c>.
    /// </summary>
    /// <remarks>
    /// Tür ÖNEMLİ ve ölçülüyor: bu uç <c>SoftwareController</c> ile aynı <c>IComponentBootstrapper</c>
    /// üzerinden aynı katalog girdisini kurar. Ayrı bir tür ("antivirus") uydurulsaydı tek yuvalı
    /// kayıt ikiye bölünür, yönetici Yazılım sayfasından PostgreSQL kurarken buradan ClamAV
    /// kurabilir ve aynı bootstrapper'a iki kurulum birden girerdi. Ayrıca kayıt SAHİPSİZ olmalı:
    /// sunucu geneli bir iştir, hiçbir kiracının şeridinde görünmemeli.
    /// </remarks>
    [Fact]
    public async Task ClamAV_Kurulumu_Suresince_Calisan_Is_Kaydi_Aciliyor()
    {
        using var alan = new IsAlani();
        var isler = alan.Isler();
        RunningJobsStatusDto? kurulumSirasinda = null;
        RunningJobsStatusDto? kiracininGordugu = null;

        var bootstrapper = new SahteBilesenKurucu(async () =>
        {
            kurulumSirasinda = await isler.GetStatusAsync([RunningJobKinds.Software], null, alan.Iptal);
            kiracininGordugu = await isler.GetStatusAsync(
                [RunningJobKinds.Software], new HashSet<string> { Guid.NewGuid().ToString("N") }, alan.Iptal);
        });

        var denetleyici = Denetleyici(bootstrapper, isler);

        await denetleyici.InstallAsync();

        var suren = Assert.Single(kurulumSirasinda!.Running);
        Assert.Equal(RunningJobKinds.Software, suren.Kind);
        Assert.Equal("clamav", suren.Title);

        // SAHİPSİZ: kapsamlı bir kullanıcı bu işi görmemeli.
        Assert.Empty(kiracininGordugu!.Running);

        // Kurulum bitti -> yuva boşaldı.
        Assert.Empty((await isler.GetStatusAsync([RunningJobKinds.Software], null, alan.Iptal)).Running);
    }

    /// <summary>
    /// Kurulum sürerken İKİNCİ bir kurulum 409 alır ve BİRİNCİNİN kaydını EZMEZ.
    /// </summary>
    /// <remarks>
    /// Ezseydi panel, biten işin hangisi olduğunu söyleyemezdi. Ayrıca hata anahtarının
    /// <c>runningjob.</c> ailesinden olması gerekir: arayüz o anahtarların çevirisini taşıyor,
    /// başka bir anahtar kullanıcının ekranında HAM olarak belirirdi.
    /// </remarks>
    [Fact]
    public async Task Kurulum_Surerken_Ikinci_Kurulum_409_Aliyor()
    {
        using var alan = new IsAlani();
        var isler = alan.Isler();

        await using var birinci = await isler.BeginAsync(
            RunningJobKinds.Software, "postgresql", subjectUserId: null, alan.Iptal);

        var bootstrapper = new SahteBilesenKurucu(() => Task.CompletedTask);
        var denetleyici = Denetleyici(bootstrapper, isler);

        var hata = await Assert.ThrowsAsync<Core.Common.AppException>(() => denetleyici.InstallAsync());

        Assert.Equal(409, hata.HttpStatusCode);
        Assert.StartsWith("runningjob.", hata.MessageKey, StringComparison.Ordinal);
        Assert.False(bootstrapper.Cagrildi, "409'a ragmen kurulum baslatildi.");

        // Birincinin kaydı YERİNDE durmalı.
        var durum = await isler.GetStatusAsync([RunningJobKinds.Software], null, alan.Iptal);
        Assert.Equal("postgresql", Assert.Single(durum.Running).Title);
    }

    // ===============================================================================================
    // 3) SOZLESME NOBETCILERI
    // ===============================================================================================

    /// <summary>
    /// SAHTE YÜZDE YASAĞI: yeni açılan iki yol da yalnız <c>Kind</c>/<c>Title</c>/<c>StartedAtUtc</c>
    /// yayımlar.
    /// </summary>
    /// <remarks>
    /// Bu iki iş de ölçülebilir bir ilerleme üretmiyor: zamanlanmış yedek sıkıştırırken toplam dosya
    /// sayısını bilmiyor, ClamAV paketinin boyutu sunucudan gelmeyebiliyor. Yüzde göstermenin tek
    /// yolu onu UYDURMAK olurdu. Sözleşmeyi burada da çiviliyoruz ki "kullanıcı yüzde istiyor"
    /// baskısı DTO'ya sessizce bir alan eklemesin.
    /// </remarks>
    [Fact]
    public void Suren_Is_Sozlesmesi_Yuzde_Alani_Tasimiyor()
    {
        var alanlar = typeof(RunningJobDto).GetProperties().Select(p => p.Name).OrderBy(ad => ad).ToArray();

        Assert.Equal(new[] { "Kind", "StartedAtUtc", "Title" }, alanlar);
    }

    /// <summary>
    /// ClamAV kurulum ucu isteğin İPTAL TOKEN'INA bağlanmamalı.
    /// </summary>
    /// <remarks>
    /// Bu, aynı denetleyicide tarama/tanım uçları için zaten yazılı olan kuralın kurulum yolundaki
    /// karşılığıdır ve ÖLÇÜLEBİLİR bir arıza kapatır: ASP.NET Core eylem imzasındaki
    /// <c>CancellationToken</c>'a <c>HttpContext.RequestAborted</c>'ı bağlar, yani kullanıcı 250 MB'lık
    /// indirmenin ortasında sayfayı yenilediğinde kurulum yarıda kesilir ve yarım kurulmuş bir
    /// bileşen bırakır. Kaydın kendisi bunu çözmez — kayıt işi hayatta tutmaz, yalnız görünür kılar.
    /// </remarks>
    [Fact]
    public void ClamAV_Kurulum_Ucu_Istek_Iptaline_Baglanmiyor()
    {
        var eylem = typeof(AntivirusController).GetMethod(nameof(AntivirusController.InstallAsync))!;

        Assert.Empty(eylem.GetParameters());
    }

    // ===============================================================================================
    // Yardimcilar
    // ===============================================================================================

    private static AntivirusController Denetleyici(IComponentBootstrapper bootstrapper, IRunningJobService isler)
    {
        var kullanici = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.Role, "Administrator")
            ],
            "test"));

        return new AntivirusController(new SahteAntivirus(), bootstrapper, isler, new SessizDenetimKaydi())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = kullanici }
            }
        };
    }

    private sealed class IsAlani : IDisposable
    {
        private readonly CancellationTokenSource iptal = new(TimeSpan.FromSeconds(120));

        public IsAlani()
        {
            Kok = Path.Combine(Path.GetTempPath(), $"adapanel-kalan-isler-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Kok);
            DosyaDeposu = new JsonFileStore(
                Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Kok }));

            // TEK örnek: servis diske yazıyor ve testin okuduğu kayıt, işi açan kaydın TA KENDİSİ
            // olmalı. İki ayrı örnek kurmak da çalışırdı (aynı dosya) ama sahiplik muhasebesini
            // gereksizce dolaylı hale getirirdi.
            Islerimiz = new RunningJobService(DosyaDeposu, NullLogger<RunningJobService>.Instance);
            YedekServisi = new SahteYedekServisi(Islerimiz);
        }

        public string Kok { get; }

        public JsonFileStore DosyaDeposu { get; }

        private RunningJobService Islerimiz { get; }

        public SahteYedekServisi YedekServisi { get; }

        public CancellationToken Iptal => iptal.Token;

        public IRunningJobService Isler() => Islerimiz;

        public SubscriptionBackupScheduleService Programlar() => new(
            DosyaDeposu,
            YedekServisi,
            new OlcumsuzKapsam(),
            new OlcumsuzPlanlar(),
            new DuzMetinKoruyucu(),
            new ConcurrencyLock(),
            Islerimiz,
            NullLogger<SubscriptionBackupScheduleService>.Instance);

        /// <summary>ZAMANI GELMİŞ bir program yazar (aralık modu + hiç çalışmamış = hemen due).</summary>
        public Task ProgramKurAsync(Guid abonelik) => Programlar().UpdateScheduleAsync(
            abonelik,
            new UpdateSubscriptionBackupScheduleRequest
            {
                Enabled = true,
                ScheduleMode = "interval",
                IntervalHours = 24,
                TargetType = "local"
            },
            Iptal);

        public void Dispose()
        {
            iptal.Dispose();
            try
            {
                Directory.Delete(Kok, recursive: true);
            }
            catch
            {
                // Geçici klasör silinemedi; testin sonucuyla ilgisi yok.
            }
        }
    }

    /// <summary>
    /// Yedek ALMAYAN yedek servisi: tek işi, çağrıldığı ANDA çalışan-iş durumunu okumak.
    /// Gerçek arşivleme bu testlerin sorusu değil (ve bu makinede IIS/DB/mail zaten yok).
    /// </summary>
    private sealed class SahteYedekServisi(IRunningJobService isler) : IBackupService
    {
        public bool YedekCagrildi { get; private set; }

        public RunningJobsStatusDto? TurSirasindakiDurum { get; private set; }

        public RunningJobsStatusDto? SahibininGordugu { get; set; }

        public RunningJobsStatusDto? YabancininGordugu { get; set; }

        /// <summary>Varsayılan ölçümün yerine geçen özel okuma (kiracı süzgeci testi için).</summary>
        public Func<IRunningJobService, Task>? TurSirasindaOku { get; set; }

        public async Task<BackupRecordDto> BackupSubscriptionAsync(BackupSubscriptionRequest request, CancellationToken cancellationToken = default)
        {
            YedekCagrildi = true;
            if (TurSirasindaOku is null)
            {
                TurSirasindakiDurum = await isler.GetStatusAsync([RunningJobKinds.Backup], null, cancellationToken);
            }
            else
            {
                await TurSirasindaOku(isler);
            }

            return new BackupRecordDto
            {
                Id = "b1",
                Name = "abonelik-yedegi.zip",
                BackupMode = "full",
                ItemCount = 1,
                Size = 1024
            };
        }

        public Task<int> PruneSubscriptionAsync(Guid userId, int retentionDays, int keepCount, BackupPruneRemoteOptions? remote = null, CancellationToken cancellationToken = default)
            => Task.FromResult(0);

        public Task<IReadOnlyCollection<BackupRecordDto>> GetBackupsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupRecordDto> BackupSiteAsync(BackupSiteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupRecordDto> BackupDatabaseAsync(BackupDatabaseRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupRecordDto> BackupMailAsync(BackupMailRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupRecordDto> BackupPanelAsync(BackupPanelRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupRecordDto> RestoreAsync(RestoreBackupRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupEntriesDto> GetEntriesAsync(string backupId, string? prefix, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RestoreBackupItemsResultDto> RestoreItemsAsync(RestoreBackupItemsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> PruneAsync(int retentionDays, BackupPruneRemoteOptions? remote = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupRemoteTestResultDto> TestRemoteTargetAsync(BackupRemoteTestRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupVerificationResultDto> VerifyBackupsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupChainDto> GetChainAsync(string backupId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteBackupAsync(string backupId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupDeleteResultDto> DeleteBackupChainAsync(string backupId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupEncryptionSettingsDto> GetEncryptionSettingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupEncryptionSettingsDto> SetEncryptionSettingsAsync(SetBackupEncryptionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>
    /// Kapsamı ÖLÇEMEYEN kapsam servisi. Servis bunu zaten hesaba katıyor: kota okunamazsa
    /// "sınırsız" sayılıyor (ölçemediğimiz bir sınır yüzünden müşterinin yedeğini hiç almamak,
    /// kotayı korumaktan pahalıdır). Yani bu fake, üretimde de olabilecek bir durumu taklit ediyor.
    /// </summary>
    private sealed class OlcumsuzKapsam : ICustomerScopeService
    {
        public Task<CustomerScope> GetScopeAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> GetAssignedSitesAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetScopeAsync(Guid userId, IEnumerable<string> siteNames, int maxSites, int maxDiskMb, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetExtraLimitsAsync(Guid userId, int maxDatabases, int maxMailboxes, int maxScheduledTasks, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveUserAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveSiteFromAllScopesAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> GetAllowedMailDomainsAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long> GetDiskUsageMbAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetOwnerAsync(Guid userId, Guid? ownerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid?> GetOwnerAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetOwnedUserIdsAsync(Guid ownerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> GetResellerSitesAsync(Guid resellerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> GetResellerMailDomainsAsync(Guid resellerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid?> GetSiteOwnerUserIdAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetParentSubscriptionAsync(Guid userId, Guid? parentUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid?> GetParentSubscriptionAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetSubUserIdsAsync(Guid parentUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetSubUserSectionsAsync(Guid userId, IReadOnlyCollection<string>? sections, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>?> GetSubUserSectionsAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetPlanAsync(Guid userId, string? planId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetExpiryAsync(Guid userId, DateTime? expiresAtUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SuspendAsync(Guid userId, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ResumeAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetSuspendedUserIdsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetSubscriptionUserIdsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class OlcumsuzPlanlar : IServicePlanService
    {
        public Task<IReadOnlyCollection<ServicePlanDto>> GetPlansAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ServicePlanDto?> GetPlanAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ServicePlanDto> CreatePlanAsync(ServicePlanRequest request, Guid? ownerId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ServicePlanDto> UpdatePlanAsync(string id, ServicePlanRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeletePlanAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Testte DPAPI kullanmıyoruz; bu testlerin sorusu uzak parola değil.</summary>
    private sealed class DuzMetinKoruyucu : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;

        public string Unprotect(string protectedValue) => protectedValue;
    }

    /// <summary>Kurulum YAPMAYAN bootstrapper: tek işi, kurulum sürerken verilen ölçümü çalıştırmak.</summary>
    private sealed class SahteBilesenKurucu(Func<Task> kurulumSirasinda) : IComponentBootstrapper
    {
        public bool Cagrildi { get; private set; }

        public async Task<ComponentInstallResultDto> InstallAsync(
            string componentId,
            Guid? actorUserId,
            string? ipAddress,
            bool force = false,
            CancellationToken cancellationToken = default)
        {
            Cagrildi = true;
            await kurulumSirasinda();
            return new ComponentInstallResultDto { ComponentId = componentId, Status = "Installed" };
        }

        public Task<SoftwareCatalogDto> GetCatalogAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SoftwareCatalogDto> CheckForUpdatesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> GetPendingUpdateCountAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<ComponentInstallResultDto>> InstallDefaultsAsync(Guid? actorUserId, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> RemoveAsync(string componentId, Guid? actorUserId, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class SahteAntivirus : IAntivirusService
    {
        public Task<AntivirusStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new AntivirusStatusDto());

        public Task<AntivirusStatusDto> UpdateDefinitionsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AntivirusScanResultDto> ScanAsync(AntivirusScanRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Denetim kaydı bu testlerin sorusu değil; yazma sessizce yutulur.</summary>
    private sealed class SessizDenetimKaydi : IAuditService
    {
        public Task WriteAsync(string action, string resource, string details, Guid? userId, string? ipAddress, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task WriteForSubscriptionAsync(string action, string resource, string details, Guid? userId, Guid? subscriptionUserId, string? ipAddress, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyCollection<AuditLogDto>> GetRecentAsync(int take, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AuditLogPageDto> SearchAsync(AuditLogQuery query, AuditVisibility visibility, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<AuditLogDto> StreamAsync(AuditLogQuery query, AuditVisibility visibility, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AuditVisibility> ResolveVisibilityAsync(Guid userId, UserRole role, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AuditRetentionSettingsDto> GetRetentionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AuditRetentionSettingsDto> SetRetentionAsync(int retentionDays, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> PurgeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
