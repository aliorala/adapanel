using AdaPanel.Core.Dtos.Update;
using AdaPanel.Core.Common;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace AdaPanel.Infrastructure.SelfUpdate;

/// <summary>Paketin isletim sisteminden okunan kimligi: yayinci bilgisi ve Authenticode durumu.</summary>
internal sealed record UpdatePackageIdentity(
    string Company,
    string Product,
    string SignatureStatus,
    string SignatureSubject);

/// <summary>
/// Paketin kimligini isletim sisteminden okur (Win32 VersionInfo + Authenticode).
/// </summary>
/// <remarks>
/// Ayri bir temsilci, cunku bu isletim sistemine bagli TEK adimdir: karar mantigi
/// (<see cref="UpdatePackageTrust"/>) saftir ve dogrudan sinanir, okuma ise testlerde yerine
/// konabilir. Aksi halde "kimligi dogru paket kabul edilir" davranisini dogrulamak icin testin,
/// surum kaynagi ve imza tasiyan GERCEK bir PE uretmesi gerekirdi -- yani sinanamazdi.
/// </remarks>
internal delegate Task<UpdatePackageIdentity> UpdatePackageIdentityReader(
    string filePath,
    CancellationToken cancellationToken);

public sealed class SelfUpdateService : ISelfUpdateService
{
    private readonly JsonFileStore fileStore;
    private readonly IHttpClientFactory httpClientFactory;
    private readonly IUpdateInstallerLauncher installerLauncher;
    private readonly UpdatePackageIdentityReader kimlikOkuyucu;

    public SelfUpdateService(
        JsonFileStore fileStore,
        IHttpClientFactory httpClientFactory,
        IUpdateInstallerLauncher installerLauncher)
        : this(fileStore, httpClientFactory, installerLauncher, VarsayilanKimlikOkuyucu)
    {
    }

    // DI yalnizca PUBLIC kurucuyu gorur; bu asiri yukleme testler icindir ve uretimde kullanilmaz.
    internal SelfUpdateService(
        JsonFileStore fileStore,
        IHttpClientFactory httpClientFactory,
        IUpdateInstallerLauncher installerLauncher,
        UpdatePackageIdentityReader kimlikOkuyucu)
    {
        this.fileStore = fileStore;
        this.httpClientFactory = httpClientFactory;
        this.installerLauncher = installerLauncher;
        this.kimlikOkuyucu = kimlikOkuyucu;
    }

    private static async Task<UpdatePackageIdentity> VarsayilanKimlikOkuyucu(
        string filePath,
        CancellationToken cancellationToken)
    {
        var surumBilgisi = System.Diagnostics.FileVersionInfo.GetVersionInfo(filePath);
        var (imzaDurumu, imzaKonusu) = await ReadAuthenticodeAsync(filePath, cancellationToken);
        return new UpdatePackageIdentity(
            surumBilgisi.CompanyName ?? string.Empty,
            surumBilgisi.ProductName ?? string.Empty,
            imzaDurumu,
            imzaKonusu);
    }

    private const string ManifestPath = "config/update-manifest.json";
    private const string CheckStatePath = "config/update-check.json";
    private const string GithubConfigPath = "config/update-github.json";
    private const string PendingUpdatePath = "updates/pending-update.json";
    private const string TrustConfigPath = "config/update-trust.json";
    private const string DefaultRepo = "aliorala/adapanel";

    /// <summary>
    /// PANELIN KENDI hukmunun kalici kopyasi. Calistirici bu dosyayi HIC yazmaz (yazdigi dosyalar
    /// yalnizca <c>last-attempt.json</c>, <c>apply-update.log</c>, <c>update-runner.log</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// NEDEN AYRI DOSYA -- olculen K11b arizasi: panel hukmunu <c>last-attempt.json</c>'a yaziyor,
    /// hemen ardindan bekleyen kaydi siliyor; bekleyen kayit gidince <see cref="DecideAttemptVerdict"/>
    /// artik HER ZAMAN <see cref="AttemptVerdict.Keep"/> donuyor. Calistirici saniyeler sonra ayni
    /// dosyaya "failed" yazinca panelin bunu duzeltmesi KALICI OLARAK imkansiz hale geliyordu.
    /// </para>
    /// <para>
    /// Yeni calistiricinin sahiplik kurali ezmeyi zaten engelliyor -- ama calistirici betigini
    /// KURULU (eski) panel uretir. Yani bu duzeltmenin dagitildigi guncellemede ESKI calistirici
    /// kosacak ve hukmu bir kez daha ezecek. Panelin, bekleyen kayit olmadan da toparlayabilmesi
    /// icin kendine ait, calistiricinin dokunmadigi bir kanita ihtiyaci var. Kanit bu dosya.
    /// </para>
    /// </remarks>
    private const string PanelVerdictPath = "updates/panel-verdict.json";

    /// <summary>
    /// GitHub sonucunun taze sayıldığı süre. Rozet 60 saniyede bir sorduğu ve GitHub'ın kimliksiz
    /// sınırı saatte 60 istek olduğu için her çağrıda ağa çıkmak sınırı tek başına doldurur.
    /// 15 dakika, sınırı saatte ~4 isteğe indirirken yeni sürümü fark etmeyi de yeterince hızlı tutar.
    /// </summary>
    private static readonly TimeSpan ManifestFreshness = TimeSpan.FromMinutes(15);

    private const string LastAttemptPath = "updates/last-attempt.json";

    /// <summary>
    /// Çalıştırıcı son bir işaret verdikten sonra hükmün ne kadar bekleneceği. Süre dolduğunda ve
    /// sürüm HÂLÂ değişmemişse deneme "hüküm verilemedi" diye kapatılır.
    /// <para>
    /// 30 dakika ölçüme dayanır: çalıştırıcı 'installing' satırını Start-Process'ten hemen önce yazar;
    /// gözlenen kurulum (paketin açılması + dosya kopyalama + servisin geri başlaması) 35 saniye sürdü.
    /// Yavaş disk, antivirüs taraması ve şema göçü payıyla bunun ~50 katı bırakıldı. Kayıt SONSUZA
    /// KADAR "kuruluyor"da kalamaz: kullanıcı, bir daha asla sonuçlanmayacak bir ilerlemeye bakar.
    /// </para>
    /// </summary>
    internal static readonly TimeSpan AttemptVerdictGrace = TimeSpan.FromMinutes(30);

    /// <summary>Panelin kendi ölçümüyle vardığı sonuç.</summary>
    internal enum AttemptVerdict
    {
        /// <summary>Karışma: çalıştırıcının yazdığı ne ise o geçerli.</summary>
        Keep,

        /// <summary>Çalışan sürüm hedef sürüme ULAŞMIŞ: kurulum olmuştur.</summary>
        Succeeded,

        /// <summary>Sürüm değişmedi ve hüküm verecek kimse kalmadı: deneme sonuçsuz kapanır.</summary>
        NoVerdict
    }

