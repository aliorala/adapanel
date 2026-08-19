using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AdaPanel.Core.Dtos.Components;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Ssl;
using AdaPanel.Infrastructure.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Uzlastirmanin GERCEKTEN CAGRILDIGINI korur.
/// <para>
/// Denetimin bulgusu "uzlastirma yanlis" degil, <b>hic yoktu</b> idi: <c>RenewAllAsync</c> kaydi
/// GUNCELLEMIYORDU ve listeleme de kayda oldugu gibi guveniyordu. Saf uzlastiriciyi (bkz.
/// <see cref="SslStateReconciliationTests"/>) tek basina dogrulamak bu hatayi yakalamaz --
/// fonksiyon dogru olsa da cagrilmayabilir. Bu yuzden burada gercek servis, gercek dosya deposu
/// ve gercek Windows sertifika deposu uzerinden kosuluyor.
/// </para>
/// <para>
/// Kayittaki host bu makinede bulunmayan bir test alan adidir; sertifika deposu OKUNUR ve
/// karsiligi bulunamaz -- yani "olculdu ve yok" hâli gercekten yasanir.
/// </para>
/// </summary>
public sealed class SslStateVerificationWiringTests : IDisposable
{
    private const string OlmayanHost = "adapanel-testte-olmayan-host.invalid";

    private readonly string _ust = Path.Combine(Path.GetTempPath(), $"adapanel-ssl-{Guid.NewGuid():N}");
    private readonly SqliteConnection _baglanti = new("Filename=:memory:");
    private readonly AdaPanelDbContext _dbContext;

    public SslStateVerificationWiringTests()
    {
        Directory.CreateDirectory(_ust);
        _baglanti.Open();
        _dbContext = new AdaPanelDbContext(
            new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(_baglanti).Options);
        _dbContext.Database.EnsureCreated();
    }

    /// <summary>
    /// ASIL BULGU: <c>RenewAllAsync</c> kaydi hic guncellemiyordu. win-acme kurulu OLMASA bile
    /// kayit dogrulanabilir -- sertifika deposunu okumak win-acme'ye bagli degildir.
    /// </summary>
    [Fact]
    public async Task RenewAll_VerifiesTheRecordAgainstTheCertificateStore()
    {
        KayitYaz(NotAfterUtc: DateTime.UtcNow.AddDays(60));

        var sonuc = await Servis().RenewAllAsync();

        // win-acme kurulu degil: yenileme OLCULEMEDI ve bu SOYLENIYOR...
        Assert.False(sonuc.Measured);

        // ...ama kayit yine de gercekle karsilastirildi.
        Assert.Equal(1, sonuc.MissingFromStoreCount);
        Assert.Equal(0, sonuc.VerifiedCount);

        var kayit = Assert.Single(await KayitOkuAsync());
        Assert.False(kayit.IsPresentInStore);
        Assert.NotNull(kayit.LastVerifiedUtc);
    }

