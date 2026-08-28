using System.IO.Compression;
using System.Text.Json;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Mail;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// SITE BASINA POSTA YEDEGI: arsivde GERCEK ileti var mi, yok mu -- ve arsiv bunu DOGRU soyluyor mu?
/// </summary>
/// <remarks>
/// <para>
/// KULLANICININ BIREBIR SARTI: "geri yuklenemeyen bir posta arsivi uretip 'yedeklendi' demek, hic
/// yedeklememekten KOTUDUR." Bu yuzden buradaki testlerin cogu MUTLULUK YOLUNU degil, YALAN
/// SOYLEME ihtimalini korur: posta alinamadiginda arsiv "posta dahil" DEMEMELI ve NEDENI
/// tasimalidir.
/// </para>
/// <para>
/// <b>NE TAKLIT EDILIYOR:</b> <c>vandelay</c> ve canli Stalwart calistirilmaz;
/// <see cref="IMailAccountArchiver"/> sahtelenir. Yani dogrulanan sey YEDEK SERVISININ KARARLARIDIR
/// (parca eklendi mi, manifest ne diyor, geri yukleme hangi alan adina hangi dosyalari verdi).
/// Vandelay'in kendi ice/disa aktarimi burada dogrulanmaz -- o, canli sunucuda ayrica olculdu.
/// </para>
/// </remarks>
public sealed class MailPerSiteBackupTests
{
    private const string Site = "ornek.com";

    // ===========================================================================================
    // 1) POSTA ALINAMADIGINDA ARSIV "DAHIL" DEMEZ
    // ===========================================================================================

    [Fact]
    public async Task Arsivleyici_YOKSA_posta_dahil_denmez_ve_neden_manifeste_yazilir()
    {
        using var alan = new PostaAlani();
        var servis = alan.ServisKur(arsivleyici: null);

        var kayit = await servis.BackupSiteAsync(alan.SiteIstegi(), alan.Iptal);

        var manifest = alan.Manifest(kayit);
        Assert.DoesNotContain("mail-messages", alan.Parcalar(manifest));
        Assert.Empty(alan.PostaGirdileri(kayit));

        // Manifest UYARIYI ve NEDENI tasir; sessiz kalmaz.
        Assert.Contains("MESSAGES are NOT included", manifest.GetProperty("mailNote").GetString());
        Assert.Contains("vandelay", manifest.GetProperty("mailMessagesError").GetString());
    }

    [Fact]
    public async Task On_kontrol_gecmezse_posta_dahil_denmez_ve_SEBEP_arsivde_durur()
    {
        using var alan = new PostaAlani();
        // Gercek hayattaki en olasi sebep: Stalwart'in duyurdugu JMAP adresi 443'ten karsilanmiyor.
        var arsivleyici = SahteArsivleyici.OnKontrolDusuyor(
            "Stalwart'in duyurdugu JMAP adresi (https://mail.ornek.com/.well-known/jmap) bu sunucudan ulasilamiyor");
        var servis = alan.ServisKur(arsivleyici);

        var kayit = await servis.BackupSiteAsync(alan.SiteIstegi(), alan.Iptal);

        var manifest = alan.Manifest(kayit);
        Assert.DoesNotContain("mail-messages", alan.Parcalar(manifest));
        Assert.Empty(alan.PostaGirdileri(kayit));
        Assert.Contains("ulasilamiyor", manifest.GetProperty("mailMessagesError").GetString());
    }

    /// <summary>
    /// KISMI BASARI = BASARISIZLIK. Iki hesabin biri alinamadiysa arsive HICBIRI konmaz.
    /// </summary>
    /// <remarks>
    /// Yarim bir posta arsivi en tehlikeli ciktidir: kullanici postasinin yedeklendigini sanir,
    /// eksigi ancak geri yuklerken -- yani en kotu anda -- gorur.
    /// </remarks>
    [Fact]
    public async Task Tek_hesap_bile_alinamazsa_HICBIR_hesap_arsive_konmaz()
    {
        using var alan = new PostaAlani();
        var arsivleyici = SahteArsivleyici.KismiBasari(
            basarili: ["kutu@" + Site],
            hatali: ["muhasebe@" + Site + ": baglanti koptu"]);
        var servis = alan.ServisKur(arsivleyici);

        var kayit = await servis.BackupSiteAsync(alan.SiteIstegi(), alan.Iptal);

        Assert.Empty(alan.PostaGirdileri(kayit));
        var manifest = alan.Manifest(kayit);
        Assert.DoesNotContain("mail-messages", alan.Parcalar(manifest));
        Assert.Contains("muhasebe@" + Site, manifest.GetProperty("mailMessagesError").GetString());
    }