    public async Task<UpdateStatusDto> CheckForUpdateAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var currentVersion = VersionInfo.Current;
        // The release-check gate is gone before the independent package/installer gate.
        var (github, manifest, fetchNote) = await ReadCheckedManifestAsync(forceRefresh, cancellationToken);
        var latestVersion = string.IsNullOrWhiteSpace(manifest.LatestVersion) ? currentVersion : manifest.LatestVersion;
        using var pendingLease = await SelfUpdateLifecycle.EnterAsync(fileStore.ResolvePath(PendingUpdatePath), cancellationToken);
        var pendingUpdate = await fileStore.ReadAsync(PendingUpdatePath, new PendingUpdate(), cancellationToken);

        // HUKUM BURADA VERILIR -- bekleyen kayit silinmeden ONCE, cunku hukmun tek kaniti odur.
        var lastAttempt = await ReconcileLastAttemptAsync(pendingUpdate, currentVersion, cancellationToken);

        // Bekleyen kayit ARTIK ANLAMSIZSA temizlenir; olcut IsPendingUpdateObsolete'te, hukumle
        // AYNI surum karsilastirmasindan gelir.
        if (IsPendingUpdateObsolete(
                pendingUpdate.Version,
                currentVersion,
                pendingUpdate.InstallScheduledAtUtc,
                SurecBaslangici()))
        {
            // IKI SILME AYRI DENENIR ve bu bir uslup tercihi degil, olculen bir hatanin duzeltmesi:
            // ikisi tek try icindeyken kilitli bir paket dosyasi (antivirus taramasi, Explorer'in
            // acik tuttugu ~50 MB exe) once patliyor ve KAYIT DOSYASINA HIC SIRA GELMIYORDU. Yani
            // en gurultulu artik (paket) temizlenemedigi icin en yaniltici olan (bekleyen kayit)
            // kaliyordu.
            TemizlemeyiDene(() =>
            {
                if (!string.IsNullOrWhiteSpace(pendingUpdate.PackagePath) && File.Exists(pendingUpdate.PackagePath))
                {
                    File.Delete(pendingUpdate.PackagePath);
                }
            });

            TemizlemeyiDene(() => File.Delete(fileStore.ResolvePath(PendingUpdatePath)));

            pendingUpdate = new PendingUpdate();
        }