    /// <summary>
    /// BULGUNUN TAM MERKEZI: win-acme KURULU oldugunda, yani gercek yenileme yolunda, kayit
    /// yenileme turundan SONRA tazeleniyor mu.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Eksik olan tam olarak buydu: <c>wacs --renew</c> calisiyor, sertifikayi YENI parmak izi ve
    /// YENI bitis tarihiyle depoya koyuyor, ama panel <c>ssl.json</c>'a hic donmuyordu. Kayittaki
    /// tarih ilk alim gununden kalma oldugu icin danisman "sertifikanin suresi DOLDU" diyor,
    /// otomasyon da HER GUN ayni uyari e-postasini gonderiyordu.
    /// </para>
    /// <para>
    /// win-acme yerine, argumanlarini yok sayan zararsiz bir sistem araci <c>wacs.exe</c> adiyla
    /// kopyalanir: amac win-acme'yi taklit etmek degil, <b>yenileme dalinin GERCEKTEN
    /// calistirildigi</b> kosulda uzlastirmanin yapildigini kanitlamaktir. Dis arac ne donerse
    /// donsun kayit dogrulanmalidir.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AfterARenewalRun_TheRecordIsRefreshedFromTheCertificateStore()
    {
        KayitYaz(NotAfterUtc: DateTime.UtcNow.AddDays(60));
        WinAcmeKur();

        var sonuc = await Servis().RenewAllAsync();

        // Yenileme dali GERCEKTEN kosuldu (win-acme bulundu; "kurulu degil" dalina dusulmedi).
        Assert.DoesNotContain("kurulu degil", sonuc.FailureReason, StringComparison.OrdinalIgnoreCase);

        // ...ve tur bittikten sonra kayit gercekle karsilastirildi.
        Assert.Equal(1, sonuc.MissingFromStoreCount);

        var kayit = Assert.Single(await KayitOkuAsync());
        Assert.False(kayit.IsPresentInStore);
        Assert.NotNull(kayit.LastVerifiedUtc);
    }

    /// <summary>
    /// HER GUN GONDERILEN YANLIS "SURESI DOLDU" MAILININ BITTIGININ KANITI.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Senaryo birebir canli durumdur: <c>ssl.json</c>'daki kayit BES GUN ONCE dolmus gorunuyor,
    /// oysa sertifika depoda GECERLI ve 89 gun omru var (win-acme onu yenilemis). Eski kod kaydi
    /// hic okumadigi icin danisman "sertifikanin suresi DOLDU" kritik uyarisini, otomasyon da her
    /// gun ayni e-postayi uretiyordu -- kullanici bir sure sonra butun SSL uyarilarina bakmaz
    /// oluyordu.
    /// </para>
    /// <para>
    /// Test GERCEK Windows sertifika deposunu kullanir (kullanici deposu; yonetici yetkisi
    /// gerekmez) ve kendi ekledigi sertifikayi sonunda temizler.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ARecordThatLooksExpired_IsRefreshedFromTheRealCertificate()
    {
        var host = $"adapanel-test-{Guid.NewGuid():N}.invalid";
        using var sertifika = KendindenImzaliUret(host, gun: 89);
        DepoyaEkle(sertifika);

        try
        {
            // Panelin kaydi: "suresi bes gun once doldu" (yenilemeden onceki donmus gercek).
            KayitYaz(NotAfterUtc: DateTime.UtcNow.AddDays(-5), host: host);

            var sonuc = await Servis().RenewAllAsync();

            Assert.Equal(1, sonuc.VerifiedCount);
            Assert.Equal(0, sonuc.MissingFromStoreCount);

            var kayit = Assert.Single(await KayitOkuAsync());

            // ARTIK "SURESI DOLDU" DEMIYOR: tarih gercek sertifikadan geldi.
            Assert.True(kayit.NotAfterUtc > DateTime.UtcNow, $"Kayittaki bitis hâlâ gecmiste: {kayit.NotAfterUtc:o}");
            Assert.Equal(sertifika.NotAfter.ToUniversalTime(), kayit.NotAfterUtc);

            // Parmak izi de tazelendi: yenilenen sertifika ARTIK dogru kimlikle taniniyor.
            Assert.Equal(sertifika.Thumbprint, kayit.Thumbprint);
            Assert.True(kayit.IsPresentInStore);

            // Kaynak da OLCULDU: kayit "letsencrypt" diyordu, depodaki sertifika kendinden imzali.
            Assert.Equal("self-signed", kayit.Source);
        }
        finally
        {
            DepodanSil(sertifika);
        }
    }