    // ===========================================================================================
    // 2) POSTA ALINDIGINDA ARSIVDE GERCEKTEN DURUR
    // ===========================================================================================

    [Fact]
    public async Task Hesaplar_alindiginda_arsivde_ALAN_ADI_klasorunde_durur_ve_parca_eklenir()
    {
        using var alan = new PostaAlani();
        var servis = alan.ServisKur(SahteArsivleyici.Basarili(["kutu@" + Site, "muhasebe@" + Site]));

        var kayit = await servis.BackupSiteAsync(alan.SiteIstegi(), alan.Iptal);

        Assert.Equal(
            new[]
            {
                $"mail-messages/{Site}/kutu@{Site}.sqlite",
                $"mail-messages/{Site}/muhasebe@{Site}.sqlite"
            },
            alan.PostaGirdileri(kayit).OrderBy(ad => ad, StringComparer.Ordinal));

        var manifest = alan.Manifest(kayit);
        Assert.Contains("mail-messages", alan.Parcalar(manifest));
        Assert.Contains("messages included", manifest.GetProperty("mailNote").GetString());
        Assert.Equal(string.Empty, manifest.GetProperty("mailMessagesError").GetString());
    }

    /// <summary>Envanter (hesap/alias listesi) KALDIRILMADI: iletiler alinsa da alinmasa da yazilir.</summary>
    [Fact]
    public async Task Hesap_envanteri_iletilerden_BAGIMSIZ_olarak_korunur()
    {
        using var alan = new PostaAlani();
        var kayit = await alan.ServisKur(SahteArsivleyici.Basarili(["kutu@" + Site]))
            .BackupSiteAsync(alan.SiteIstegi(), alan.Iptal);

        Assert.Contains($"mail/{Site}.json", alan.Girdiler(kayit));
        Assert.Contains("mail-inventory", alan.Parcalar(alan.Manifest(kayit)));
    }

    // ===========================================================================================
    // 3) GERI YUKLEME TEK ALAN ADINI YAZAR
    // ===========================================================================================

    /// <summary>
    /// TATBIKAT: arsivde IKI alan adinin postasi varsa, her biri KENDI cagrisinda ve YALNIZ kendi
    /// dosyalariyla geri yuklenir. Bir alanin dosyasi digerinin cagrisina KARISMAZ.
    /// </summary>
    [Fact]
    public async Task Geri_yukleme_her_alan_adina_YALNIZ_kendi_dosyalarini_verir()
    {
        using var alan = new PostaAlani();
        var arsivleyici = SahteArsivleyici.Basarili(["kutu@" + Site]);
        var servis = alan.ServisKur(arsivleyici);

        var kayit = await servis.BackupSiteAsync(alan.SiteIstegi(), alan.Iptal);

        // Arsive BASKA bir musterinin alan adi da eklenir: geri yukleme onu kendi cagrisinda ve
        // ayri klasorde gormeli; iki alanin dosyalari asla ayni cagriya girmemeli.
        alan.ArsiveEkle(kayit, "mail-messages/komsu.com/kutu@komsu.com.sqlite");

        arsivleyici.GeriYuklemeler.Clear();
        await servis.RestoreAsync(new RestoreBackupRequest { BackupId = kayit.Id }, alan.Iptal);

        var cagrilar = arsivleyici.GeriYuklemeler.ToDictionary(cagri => cagri.Alan, cagri => cagri.Dosyalar);
        Assert.Equal([Site, "komsu.com"], cagrilar.Keys.OrderByDescending(ad => ad));
        Assert.Equal([$"kutu@{Site}.sqlite"], cagrilar[Site]);
        Assert.Equal(["kutu@komsu.com.sqlite"], cagrilar["komsu.com"]);
    }