        var defaultNote = string.IsNullOrWhiteSpace(github.Repo) ? "update.github_not_configured" : "update.github_ok";
        return new UpdateStatusDto
        {
            CurrentVersion = currentVersion,
            LatestVersion = latestVersion,
            DownloadUrl = manifest.DownloadUrl,
            PackageSha256 = manifest.PackageSha256,
            PackagePath = pendingUpdate.PackagePath,
            PreparedVersion = pendingUpdate.Version,
            PreparedPackageSha256 = pendingUpdate.PackageSha256,
            IsPackageReady = await IsPreparedPackageReadyAsync(pendingUpdate, cancellationToken),
            IsInstallScheduled = IsInstallationActive(pendingUpdate, lastAttempt),
            InstallScheduledAtUtc = pendingUpdate.InstallScheduledAtUtc,
            Notes = !string.IsNullOrEmpty(fetchNote) ? fetchNote : (string.IsNullOrWhiteSpace(manifest.Notes) ? defaultNote : manifest.Notes),
            GithubRepo = github.Repo,
            // Çekme başarısızsa sürüm bilgisi önbellekten geliyor demektir; ekran "güncelsiniz"
            // DEMEMELİ, çünkü yeni bir sürüm yayınlanmış olabilir ve panel onu görememiştir.
            IsCheckFailed = !string.IsNullOrEmpty(fetchNote),
            LastAttempt = lastAttempt,
            IsUpdateAvailable = IsNewerVersion(currentVersion, latestVersion),
            CheckedAtUtc = DateTime.UtcNow
        };
    }

    private async Task<(GithubConfig Github, UpdateManifest Manifest, string ErrorKey)> ReadCheckedManifestAsync(
        bool forceRefresh, CancellationToken cancellationToken)
    {
        using var checkLease = await SelfUpdateCheckLifecycle.EnterAsync(fileStore.ResolvePath(ManifestPath), cancellationToken);
        var github = await fileStore.ReadAsync(GithubConfigPath, new GithubConfig { Repo = DefaultRepo }, cancellationToken);
        // Validate file-backed configuration too, not only the settings endpoint.
        if (!UpdatePackageTrust.DepoIzinli(github.Repo)) github = new GithubConfig { Repo = DefaultRepo };
        var repository = UpdatePackageTrust.DepoyuCozumle(github.Repo) is { } parsed
            ? $"{parsed.Owner}/{parsed.Name}".ToLowerInvariant() : string.Empty;

        var manifest = await fileStore.ReadAsync(ManifestPath, new UpdateManifest(), cancellationToken);
        var lastCheck = await fileStore.ReadAsync(CheckStatePath, new UpdateCheckState(), cancellationToken);
        var fetchNote = string.Equals(lastCheck.Repository, repository, StringComparison.OrdinalIgnoreCase)
            ? lastCheck.ErrorKey : string.Empty;
        var now = DateTime.UtcNow;
        var isFresh = manifest.FetchedAtUtc is { } fetched && fetched <= now && now - fetched < ManifestFreshness &&
                      !string.IsNullOrWhiteSpace(manifest.LatestVersion) && TryParseVersion(manifest.LatestVersion) is not null;
        if (repository.Length != 0 && (forceRefresh || !isFresh))
        {
            try
            {
                var remote = await FetchGithubManifestAsync(github.Repo.Trim(), cancellationToken);
                remote.FetchedAtUtc = DateTime.UtcNow;
                await fileStore.WriteAsync(ManifestPath, remote, cancellationToken);
                manifest = remote;
                fetchNote = string.Empty;
            }
            catch (AppException appException) { fetchNote = appException.MessageKey; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { fetchNote = "update.github_fetch_failed"; }

            // A failed manual refresh remains visible to later scoped readers.
            // Keep the last verified manifest intact, and clear the error only on success.
            cancellationToken.ThrowIfCancellationRequested();
            await fileStore.WriteAsync(CheckStatePath, new UpdateCheckState
            {
                Repository = repository, ErrorKey = fetchNote
            }, cancellationToken);
        }
        return (github, manifest, fetchNote);
    }

    /// <summary>
    /// Kurulum çalıştırıcısının bıraktığı son durumu okur (updates/last-attempt.json).
    /// Dosya yoksa ya da okunamıyorsa null döner — bu bir hata değildir, henüz deneme olmamıştır.
    /// </summary>
    private async Task<UpdateAttemptDto?> ReadLastAttemptAsync(CancellationToken cancellationToken)
    {
        try
        {
            var path = fileStore.ResolvePath(LastAttemptPath);
            if (!File.Exists(path))
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(path, cancellationToken);
            return JsonSerializer.Deserialize<UpdateAttemptDto>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception)
        {
            // Durum dosyası okunamadıysa güncelleme akışını bozmamalı: bu yalnız bilgilendirme.
            return null;
        }
    }

    /// <summary>
    /// Kurulum denemesinin sonucunu PANELIN KENDI OLCUMUYLE karara baglar ve gerekiyorsa durum
    /// dosyasini duzeltir.
    /// <para>
    /// KOK SEBEP: hukmu yazacak olan calistirici, hukum verilecek anda oldurulen seyin ta kendisine
    /// baglidir -- kurulum panelin servisini durdurur ve calistirici cogu zaman 'installing'
    /// satirini yazdiktan sonra bir daha hicbir sey yazamaz. Canli iki makinede de gorulen tablo
    /// buydu: kurulum gunlugu "Installation process succeeded" derken durum dosyasi 'installing'de
    /// donmustu ve panel BASARILI bir guncellemeyi "tamamlanamadi" diye raporladi. Hukum, olmesi
    /// muhtemel bir surece birakilamaz; PANEL kendi CALISAN surumune bakarak karar verir.
    /// </para>
    /// </summary>
    private async Task<UpdateAttemptDto?> ReconcileLastAttemptAsync(
        PendingUpdate pendingUpdate,
        string currentVersion,
        CancellationToken cancellationToken)
    {
        var attempt = await ReadLastAttemptAsync(cancellationToken);
        var panelVerdict = await fileStore.ReadAsync(PanelVerdictPath, new PanelVerdict(), cancellationToken);
        var verdict = DecideAttemptVerdict(
            attempt,
            pendingUpdate.Version,
            pendingUpdate.InstallScheduledAtUtc,
            currentVersion,
            DateTime.UtcNow);

        if (verdict == AttemptVerdict.Keep)
        {
            // BEKLEYEN KAYIT OLMADAN TOPARLAMA (K11b). Panel daha once "basarili" dedi, calistirici
            // sonradan ayni dosyaya "basarisiz" yazdi ve bekleyen kayit silindigi icin yukaridaki
            // hukum artik devreye giremiyor. Panelin kendi kalici kanidi hala duruyorsa ve hedef
            // surum HALA calisiyorsa dogru olan yeniden yazilir.
            if (!ShouldRestorePanelVerdict(attempt, panelVerdict.Version, panelVerdict.Phase, currentVersion))
            {
                return attempt;
            }

            var toparlanan = KararOlustur(AttemptVerdict.Succeeded, attempt?.InstallerExitCode);
            await HukmuYazAsync(toparlanan, panelVerdict.Version, cancellationToken);
            return toparlanan;
        }

        var karar = KararOlustur(verdict, attempt?.InstallerExitCode);
        await HukmuYazAsync(karar, pendingUpdate.Version, cancellationToken);
        return karar;
    }

    private static UpdateAttemptDto KararOlustur(AttemptVerdict verdict, int? installerExitCode) => new()
    {
        Phase = verdict == AttemptVerdict.Succeeded ? "succeeded" : "failed",
        // Cikis kodu TASINIR: calistirici olmeden once bir kod yazabildiyse teshis degeri vardir.
        InstallerExitCode = installerExitCode,
        Error = string.Empty,
        ErrorKey = verdict == AttemptVerdict.Succeeded ? string.Empty : "update.attempt_no_verdict",
        IsDecidedByPanel = true,
        UpdatedAtUtc = DateTime.UtcNow
    };

    /// <summary>
    /// Hukum IKI YERE yazilir: kullaniciya gosterilen ortak dosyaya ve yalnizca panelin sahip
    /// oldugu <see cref="PanelVerdictPath"/> dosyasina. Ikincisi olmadan hukum, onu ezebilecek
    /// tarafla ayni dosyada yasar.
    /// </summary>
    private async Task HukmuYazAsync(UpdateAttemptDto karar, string hedefSurum, CancellationToken cancellationToken)
    {
        try
        {
            await fileStore.WriteAsync(LastAttemptPath, karar, cancellationToken);
            await fileStore.WriteAsync(
                PanelVerdictPath,
                new PanelVerdict
                {
                    Version = hedefSurum,
                    Phase = karar.Phase,
                    ErrorKey = karar.ErrorKey,
                    UpdatedAtUtc = DateTime.UtcNow
                },
                cancellationToken);
        }
        catch
        {
            // En iyi caba; durum dosyasi yalnizca bilgilendirmedir, guncelleme akisini bozmamali.
        }
    }

    /// <summary>
    /// Panelin daha once verdigi "basarili" hukmu, calistiricinin sonradan yazdigi bir sonucun
    /// altinda kalmis mi? Bekleyen kayda IHTIYAC DUYMAZ -- K11b'nin kalicilik sebebi tam olarak
    /// hukmun o kayda bagli olmasiydi.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Yalnizca "basarili" yonu toparlanir. Ters yon (panel "hukum alinamadi" demis, calistirici
    /// sonradan GERCEK sebebi yazmis) bilerek dokunulmadan birakilir: orada calistiricinin bilgisi
    /// panelinkinden fazladir ve gercek basarisizlik gizlenmemelidir.
    /// </para>
    /// <para>
    /// Hedef surum HER SEFERINDE yeniden olculur: kullanici arada eski bir surume donduyse eski
    /// "basarili" hukmu dirilmez.
    /// </para>
    /// </remarks>
    internal static bool ShouldRestorePanelVerdict(
        UpdateAttemptDto? attempt,
        string panelVerdictVersion,
        string panelVerdictPhase,
        string currentVersion)
    {
        if (!string.Equals(panelVerdictPhase, "succeeded", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!IsPendingTargetReached(panelVerdictVersion, currentVersion))
        {
            return false;
        }

        // Zaten "basarili" yaziyorsa toparlanacak bir sey yok (her kontrolde yazim olmasin).
        return attempt is null || !string.Equals(attempt.Phase, "succeeded", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Iki kaynak (calistiricinin yazdigi hukum ve panelin olctugu surum) celisirse hangisinin
    /// kazandigini belirler.
    /// <list type="bullet">
    /// <item><b>Calisan surum hedefe ulastiysa OLCUM kazanir</b> (<see cref="AttemptVerdict.Succeeded"/>):
    /// calistiricinin "basarisiz" diyebilecegi iki sey vardir -- kurulum yapilamadi ya da servis
    /// kalkmadi. Bu istegi YENI surumle yanitlayan bir panelin varligi ikisini de curutur. Iddia
    /// degil, olculen son durum esastir.</item>
    /// <item><b>Surum hedefe ulasmadiysa CALISTIRICI kazanir</b> (<see cref="AttemptVerdict.Keep"/>):
    /// gercek basarisizligin SEBEBINI (kurulum cikis kodu, gunluk kuyrugu) yalniz o bilir. Bu
    /// duzeltme gercek basarisizligi gizlememeli.</item>
    /// <item><b>Kimse hukum vermediyse ve sure dolduysa</b> (<see cref="AttemptVerdict.NoVerdict"/>):
    /// kayit "kuruluyor"da sonsuza kadar birakilamaz; sonucsuz kapatilir ve kullaniciya SEBEP
    /// uydurulmadan "hukum alinamadi" denir.</item>
    /// </list>
    /// </summary>
    internal static AttemptVerdict DecideAttemptVerdict(
        UpdateAttemptDto? attempt,
        string pendingVersion,
        DateTime? installScheduledAtUtc,
        string currentVersion,
        DateTime nowUtc)
    {
        // Kurulum HIC baslatilmadiysa ortada hukum verilecek bir deneme yok. Onceki denemeden kalan
        // sonuc (kurulumdan sonra silinen bekleyen kayit) oldugu gibi korunur.
        if (!installScheduledAtUtc.HasValue || string.IsNullOrWhiteSpace(pendingVersion))
        {
            return AttemptVerdict.Keep;
        }

        // Hedefe ULASILDI MI: olcut bekleyen kaydin temizligiyle AYNI (IsPendingTargetReached).
        if (IsPendingTargetReached(pendingVersion, currentVersion))
        {
            // Zaten 'succeeded' yaziyorsa dosyayi bos yere yeniden yazma (her kontrolde yazim olmasin).
            return attempt is not null && string.Equals(attempt.Phase, "succeeded", StringComparison.OrdinalIgnoreCase)
                ? AttemptVerdict.Keep
                : AttemptVerdict.Succeeded;
        }

        // Surum degismedi. Calistirici sonuclandirdiysa (basarili/basarisiz) SEBEBIYLE birlikte
        // onun hukmu gecerlidir.
        if (attempt is not null && attempt.IsFinished)
        {
            return AttemptVerdict.Keep;
        }

        // Kimse sonuclandirmadi. Son isaretin uzerinden ne kadar gectigine bak: hala suruyor olabilir.
        var sonIsaret = attempt?.UpdatedAtUtc ?? installScheduledAtUtc.Value;
        if (sonIsaret < installScheduledAtUtc.Value)
        {
            // Durum dosyasi ONCEKI denemeden kalmis; bu denemenin baslangicini esas al.
            sonIsaret = installScheduledAtUtc.Value;
        }

        return nowUtc - sonIsaret >= AttemptVerdictGrace ? AttemptVerdict.NoVerdict : AttemptVerdict.Keep;
    }

    /// <summary>
    /// Bekleyen paketin HEDEFINE ULASILDI MI: calisan surum, bekleyen surumden geri degil.
    /// </summary>
    /// <remarks>
    /// Esitlik YETMEZ, "daha yeni surum calisiyor" da hedefe ulasmis sayilir: kullanici arada elle
    /// daha yeni bir paket kurmus olabilir. Hem hukum (<see cref="DecideAttemptVerdict"/>) hem de
    /// bekleyen kaydin temizligi bu TEK olcutu kullanir.
    /// </remarks>
    internal static bool IsPendingTargetReached(string pendingVersion, string currentVersion)
        => !string.IsNullOrWhiteSpace(pendingVersion) && !IsNewerVersion(currentVersion, pendingVersion);

    /// <summary>
    /// Bekleyen kurulum kaydi (<c>updates/pending-update.json</c>) hala anlamli mi, yoksa artik
    /// HAYALET mi?
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Olculen olay:</b> kurulum 22:29'da BASARIYLA bitti, ama bekleyen kayit 0.5.6'yi
    /// gostermeye devam etti (22:28'den kalma) ve saatler sonra hala oradaydi; panel her acilista o
    /// bayat kaydi okuyup kullaniciya yanlis durum ("paket hazir", "kurulum planlandi") gosterdi.
    /// </para>
    /// <para>
    /// <b>Kok sebep:</b> temizlik olcutu <c>pendingVersion == currentVersion</c> ESITLIGIYDI, oysa
    /// hukum veren yer "hedefe ULASILDI MI" diye soruyordu. Iki olcut ayni govdede, on bes satir
    /// arayla farkliydi: calisan surum bekleyen surumden DAHA YENIYSE (kullanici arada 0.5.8'i
    /// kurdu) esitlik tutmaz ve kayit HIC silinmezdi. Artik ikisi de
    /// <see cref="IsPendingTargetReached"/> kullaniyor.
    /// </para>
    /// <para>
    /// <b>"Tamamlandi" ile "henuz baslamadi" ayrimi TAHMIN EDILMEZ, OLCULUR</b> ve iki olcum
    /// birlikte kullanilir:
    /// </para>
    /// <list type="number">
    /// <item><b>Surum</b> — bekleyen surum calisan surumden yeniyse is bitmemistir; kayit gercekten
    /// bekliyordur (paket indirilmis ya da kurulum suruyor) ve ASLA silinmez.</item>
    /// <item><b>Surecin baslama ani</b> — kurulum planlandi ama panel O ANDAN BERI YENIDEN
    /// BASLAMADIYSA kurulum tamamlanmis olamaz (kurulum panelin servisini durdurup yerine yenisini
    /// koyar). Bu, AYNI ya da DAHA ESKI surumu yeniden kurma durumunda sarttir: orada surum olcutu
    /// daha kurulum baslamadan "ulasildi" der ve kayit, kurulum kuyrukta beklerken silinirdi.</item>
    /// </list>
    /// <para>
    /// Hukumden daha muhafazakar olmasi bilincli: hukum bir dosyaya yazilir ve bir sonraki kontrolde
    /// duzeltilebilir, bekleyen kaydin silinmesi ise GERI ALINAMAZ (paket de silinir).
    /// </para>
    /// </remarks>
    internal static bool IsPendingUpdateObsolete(
        string pendingVersion,
        string currentVersion,
        DateTime? installScheduledAtUtc,
        DateTime processStartUtc)
    {
        if (!IsPendingTargetReached(pendingVersion, currentVersion))
        {
            return false;
        }

        // Kurulum planlandi ve panel O ANDAN BU YANA yeniden baslamadi: is daha bitmedi.
        return installScheduledAtUtc is not { } planlanma || planlanma < processStartUtc;
    }

    /// <summary>
    /// Su an calisan panel surecinin baslama ani (UTC). Kurulumun tamamlanip tamamlanmadigini
    /// olcmenin dogrudan yolu: kurulum, panelin servisini durdurup yeniden baslatir.
    /// </summary>
    private static DateTime SurecBaslangici()
    {
        try
        {
            return System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
        }
        catch
        {
            // Olculemedi. "Su an baslamis" varsayilir: en muhafazakar deger, cunku planlanmis her
            // kurulum "henuz tamamlanmadi" sayilir ve hicbir kayit yanlislikla SILINMEZ.
            return DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Artik temizligi: basarisizlik AKISI BOZMAZ ama bir sonraki islemi de ENGELLEMEZ.
    /// Her adim ayri ayri denenir (bkz. CheckForUpdateAsync icindeki gerekce).
    /// </summary>
    private static void TemizlemeyiDene(Action islem)
    {
        try
        {
            islem();
        }
        catch
        {
            // Dosya kilitliyse bir sonraki kontrolde tekrar denenir; kullanicinin gordugu durum
            // zaten bellekte duzeltilmis oluyor.
        }
    }

    public async Task<UpdateStatusDto> SetGithubRepoAsync(string repo, CancellationToken cancellationToken = default)
    {
        var trimmed = (repo ?? string.Empty).Trim();

        // KEYFI DEPO KABUL EDILMEZ. Buradan gecen deger, panelin bir sonraki adimda indirip
        // SYSTEM kimligiyle CALISTIRACAGI exe'nin kaynagidir; yani bu uc, dogrulanmadigi surece
        // "panel yoneticisi -> SYSTEM" yukselmesidir. Panel yoneticisi bir WINDOWS yoneticisi
        // degildir -- TEK istisna sunucu sahibidir (SYSTEM kabugu veren terminal ucu yalniz ona
        // acik; gerekce UpdatePackageTrust ve IServerOwnerService uzerinde yazili). Panelden
        // yaratilan bir EK yonetici hesabi icin bu yukselme gercek bir sinir asimidir.
        //
        // Not: bu ucu arayuz hic cagirmiyor (frontend'de setUpdateGithubRepo tanimli ama kullanan
        // ekran yok), yani daraltmanin kullaniciya gorunen bir maliyeti yok.
        if (!UpdatePackageTrust.DepoIzinli(trimmed))
        {
            throw new AppException("update.github_repo_invalid", 400, 400, $"Repo={trimmed}");
        }

        await fileStore.WriteAsync(GithubConfigPath, new GithubConfig { Repo = trimmed }, cancellationToken);
        // Depo AÇIKÇA değiştirildi: önbelleği atla, yeni deponun sürümünü hemen göster.
        return await CheckForUpdateAsync(forceRefresh: true, cancellationToken);
    }

    // GitHub Releases API'sinden en son sürümü çevirir → UpdateManifest. owner/repo (ya da github.com URL'si) kabul eder.
    // .exe asset'inin indirme linki + SHA256'sı (asset 'digest' alanı veya '<exe>.sha256' asset'i) alınır. PUBLIC repo:
    // kimlik gerektirmez (User-Agent zorunlu). SHA256 bulunamazsa güvenlik gereği net hata (.sha256 ekleyin) verir.
    private async Task<UpdateManifest> FetchGithubManifestAsync(string repo, CancellationToken cancellationToken)
    {
        // TEK AYRISTIRICI: izin denetimi (UpdatePackageTrust.DepoIzinli) ile burasi ayni koda bakar.
        // Ikisi ayri ayristirici olsaydi biri "gecerli" digeri "gecersiz" diyebilir ve izin denetimi
        // ayristirma farkindan atlanabilirdi.
        if (UpdatePackageTrust.DepoyuCozumle(repo) is not { } cozum)
        {
            throw new AppException("update.github_repo_invalid", 400, 400, $"Repo={repo}");
        }

        var owner = cozum.Owner;
        var name = cozum.Name;

        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(20);
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "AdaPanel");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

        using var response = await client.GetAsync($"https://api.github.com/repos/{owner}/{name}/releases/latest", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new AppException("update.github_no_release", 404, 404, $"{owner}/{name}");
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new AppException("update.github_fetch_failed", (int)response.StatusCode, 502, $"{owner}/{name} HTTP {(int)response.StatusCode}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() ?? string.Empty : string.Empty;
        var version = tag.TrimStart('v', 'V').Trim();
        if (string.IsNullOrWhiteSpace(version) || TryParseVersion(version) is null)
            throw new AppException("update.github_version_invalid", 502, 502);
        var notes = root.TryGetProperty("body", out var bodyElement) ? bodyElement.GetString() ?? string.Empty : string.Empty;

        var downloadUrl = string.Empty;
        var sha = string.Empty;
        var exeAssetName = string.Empty;
        string? shaAssetUrl = null;

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var assetName = asset.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                if (assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? string.Empty : string.Empty;
                    exeAssetName = assetName;
                    if (asset.TryGetProperty("digest", out var digestElement) && digestElement.ValueKind == JsonValueKind.String)
                    {
                        var digest = digestElement.GetString() ?? string.Empty;
                        if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        {
                            sha = digest["sha256:".Length..].Trim();
                        }
                    }
                }
            }

            // .exe var ama digest yoksa: bir SHA256 asset'i ara ('<exe>.sha256', '*.sha256', 'SHA256SUMS').
            if (!string.IsNullOrEmpty(downloadUrl) && string.IsNullOrEmpty(sha))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var assetName = asset.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                    if (assetName.Equals($"{exeAssetName}.sha256", StringComparison.OrdinalIgnoreCase) ||
                        assetName.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase) ||
                        assetName.Equals("SHA256SUMS", StringComparison.OrdinalIgnoreCase))
                    {
                        shaAssetUrl = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                        break;
                    }
                }
            }
        }

        if (string.IsNullOrEmpty(downloadUrl))
        {
            throw new AppException("update.github_no_asset", 404, 404, $"{owner}/{name}: latest release has no .exe asset");
        }

        if (string.IsNullOrEmpty(sha) && shaAssetUrl is not null)
        {
            var shaContent = await client.GetStringAsync(shaAssetUrl, cancellationToken);
            sha = ExtractSha256(shaContent);
        }

        if (string.IsNullOrEmpty(sha))
        {
            throw new AppException("update.github_no_sha", 422, 422, $"{owner}/{name}: .exe asset has no SHA256 (digest/.sha256)");
        }

        return new UpdateManifest
        {
            LatestVersion = version,
            DownloadUrl = downloadUrl,
            PackageSha256 = sha,
            Notes = notes
        };
    }

    // Metinden ilk 64 karakterlik hex SHA256 dizisini çıkarır (".sha256"/SHA256SUMS dosyaları: "hash *dosya" biçimi).
    private static string ExtractSha256(string content)
    {
        foreach (var token in content.Split([' ', '\t', '\r', '\n', '*'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length == 64 && token.All(Uri.IsHexDigit))
            {
                return token;
            }
        }
        return string.Empty;
    }

    public async Task<UpdateStatusDto> ApplyUpdateAsync(ApplyUpdateRequest request, CancellationToken cancellationToken = default)
    {
        // İndirme başlıyor: indirme linki ve SHA256 GÜNCEL olmalı, bayat manifestten indirme yapılmaz.
        var status = await CheckForUpdateAsync(forceRefresh: true, cancellationToken);
        if (status.IsInstallScheduled)
            throw new AppException("update.install_already_scheduled", 409, 409);

        // ISTEK GOVDESINDEKI adres ve ozet KULLANILMAZ; kaynak yalnizca taze cekilmis manifesttir.
        //
        // Eskiden govdeden gelen `downloadUrl` + `packageSha256` oldugu gibi kabul ediliyordu:
        // panel yoneticisi keyfi bir adresten keyfi bir exe indirtip SYSTEM olarak calistirabiliyordu
        // -- ozet denetimi de bunu YAKALAMAZ, cunku ozeti de ayni istekte veren o. Yani "SHA256
        // dogrulaniyor" cumlesi bu yolda hicbir sey ifade etmiyordu.
        //
        // Uyumluluk: arayuz zaten manifestten okudugu iki degeri aynen geri gonderiyor
        // (UpdateView.vue -> applyUpdate(status.downloadUrl, status.packageSha256)), yani gormezden
        // gelmenin davranissal etkisi yok. Alanlar sozlesmede birakildi ki eski arayuzler kirilmasin.
        var url = status.DownloadUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return new UpdateStatusDto
            {
                CurrentVersion = status.CurrentVersion,
                LatestVersion = status.LatestVersion,
                DownloadUrl = status.DownloadUrl,
                PackageSha256 = status.PackageSha256,
                PackagePath = status.PackagePath,
                PreparedVersion = status.PreparedVersion,
                PreparedPackageSha256 = status.PreparedPackageSha256,
                IsPackageReady = status.IsPackageReady,
                IsCheckFailed = status.IsCheckFailed,
                GithubRepo = status.GithubRepo,
                Notes = "update.package_url_missing",
                IsUpdateAvailable = status.IsUpdateAvailable,
                CheckedAtUtc = DateTime.UtcNow
            };
        }

        var packageUri = ResolvePackageUri(url);

        // Adres, yapilandirilmis deponun release varligina BAGLANIR. Manifest diskte duran bir
        // dosyadan da gelebilir (cekme basarisizsa onbellek kullanilir); sunucu/yol denetimi
        // olmadan o dosyaya yazabilen biri SYSTEM olarak calisacak exe'nin kaynagini belirler.
        if (!UpdatePackageTrust.IndirmeAdresiIzinli(packageUri.ToString(), status.GithubRepo))
        {
            throw new AppException("update.package_url_invalid", 400, 400, $"Url={packageUri}\r\nRepo={status.GithubRepo}");
        }

        var expectedHash = NormalizeSha256(status.PackageSha256);
        var updatesDirectory = fileStore.EnsureDirectory("updates");
        // The previous prepared package may still be approved or in use by the installer.
        // Download into an independent candidate; only publish its metadata after verification.
        var destination = Path.Combine(updatesDirectory, $"AdaPanelUpdate-{Guid.NewGuid():N}.exe");
        var published = false;
        try
        {
        var client = httpClientFactory.CreateClient();
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var response = await client.GetAsync(packageUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new AppException("update.download_not_found", 404, 404, $"Url={packageUri}");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new AppException(
                "update.download_failed",
                (int)response.StatusCode,
                502,
                $"Url={packageUri}\r\nStatusCode={(int)response.StatusCode} {response.ReasonPhrase}");
        }

        await using (var stream = File.Create(destination))
        {
            await response.Content.CopyToAsync(stream, cancellationToken);
        }

        try
        {
            await EnsureWindowsExecutableAsync(destination, cancellationToken);
        }
        catch
        {
            File.Delete(destination);
            throw;
        }

        var actualHash = await CalculateSha256Async(destination, cancellationToken);
        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(destination);
            throw new AppException("update.checksum_mismatch", 400, 400, $"Expected={expectedHash}\r\nActual={actualHash}");
        }

        // OZET TUTTUKTAN SONRA KIMLIK DENETIMI. Sirasi bilincli: ozet "aktarim sirasinda bozulma"
        // sorusunu cevaplar, "yayinlanan dosyanin kendisi bulasik mi" sorusunu CEVAPLAMAZ -- kaynakta
        // bulasma varsa yayindaki ozet de bulasik dosyanin ozetidir ve karsilastirma "temiz" der.
        // Bu depoda tam olarak bu yasandi (Synaptics PE bulastiricisi).
        try
        {
            await EnsureTrustedPackageAsync(destination, cancellationToken);
        }
        catch
        {
            File.Delete(destination);
            throw;
        }

        var pendingUpdate = new PendingUpdate
        {
            Version = status.LatestVersion,
            DownloadUrl = url,
            PackagePath = destination,
            PackageSha256 = actualHash,
            StagedAtUtc = DateTime.UtcNow,
            InstallScheduledAtUtc = null
        };
        using (await SelfUpdateLifecycle.EnterAsync(fileStore.ResolvePath(PendingUpdatePath), cancellationToken))
        {
            var previous = await fileStore.ReadAsync(PendingUpdatePath, new PendingUpdate(), cancellationToken);
            var attempt = await ReconcileLastAttemptAsync(previous, VersionInfo.Current, cancellationToken);
            if (IsInstallationActive(previous, attempt))
                throw new AppException("update.install_already_scheduled", 409, 409);
            await fileStore.WriteAsync(PendingUpdatePath, pendingUpdate, cancellationToken);
            published = true;
            await TryDeleteSupersededPackageAsync(previous, destination);
        }

        return new UpdateStatusDto
        {
            CurrentVersion = status.CurrentVersion,
            LatestVersion = status.LatestVersion,
            DownloadUrl = packageUri.ToString(),
            PackageSha256 = actualHash,
            PackagePath = destination,
            PreparedVersion = pendingUpdate.Version,
            PreparedPackageSha256 = actualHash,
            IsPackageReady = true,
            IsInstallScheduled = false,
            IsCheckFailed = status.IsCheckFailed,
            GithubRepo = status.GithubRepo,
            Notes = "update.package_staged",
            IsUpdateAvailable = status.IsUpdateAvailable,
            CheckedAtUtc = DateTime.UtcNow
        };
        }
        finally
        {
            if (!published) TemizlemeyiDene(() => File.Delete(destination));
        }
    }

    public Task<UpdateStatusDto> InstallPreparedUpdateAsync(CancellationToken cancellationToken = default) =>
        InstallPreparedUpdateCoreAsync(null, cancellationToken);

    public Task<UpdateStatusDto> InstallPreparedUpdateAsync(InstallUpdateRequest request, CancellationToken cancellationToken = default) =>
        InstallPreparedUpdateCoreAsync(new InstallUpdateRequest
        {
            ExpectedVersion = request?.ExpectedVersion ?? string.Empty,
            ExpectedPackageSha256 = request?.ExpectedPackageSha256 ?? string.Empty
        }, cancellationToken);

    private async Task<UpdateStatusDto> InstallPreparedUpdateCoreAsync(InstallUpdateRequest? request, CancellationToken cancellationToken)
    {
        using (await SelfUpdateLifecycle.EnterAsync(fileStore.ResolvePath(PendingUpdatePath), cancellationToken))
        {
        var pendingUpdate = await fileStore.ReadAsync(PendingUpdatePath, new PendingUpdate(), cancellationToken);
        if (request is not null &&
            (string.IsNullOrWhiteSpace(request.ExpectedVersion) ||
             request.ExpectedPackageSha256.Length != 64 || request.ExpectedPackageSha256.Any(character => !Uri.IsHexDigit(character)) ||
             !string.Equals(request.ExpectedVersion, pendingUpdate.Version, StringComparison.Ordinal) ||
             !string.Equals(request.ExpectedPackageSha256, pendingUpdate.PackageSha256, StringComparison.OrdinalIgnoreCase)))
            throw new AppException("update.package_changed", 409, 409);
        var attempt = await ReconcileLastAttemptAsync(pendingUpdate, VersionInfo.Current, cancellationToken);
        if (IsInstallationActive(pendingUpdate, attempt))
            throw new AppException("update.install_already_scheduled", 409, 409);
        if (string.IsNullOrWhiteSpace(pendingUpdate.PackagePath) ||
            string.IsNullOrWhiteSpace(pendingUpdate.PackageSha256) ||
            !File.Exists(pendingUpdate.PackagePath))
        {
            throw new AppException("update.package_not_ready", 409, 409);
        }

        var updatesDirectory = Path.GetFullPath(fileStore.EnsureDirectory("updates"));
        var packagePath = Path.GetFullPath(pendingUpdate.PackagePath);
        if (!packagePath.StartsWith(
                updatesDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException("update.package_not_ready", 409, 409);
        }

        await EnsureWindowsExecutableAsync(packagePath, cancellationToken);
        var expectedHash = NormalizeSha256(pendingUpdate.PackageSha256);
        var actualHash = await CalculateSha256Async(packagePath, cancellationToken);
        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException(
                "update.checksum_mismatch",
                400,
                400,
                $"Expected={expectedHash}\r\nActual={actualHash}");
        }

        // KURULUMDAN HEMEN ONCE SON KIMLIK DENETIMI. Paket indirme ile kurulum arasinda diskte
        // bekliyor; bu depoda diskteki exe'leri periyodik olarak yeniden bulastiran bir zararli
        // gorulduğu icin bu araligi denetimsiz birakmak gercek bir riski gormezden gelmek olurdu.
        // Ozet zaten yukarida yeniden hesaplandi; bu adim ozetin gormedigi soruyu sorar.
        //
        // DURUM DOSYALARINDAN ONCE calisir: denetim reddederse ortada YENI bir deneme yoktur ve
        // onceki denemenin sonucu kullanicidan silinmemelidir.
        await EnsureTrustedPackageAsync(packagePath, cancellationToken);

        // ONCEKI denemenin durum dosyasi SILINIR. Aksi halde eski bir "failed" kaydi yeni denemenin
        // uzerine golge duser: panel, daha kurulum baslamadan gecmis bir basarisizligi bu denemenin
        // sonucu gibi raporlar. Silince kural netlesir: last-attempt.json HER ZAMAN son denemeye aittir
        // ve dosyanin YOKLUGU "calistirici henuz hicbir sey yazmadi" demektir.
        TemizlemeyiDene(() => File.Delete(fileStore.ResolvePath(LastAttemptPath)));

        // PANELIN KENDI KANITI DA SILINIR. Yoksa onceki denemenin "basarili" hukmu, yeni deneme
        // daha kurulum asamasindayken toparlama yoluna girip (ShouldRestorePanelVerdict) bu
        // denemenin sonucuymus gibi gorunurdu.
        TemizlemeyiDene(() => File.Delete(fileStore.ResolvePath(PanelVerdictPath)));

        await installerLauncher.ScheduleAsync(packagePath, actualHash, cancellationToken);
        pendingUpdate.InstallScheduledAtUtc = DateTime.UtcNow;
        // Scheduling has committed. A disconnected caller must not erase that fact.
        using var publicationDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await fileStore.WriteAsync(PendingUpdatePath, pendingUpdate, publicationDeadline.Token);
        }

        var status = await CheckForUpdateAsync(forceRefresh: false, cancellationToken);
        status.Notes = "update.install_scheduled";
        return status;
    }

    private static bool IsInstallationActive(PendingUpdate pending, UpdateAttemptDto? attempt) =>
        pending.InstallScheduledAtUtc is { } scheduled &&
        !(attempt is { IsFinished: true, UpdatedAtUtc: { } finished } && finished >= scheduled &&
          (!attempt.IsDecidedByPanel || attempt.IsFailed || scheduled < SurecBaslangici()));

    private async Task TryDeleteSupersededPackageAsync(PendingUpdate previous, string publishedPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(previous.PackagePath)) return;
            var oldPath = Path.GetFullPath(previous.PackagePath);
            var updatesPath = Path.GetFullPath(fileStore.ResolvePath("updates"));
            if (!string.Equals(Path.GetDirectoryName(oldPath), updatesPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(oldPath, publishedPath, StringComparison.OrdinalIgnoreCase)) return;

            var name = Path.GetFileName(oldPath);
            var stem = Path.GetFileNameWithoutExtension(name);
            var managedCandidate = stem.StartsWith("AdaPanelUpdate-", StringComparison.OrdinalIgnoreCase) &&
                                   Guid.TryParseExact(stem["AdaPanelUpdate-".Length..], "N", out _);
            if (!string.Equals(name, "AdaPanelSetup.exe", StringComparison.OrdinalIgnoreCase) &&
                !(managedCandidate && string.Equals(Path.GetExtension(name), ".exe", StringComparison.OrdinalIgnoreCase))) return;

            // Only the previous metadata record is eligible. No directory sweep: another
            // request may be downloading a candidate while this publication owns the lease.
            for (string? current = oldPath; current is not null; current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (!await IsPreparedPackageReadyAsync(previous, deadline.Token)) return;
            File.Delete(oldPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or OperationCanceledException)
        {
            // The verified replacement is already published. Cleanup failure must not undo it.
        }
    }

    private async Task<bool> IsPreparedPackageReadyAsync(PendingUpdate pending, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(pending.Version) || string.IsNullOrWhiteSpace(pending.PackagePath) ||
            pending.PackageSha256 is not { Length: 64 } || pending.PackageSha256.Any(character => !Uri.IsHexDigit(character))) return false;
        try
        {
            var root = Path.GetFullPath(fileStore.ResolvePath("updates")) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(pending.PackagePath);
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
            await EnsureWindowsExecutableAsync(path, token);
            return string.Equals(await CalculateSha256Async(path, token), pending.PackageSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or AppException)
        {
            return false;
        }
    }

    private static async Task<string> CalculateSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static Uri ResolvePackageUri(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new AppException("update.package_url_invalid", 400, 400, $"Url={url}");
        }

        return uri;
    }

    private static string ResolvePackageFileName(Uri uri)
    {
        var fileName = Path.GetFileName(uri.LocalPath);
        return string.IsNullOrWhiteSpace(fileName)
            ? $"AdaPanelUpdate-{DateTime.UtcNow:yyyyMMddHHmmss}.exe"
            : fileName;
    }

    // Yalnizca GERCEKTEN daha yeni surumde "guncelleme var" de: esit ya da ESKI release tag'i
    // (or. panel 0.2.7 iken GitHub'da hala 0.2.6 varsa) uyari dogurmasin. "-preview"/"+hash"
    // ekleri sayisal kiyastan once atilir; sayi olarak cozulemeyen surumlerde eski esitsizlik davranisi korunur.
    internal static bool IsNewerVersion(string currentVersion, string latestVersion)
    {
        if (string.IsNullOrWhiteSpace(latestVersion))
        {
            return false;
        }

        var current = TryParseVersion(currentVersion);
        var latest = TryParseVersion(latestVersion);
        if (current is null || latest is null)
        {
            return !string.Equals(currentVersion, latestVersion, StringComparison.OrdinalIgnoreCase);
        }

        return latest > current;
    }

    private static Version? TryParseVersion(string value)
    {
        var core = value.Split('-', '+')[0].Trim();
        if (!core.Contains('.'))
        {
            core += ".0";
        }

        return Version.TryParse(core, out var parsed) ? parsed : null;
    }

    private static string NormalizeSha256(string? value)
    {
        var normalized = value?.Trim().ToUpperInvariant() ?? string.Empty;
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new AppException("update.checksum_required", 400, 400);
        }

        return normalized;
    }

    private static async Task EnsureWindowsExecutableAsync(string filePath, CancellationToken cancellationToken)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException("update.package_invalid", 400, 400);
        }

        var header = new byte[2];
        await using var stream = File.OpenRead(filePath);
        var read = await stream.ReadAsync(header, cancellationToken);
        if (read != header.Length || header[0] != (byte)'M' || header[1] != (byte)'Z')
        {
            throw new AppException("update.package_invalid", 400, 400);
        }
    }

    /// <summary>
    /// Paket SYSTEM olarak calistirilmadan once KIMLIGINI dogrular: yayinci bilgisi ve (varsa)
    /// Authenticode imzasi.
    /// </summary>
    /// <remarks>
    /// Karar mantiginin tamami <see cref="UpdatePackageTrust"/> icinde ve SAF'tir; burasi yalnizca
    /// olcumu yapip karari uygular. Sebep: bulasik bir paket bu makinede uretilemez, ama karar yine
    /// de sinanmak zorundadir.
    /// </remarks>
    private async Task EnsureTrustedPackageAsync(string filePath, CancellationToken cancellationToken)
    {
        await EnsureWindowsExecutableAsync(filePath, cancellationToken);

        var kimlik = await kimlikOkuyucu(filePath, cancellationToken);
        var kimlikKusuru = UpdatePackageTrust.PaketKimligiKusuru(kimlik.Company, kimlik.Product);
        if (kimlikKusuru is not null)
        {
            throw new AppException(
                "update.package_invalid",
                400,
                400,
                $"File={Path.GetFileName(filePath)}\r\n{kimlikKusuru}");
        }

        var guven = await fileStore.ReadAsync(TrustConfigPath, new TrustConfig(), cancellationToken);
        var imzaKusuru = UpdatePackageTrust.ImzaKusuru(kimlik.SignatureStatus, kimlik.SignatureSubject, guven.RequireSignature);
        if (imzaKusuru is not null)
        {
            throw new AppException(
                "update.package_invalid",
                400,
                400,
                $"File={Path.GetFileName(filePath)}\r\n{imzaKusuru}");
        }
    }

    /// <summary>
    /// Authenticode durumu ve imzalayan konusu. Okuma BASARISIZ olursa boş durum doner ve
    /// <see cref="UpdatePackageTrust.ImzaKusuru"/> karari verir.
    /// </summary>
    /// <remarks>
    /// <c>Get-AuthenticodeSignature</c> uzerinden okunur; ayni yontem depoda zaten kanitlanmis
    /// (<c>ComponentBootstrapper.VerifyAuthenticodeSignatureAsync</c>, bilesen msi/installer paketleri).
    /// Yonetilen kodda tam Authenticode dogrulamasi WinVerifyTrust P/Invoke gerektirir; kanitlanmis
    /// bir yol dururken ikinci ve dogrulanmamis bir yol yazmanin gerekcesi yok.
    /// </remarks>
    private static async Task<(string Status, string Subject)> ReadAuthenticodeAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        try
        {
            var kacirilmisYol = filePath.Replace("'", "''", StringComparison.Ordinal);
            var script =
                $"$imza = Get-AuthenticodeSignature -LiteralPath '{kacirilmisYol}'\r\n" +
                "[Console]::Out.Write($imza.Status.ToString())\r\n" +
                "[Console]::Out.Write('|')\r\n" +
                "if ($imza.SignerCertificate) { [Console]::Out.Write($imza.SignerCertificate.Subject) }";

            var sonuc = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
            var parcalar = sonuc.StandardOutput.Trim().Split('|', 2);
            return parcalar.Length == 2 ? (parcalar[0].Trim(), parcalar[1].Trim()) : (string.Empty, string.Empty);
        }
        catch
        {
            // Olculemedi. Bos durum doner; imza ZORUNLU ise bu reddedilir, degilse (bugunku durum)
            // gecer. "Olculemedi"yi "gecerli" saymak yok.
            return (string.Empty, string.Empty);
        }
    }

    private sealed class GithubConfig
    {
        public string Repo { get; set; } = string.Empty;
    }

    /// <summary>
    /// <c>config/update-trust.json</c>. Kod imzalama sertifikasi alindiginda tek yapilacak sey
    /// <see cref="RequireSignature"/> degerini true yapmaktir; kural zaten yazili ve sinanmis durumda.
    /// </summary>
    private sealed class TrustConfig
    {
        /// <summary>
        /// Varsayilan FALSE, cunku OLCULDU: uretilen <c>AdaPanelSetup.exe</c> imzasiz (NotSigned) ve
        /// depoda hicbir <c>signtool</c> adimi yok. True yapmak, kurulu HER paneli guncellenemez
        /// birakirdi -- yani isleyen tek guncelleme yolunu guvenlik adina kapatmak olurdu.
        /// </summary>
        public bool RequireSignature { get; set; }
    }

    /// <summary>Panelin kendi hukmunun, calistiricinin dokunmadigi kalici kopyasi.</summary>
    private sealed class PanelVerdict
    {
        /// <summary>Hukmun verildigi HEDEF surum. Toparlamada yeniden olculur.</summary>
        public string Version { get; set; } = string.Empty;

        public string Phase { get; set; } = string.Empty;

        public string ErrorKey { get; set; } = string.Empty;

        public DateTime UpdatedAtUtc { get; set; }
    }

    private sealed class UpdateManifest
    {
        public string LatestVersion { get; set; } = string.Empty;

        public string DownloadUrl { get; set; } = string.Empty;

        public string PackageSha256 { get; set; } = string.Empty;

        public string Notes { get; set; } = string.Empty;

        /// <summary>Bu manifest GitHub'dan en son NE ZAMAN alındı. Boşsa (eski kurulum) bayat sayılır.</summary>
        public DateTime? FetchedAtUtc { get; set; }
    }

    private sealed class UpdateCheckState
    {
        public string Repository { get; set; } = string.Empty;
        public string ErrorKey { get; set; } = string.Empty;
    }

    private sealed class PendingUpdate
    {
        public string Version { get; set; } = string.Empty;

        public string DownloadUrl { get; set; } = string.Empty;

        public string PackagePath { get; set; } = string.Empty;

        public string PackageSha256 { get; set; } = string.Empty;

        public DateTime StagedAtUtc { get; set; }

        public DateTime? InstallScheduledAtUtc { get; set; }
    }
}