    /// <summary>
    /// ORTAK KURAL, olcum katmaninda: sertifika deposu HIC okunamadiysa sonuc "bulunamadi" DEGIL
    /// "olculemedi"dir. Bu ayrim silinirse tek bir erisim hatasi butun kayitlara kalici
    /// "depoda yok" damgasi vururdu.
    /// </summary>
    [Fact]
    public void WhenNoCertificateStoreCouldBeRead_TheProbeIsUnmeasured()
    {
        Assert.Equal(
            SslStoreProbeOutcome.Unmeasured,
            WinAcmeSslService.BuildProbe(storesReadable: false, certificate: null).Outcome);
    }

    /// <summary>Depo OKUNDU ve karsilik yoksa cevap kesindir: bulunamadi.</summary>
    [Fact]
    public void WhenTheStoreWasReadAndNothingMatched_TheProbeIsNotFound()
    {
        Assert.Equal(
            SslStoreProbeOutcome.NotFound,
            WinAcmeSslService.BuildProbe(storesReadable: true, certificate: null).Outcome);
    }

    /// <summary>
    /// Listeleme de kayda degil gercege bakar: ekran acildiginda "gecerli, 60 gun kaldi" diyen
    /// satirin karsiligi depoda yoksa bu, kayitta isaretlenir.
    /// </summary>
    [Fact]
    public async Task TheWorkspaceListing_VerifiesTheRecordAgainstTheCertificateStore()
    {
        KayitYaz(NotAfterUtc: DateTime.UtcNow.AddDays(60));

        var calisma = await Servis().GetSiteWorkspaceAsync("site");

        var gosterilen = Assert.Single(calisma.Certificates);
        Assert.False(gosterilen.IsPresentInStore);

        // Ekranda gorulen ile diskte duran ayni sey olmali; aksi hâlde uyari e-postasi (ayni
        // dosyayi okuyor) ekrandan farkli bir gercege inanmaya devam ederdi.
        Assert.False(Assert.Single(await KayitOkuAsync()).IsPresentInStore);
    }

    private WinAcmeSslService Servis() =>
        new(_dbContext,
            new SahteSiteServisi(_ust),
            new SahteDnsServisi(),
            Depo(),
            new SahteBootstrapper(),
            NullLogger<WinAcmeSslService>.Instance);