    /// <summary>
    /// Arsivde posta VAR ama geri yukleyecek arac YOKSA: sessiz gecilmez, UYARI uretilir.
    /// </summary>
    [Fact]
    public async Task Arac_yokken_arsivdeki_posta_SESSIZCE_atlanmaz()
    {
        using var alan = new PostaAlani();
        var kayit = await alan.ServisKur(SahteArsivleyici.Basarili(["kutu@" + Site]))
            .BackupSiteAsync(alan.SiteIstegi(), alan.Iptal);

        // Ayni yedegi arsivleyicisi OLMAYAN bir servisle geri yukle (vandelay kaldirilmis sunucu).
        var sonuc = await alan.ServisKur(arsivleyici: null)
            .RestoreAsync(new RestoreBackupRequest { BackupId = kayit.Id }, alan.Iptal);

        // Anahtar MEVCUT ve cevrili olan "backup.mail_account_failed"tir (yeni anahtar uydurmak,
        // cevirisi olmayan HAM bir anahtarin ekranda gorunmesi demekti); durumu ayirt eden bilgi
        // Detail'de tasinir.
        var uyari = Assert.Single(sonuc.Warnings, item => item.Detail.Contains("vandelay"));
        Assert.Equal("backup.mail_account_failed", uyari.MessageKey);
        Assert.Contains(Site, uyari.Target);

        // AYRINTI DILDEN BAGIMSIZ. `Detail` bu depoda cevrilmez ve arayuz onu cevirinin altina HAM
        // basar; buradaki eski cumle Turkce harf de tasiyordu ("Arsivde ... YUKLENMEDI"), yani
        // Ingilizce panelde yari cevrilmis satir en gorunur haliyle cikiyordu. Denetim
        // TenantTreeWalkGuardTests'teki ORTAK nobetciyle yapilir.
        //
        // UC OLGU DA KORUNDU: arsivde posta kutusu var, eksik bilesen hangisi, sonuc ne.
        Assert.Contains("mailboxes=present", uyari.Detail, StringComparison.Ordinal);
        Assert.Contains("archiver=vandelay", uyari.Detail, StringComparison.Ordinal);
        Assert.Contains("archiver_state=not_configured", uyari.Detail, StringComparison.Ordinal);
        Assert.Contains("mail=not_restored", uyari.Detail, StringComparison.Ordinal);
        TenantTreeWalkGuardTests.DildenBagimsizOlmali(uyari.Detail, "posta arsivleyicisi yok uyarisi");
    }

    /// <summary>Geri yukleme hatalari YUTULMAZ; kullaniciya uyari olarak doner.</summary>
    [Fact]
    public async Task Geri_yukleme_hatasi_uyari_olarak_raporlanir()
    {
        using var alan = new PostaAlani();
        var arsivleyici = SahteArsivleyici.Basarili(["kutu@" + Site]);
        var servis = alan.ServisKur(arsivleyici);
        var kayit = await servis.BackupSiteAsync(alan.SiteIstegi(), alan.Iptal);

        arsivleyici.GeriYuklemeHatasi = "kutu@" + Site + ": JMAP ucu cevap vermedi";
        var sonuc = await servis.RestoreAsync(new RestoreBackupRequest { BackupId = kayit.Id }, alan.Iptal);

        var uyari = Assert.Single(sonuc.Warnings, item => item.Detail.Contains("JMAP ucu cevap vermedi"));
        Assert.Equal("backup.mail_account_failed", uyari.MessageKey);
        Assert.Equal(Site, uyari.Target);
    }

    // ===========================================================================================
    // 4) IZOLASYON KAPISI (saf): hangi hesap arsivi hangi alana yazilabilir?
    // ===========================================================================================

    [Theory]
    [InlineData("kutu@ornek.com", "ornek.com", true)]
    [InlineData("KUTU@Ornek.COM", "ornek.com", true)]
    [InlineData("kutu@komsu.com", "ornek.com", false)]
    [InlineData("kutu@alt.ornek.com", "ornek.com", false)]   // ALT alan bu alan degildir
    [InlineData("kutu@kotuornek.com", "ornek.com", false)]
    [InlineData("kutu", "ornek.com", false)]                  // alan adi yok
    [InlineData("@ornek.com", "ornek.com", false)]             // yerel kisim yok
    [InlineData("kutu@", "ornek.com", false)]
    public void Yabanci_alan_adina_ait_hesap_arsivi_REDDEDILIR(string adres, string alanAdi, bool beklenen)
        => Assert.Equal(beklenen, StalwartAccountArchiver.BelongsToDomain(adres, alanAdi));

    // ===========================================================================================
    // Sahteler ve alan
    // ===========================================================================================

    private sealed class SahteArsivleyici : IMailAccountArchiver
    {
        private MailArchivePreflightDto _onKontrol = new() { IsReady = true, SessionUrl = "https://mail.ornek.com/.well-known/jmap", ToolPath = "vandelay.exe" };
        private string[] _basarili = [];
        private string[] _hatali = [];

        public List<(string Alan, string[] Dosyalar)> GeriYuklemeler { get; } = [];

        public string? GeriYuklemeHatasi { get; set; }

        public static SahteArsivleyici Basarili(string[] adresler) => new() { _basarili = adresler };

        public static SahteArsivleyici KismiBasari(string[] basarili, string[] hatali) =>
            new() { _basarili = basarili, _hatali = hatali };

        public static SahteArsivleyici OnKontrolDusuyor(string neden) =>
            new() { _onKontrol = new MailArchivePreflightDto { IsReady = false, Reason = neden } };

        public Task<MailArchivePreflightDto> CheckAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_onKontrol);

        public Task<MailArchiveResultDto> ArchiveDomainAsync(string domain, string outputDirectory, CancellationToken cancellationToken = default)
        {
            if (!_onKontrol.IsReady)
            {
                return Task.FromResult(new MailArchiveResultDto { Failures = [_onKontrol.Reason] });
            }

            Directory.CreateDirectory(outputDirectory);
            var hesaplar = new List<MailAccountArchiveDto>();
            foreach (var adres in _basarili)
            {
                var yol = Path.Combine(outputDirectory, adres + ".sqlite");
                File.WriteAllText(yol, "SAHTE SQLITE ARSIVI");
                hesaplar.Add(new MailAccountArchiveDto { Address = adres, FilePath = yol });
            }

            return Task.FromResult(new MailArchiveResultDto { Accounts = hesaplar, Failures = _hatali });
        }