    private JsonFileStore Depo() =>
        // Tam nitelenmis: "Options" adi AdaPanel.Infrastructure.Options ad alaniyla cakisiyor.
        new(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = _ust }));

    private static X509Certificate2 KendindenImzaliUret(string host, int gun)
    {
        using var rsa = RSA.Create(2048);
        var istek = new CertificateRequest($"CN={host}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var uretilen = istek.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(gun));

        // Ozel anahtarli kalici kopya (KULLANICI deposu -> yonetici yetkisi gerekmez).
        return new X509Certificate2(
            uretilen.Export(X509ContentType.Pfx),
            string.Empty,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet);
    }

    private static void DepoyaEkle(X509Certificate2 sertifika)
    {
        using var depo = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        depo.Open(OpenFlags.ReadWrite);
        depo.Add(sertifika);
    }

    private static void DepodanSil(X509Certificate2 sertifika)
    {
        try
        {
            using var depo = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            depo.Open(OpenFlags.ReadWrite);
            depo.Remove(sertifika);
        }
        catch (CryptographicException)
        {
        }
    }

    private void KayitYaz(DateTime NotAfterUtc, string? host = null)
    {
        host ??= OlmayanHost;
        var klasor = Path.Combine(_ust, "config");
        Directory.CreateDirectory(klasor);
        File.WriteAllText(
            Path.Combine(klasor, "ssl.json"),
            $$"""
            [
              {
                "siteName": "site",
                "hostName": "{{host}}",
                "thumbprint": "AAAA",
                "friendlyName": "[win-acme] test",
                "subject": "CN={{host}}",
                "notAfterUtc": "{{NotAfterUtc:yyyy-MM-ddTHH:mm:ssZ}}",
                "source": "letsencrypt",
                "port": 443
              }
            ]
            """);
    }

    /// <summary>
    /// win-acme'yi "kurulu" yapar: servis, calistirilabiliri kurulu bilesen kaydindaki dizinde
    /// <c>wacs.exe</c> adiyla arar. Argumanlarini yok sayan zararsiz bir sistem araci kopyalanir.
    /// </summary>
    private void WinAcmeKur()
    {
        var klasor = Path.Combine(_ust, "win-acme");
        Directory.CreateDirectory(klasor);
        File.Copy(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "HOSTNAME.EXE"),
            Path.Combine(klasor, "wacs.exe"),
            overwrite: true);

        _dbContext.InstalledComponents.Add(new AdaPanel.Core.Entities.InstalledComponent
        {
            ComponentId = "win-acme",
            DisplayName = "win-acme",
            InstallDir = klasor,
            Status = AdaPanel.Core.Enums.ComponentInstallStatus.Installed
        });
        _dbContext.SaveChanges();
    }

    private async Task<SslCertificateDto[]> KayitOkuAsync() =>
        await Depo().ReadAsync("config/ssl.json", Array.Empty<SslCertificateDto>());

    public void Dispose()
    {
        _dbContext.Dispose();
        _baglanti.Dispose();
        try
        {
            Directory.Delete(_ust, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class SahteSiteServisi(string kok) : IIisSiteService
    {
        public Task<IReadOnlyCollection<SiteDto>> GetSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SiteDto
            {
                Id = 1,
                Name = siteName,
                PhysicalPath = kok,
                ApplicationRootPath = kok,
                ApplicationPoolName = "AdaPanel." + siteName,
                Bindings = [new SiteBindingDto { Protocol = "http", HostName = OlmayanHost, Port = 80 }]
            });

        public Task<SiteDto> CreateSiteAsync(CreateSiteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> UpdatePhysicalPathAsync(string siteName, string physicalPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> StartSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> StopSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> AddBindingAsync(string siteName, SiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> RemoveBindingAsync(string siteName, RemoveSiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyCollection<SiteDto>> ImportExistingSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class SahteBootstrapper : IComponentBootstrapper
    {
        public Task<SoftwareCatalogDto> GetCatalogAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SoftwareCatalogDto> CheckForUpdatesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> GetPendingUpdateCountAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ComponentInstallResultDto> InstallAsync(string componentId, Guid? actorUserId, string? ipAddress, bool force = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyCollection<ComponentInstallResultDto>> InstallDefaultsAsync(Guid? actorUserId, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyCollection<string>> RemoveAsync(string componentId, Guid? actorUserId, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class SahteDnsServisi : IDnsService
    {
        public Task<SiteDnsZoneDto> GetZoneAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDnsZoneDto> InstallWindowsDnsAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDnsZoneDto> UpdateZoneAsync(string siteName, UpdateDnsZoneRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDnsZoneDto> ResetDefaultsAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDnsZoneDto> SetZoneModeAsync(string siteName, SetDnsZoneModeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDnsZoneDto> TransferZoneNowAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> RepairDefaultsAsync(string siteName, bool applyChanges = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyCollection<string>?> GetPublishedRecordKeysAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDnsZoneDto> UpsertRecordAsync(string siteName, UpsertDnsRecordRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SubdomainRecordResult> EnsureSubdomainRecordAsync(string parentSiteName, string label, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDnsZoneDto> UpsertRecordsAsync(string siteName, IReadOnlyList<UpsertDnsRecordRequest> requests, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDnsZoneDto> DeleteRecordAsync(string siteName, string recordId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteZoneAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task EnsureZonePublishedAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DnsServerHealthDto> GetServerHealthAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DnsServerHealthDto> RepairServerAsync(bool applyDelayedAutoStart, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DnsServerHealthDto?> RunBindWatchdogAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DnsTemplateDto> GetTemplateAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DnsTemplateDto> UpdateTemplateAsync(UpdateDnsTemplateRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDnsZoneDto> SetDnssecAsync(string siteName, bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