        public Task<IReadOnlyList<string>> RestoreDomainAsync(string domain, string archiveDirectory, CancellationToken cancellationToken = default)
        {
            var dosyalar = Directory.GetFiles(archiveDirectory, "*.sqlite").Select(Path.GetFileName).OfType<string>().OrderBy(ad => ad).ToArray();
            GeriYuklemeler.Add((domain, dosyalar));
            IReadOnlyList<string> hatalar = GeriYuklemeHatasi is null ? [] : [GeriYuklemeHatasi];
            return Task.FromResult(hatalar);
        }
    }

    private sealed class PostaAlani : IDisposable
    {
        private readonly CancellationTokenSource _iptal = new(TimeSpan.FromSeconds(60));

        public PostaAlani()
        {
            Kok = Path.Combine(Path.GetTempPath(), $"adapanel-postayedek-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Kok);
            DosyaDeposu = new JsonFileStore(
                Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Kok }));
            SiteKoku = Path.Combine(Kok, "wwwroot", "site");
            Directory.CreateDirectory(SiteKoku);
            File.WriteAllText(Path.Combine(SiteKoku, "index.html"), "MUSTERI ICERIGI");
            YedekKoku = Path.Combine(Kok, "backups");
        }

        public string Kok { get; }

        public string SiteKoku { get; }

        public string YedekKoku { get; }

        public JsonFileStore DosyaDeposu { get; }

        public CancellationToken Iptal => _iptal.Token;

        public BackupService ServisKur(IMailAccountArchiver? arsivleyici) =>
            new(new SahteSiteServisi(SiteKoku),
                null!,
                new SahtePostaServisi(),
                new SahteTercihServisi(YedekKoku),
                null!,
                DosyaDeposu,
                new SahteIstemciFabrikasi(),
                encryptionStore: null,
                logger: null,
                customerScopes: null,
                databaseOwnership: null,
                mailArchiver: arsivleyici);

        public BackupSiteRequest SiteIstegi() => new()
        {
            SiteName = Site,
            IncludeFiles = true,
            IncludeConfig = false,
            IncludeMail = true,
            TargetType = "local"
        };

        public IReadOnlyList<string> Girdiler(BackupRecordDto kayit)
        {
            using var arsiv = ZipFile.OpenRead(kayit.Path);
            return [.. arsiv.Entries.Select(girdi => girdi.FullName)];
        }

        public IEnumerable<string> PostaGirdileri(BackupRecordDto kayit) =>
            Girdiler(kayit).Where(ad => ad.StartsWith("mail-messages/", StringComparison.Ordinal));

        public JsonElement Manifest(BackupRecordDto kayit)
        {
            using var arsiv = ZipFile.OpenRead(kayit.Path);
            using var akis = arsiv.GetEntry("manifest.json")!.Open();
            using var okuyucu = new StreamReader(akis);
            return JsonDocument.Parse(okuyucu.ReadToEnd()).RootElement.Clone();
        }

        public string[] Parcalar(JsonElement manifest) =>
            [.. manifest.GetProperty("parts").EnumerateArray().Select(parca => parca.GetString() ?? string.Empty)];

        /// <summary>Var olan bir yedege elle bir girdi ekler (baska musterinin postasini taklit eder).</summary>
        public void ArsiveEkle(BackupRecordDto kayit, string girdiAdi)
        {
            using var arsiv = ZipFile.Open(kayit.Path, ZipArchiveMode.Update);
            using var yazici = new StreamWriter(arsiv.CreateEntry(girdiAdi).Open());
            yazici.Write("SAHTE SQLITE ARSIVI");
        }

        public void Dispose()
        {
            _iptal.Dispose();
            try { Directory.Delete(Kok, recursive: true); } catch { /* en iyi caba */ }
        }
    }

    private sealed class SahtePostaServisi : IMailService
    {
        public Task<MailWorkspaceDto> GetWorkspaceAsync(string? domain, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MailWorkspaceDto { Installed = true, Configured = true, SelectedDomain = domain ?? string.Empty });

        public Task<MailWorkspaceDto> ConfigureAsync(ConfigureMailRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> CreateDomainAsync(CreateMailDomainRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> DeleteDomainAsync(string domainName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> CreateAccountAsync(CreateMailAccountRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> DeleteAccountAsync(string address, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> SetAccountPasswordAsync(SetMailAccountPasswordRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> CreateAliasAsync(CreateMailAliasRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> DeleteAliasAsync(string address, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> CreateMailListAsync(CreateMailListRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> SetMailListRecipientsAsync(SetMailListRecipientsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> DeleteMailListAsync(string address, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        // Ek adres (alias) uclari artik IMailService uzerinden geciyor; bu sahte servis posta isi yapmaz.
        public Task<MailWorkspaceDto> AddMailListAliasAsync(MailListAliasRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> RemoveMailListAliasAsync(MailListAliasRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> SetAccountQuotaAsync(SetMailAccountQuotaRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> SetDomainQuotaAsync(SetMailDomainQuotaRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailAutoResponderDto> GetAutoResponderAsync(string address, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailAutoResponderDto> SetAutoResponderAsync(SetMailAutoResponderRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailDnsDto> GetDnsRecordsAsync(string domain, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailDiagnosticsDto> GetDiagnosticsAsync(string domain, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailCertificateDto> GetCertificateStatusAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailCertificateDto> IssueCertificateAsync(IssueMailCertificateRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailCertificateDto?> SyncCertificateAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> EnsureServerHostnameAsync(bool applyChanges = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailSpamSettingsDto> GetSpamSettingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailSpamSettingsDto> UpdateSpamSettingsAsync(UpdateMailSpamSettingsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<MailListenerDto>> GetListenersAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailClientSettingsDto?> GetClientSettingsAsync(string domain, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailOutboundLimitDto> GetOutboundLimitAsync(string domain, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailOutboundLimitDto> SetOutboundLimitAsync(string domain, int messagesPerHour, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailQueueDto> GetQueueAsync(int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailQueueMessageDto?> GetQueueMessageAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteQueueMessageAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailRelayDto> GetRelayAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailRelayDto> SetRelayAsync(SetMailRelayRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class SahteTercihServisi(string yedekKoku) : IPanelPreferencesService
    {
        public Task<PanelPreferencesDto> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PanelPreferencesDto { DefaultBackupRoot = yedekKoku });

        public Task<PanelPreferencesDto> UpdateAsync(UpdatePanelPreferencesRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class SahteSiteServisi(string uygulamaKoku) : IIisSiteService
    {
        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SiteDto { Name = siteName, PhysicalPath = uygulamaKoku, ApplicationRootPath = uygulamaKoku });

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

    private sealed class SahteIstemciFabrikasi : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
