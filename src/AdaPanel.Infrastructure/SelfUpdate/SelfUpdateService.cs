using AdaPanel.Core.Dtos.Update;
using AdaPanel.Core.Common;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace AdaPanel.Infrastructure.SelfUpdate;

public sealed class SelfUpdateService(
    JsonFileStore fileStore,
    IHttpClientFactory httpClientFactory,
    IUpdateInstallerLauncher installerLauncher) : ISelfUpdateService
{
    private const string ManifestPath = "config/update-manifest.json";
    private const string GithubConfigPath = "config/update-github.json";
    private const string PendingUpdatePath = "updates/pending-update.json";
    private const string DefaultRepo = "aliorala/adapanel";

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
        var github = await fileStore.ReadAsync(GithubConfigPath, new GithubConfig { Repo = DefaultRepo }, cancellationToken);
        var fetchNote = string.Empty;

        var cached = await fileStore.ReadAsync(ManifestPath, new UpdateManifest(), cancellationToken);
        var isFresh = cached.FetchedAtUtc.HasValue &&
                      DateTime.UtcNow - cached.FetchedAtUtc.Value < ManifestFreshness &&
                      !string.IsNullOrWhiteSpace(cached.LatestVersion);

        // GitHub deposu ayarlıysa en son Release'i ÇEK (sürüm + .exe indirme linki + SHA256) ve yerel manifeste
        // önbelleğe yaz → indir/kur akışı yerel manifestle aynen çalışır. Hata olursa net not (UI gösterir), tab çökmiyor.
        if (!string.IsNullOrWhiteSpace(github.Repo) && (forceRefresh || !isFresh))
        {
            try
            {
                var remote = await FetchGithubManifestAsync(github.Repo.Trim(), cancellationToken);
                if (remote is not null)
                {
                    remote.FetchedAtUtc = DateTime.UtcNow;
                    await fileStore.WriteAsync(ManifestPath, remote, cancellationToken);
                }
            }
            catch (AppException appException)
            {
                fetchNote = appException.MessageKey;
            }
            catch
            {
                fetchNote = "update.github_fetch_failed";
            }
        }

        var manifest = await fileStore.ReadAsync(ManifestPath, new UpdateManifest(), cancellationToken);
        var latestVersion = string.IsNullOrWhiteSpace(manifest.LatestVersion) ? currentVersion : manifest.LatestVersion;
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
            IsPackageReady = !string.IsNullOrWhiteSpace(pendingUpdate.PackagePath) && File.Exists(pendingUpdate.PackagePath),
            IsInstallScheduled = pendingUpdate.InstallScheduledAtUtc.HasValue,
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
        var verdict = DecideAttemptVerdict(
            attempt,
            pendingUpdate.Version,
            pendingUpdate.InstallScheduledAtUtc,
            currentVersion,
            DateTime.UtcNow);
        if (verdict == AttemptVerdict.Keep)
        {
            return attempt;
        }

        var karar = new UpdateAttemptDto
        {
            Phase = verdict == AttemptVerdict.Succeeded ? "succeeded" : "failed",
            // Cikis kodu TASINIR: calistirici olmeden once bir kod yazabildiyse teshis degeri vardir.
            InstallerExitCode = attempt?.InstallerExitCode,
            Error = string.Empty,
            ErrorKey = verdict == AttemptVerdict.Succeeded ? string.Empty : "update.attempt_no_verdict",
            IsDecidedByPanel = true,
            UpdatedAtUtc = DateTime.UtcNow
        };

        try
        {
            // Hukum KALICI yazilir: bekleyen kayit birazdan silinecek, yani ayni karari bir daha
            // veremeyecegiz. Yazamazsak (disk/izin) rapor yine dogrudur, yalniz kalici olmaz.
            await fileStore.WriteAsync(LastAttemptPath, karar, cancellationToken);
        }
        catch
        {
            // En iyi caba; durum dosyasi yalnizca bilgilendirmedir, guncelleme akisini bozmamali.
        }

        return karar;
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
        await fileStore.WriteAsync(GithubConfigPath, new GithubConfig { Repo = trimmed }, cancellationToken);
        // Depo AÇIKÇA değiştirildi: önbelleği atla, yeni deponun sürümünü hemen göster.
        return await CheckForUpdateAsync(forceRefresh: true, cancellationToken);
    }

    // GitHub Releases API'sinden en son sürümü çevirir → UpdateManifest. owner/repo (ya da github.com URL'si) kabul eder.
    // .exe asset'inin indirme linki + SHA256'sı (asset 'digest' alanı veya '<exe>.sha256' asset'i) alınır. PUBLIC repo:
    // kimlik gerektirmez (User-Agent zorunlu). SHA256 bulunamazsa güvenlik gereği net hata (.sha256 ekleyin) verir.
    private async Task<UpdateManifest?> FetchGithubManifestAsync(string repo, CancellationToken cancellationToken)
    {
        var clean = repo.Trim().Trim('/');
        var marker = clean.IndexOf("github.com/", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0)
        {
            clean = clean[(marker + "github.com/".Length)..];
        }
        var parts = clean.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            throw new AppException("update.github_repo_invalid", 400, 400, $"Repo={repo}");
        }
        var owner = parts[0];
        var name = parts[1];

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
        var url = string.IsNullOrWhiteSpace(request.DownloadUrl) ? status.DownloadUrl : request.DownloadUrl.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            return new UpdateStatusDto
            {
                CurrentVersion = status.CurrentVersion,
                LatestVersion = status.LatestVersion,
                DownloadUrl = status.DownloadUrl,
                PackageSha256 = status.PackageSha256,
                PackagePath = status.PackagePath,
                IsPackageReady = status.IsPackageReady,
                Notes = "update.package_url_missing",
                IsUpdateAvailable = status.IsUpdateAvailable,
                CheckedAtUtc = DateTime.UtcNow
            };
        }

        var packageUri = ResolvePackageUri(url);
        var expectedHash = NormalizeSha256(string.IsNullOrWhiteSpace(request.PackageSha256)
            ? status.PackageSha256
            : request.PackageSha256);
        var updatesDirectory = fileStore.EnsureDirectory("updates");
        var destination = Path.Combine(updatesDirectory, ResolvePackageFileName(packageUri));
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

        var pendingUpdate = new PendingUpdate
        {
            Version = status.LatestVersion,
            DownloadUrl = url,
            PackagePath = destination,
            PackageSha256 = actualHash,
            StagedAtUtc = DateTime.UtcNow,
            InstallScheduledAtUtc = null
        };
        await fileStore.WriteAsync(PendingUpdatePath, pendingUpdate, cancellationToken);

        return new UpdateStatusDto
        {
            CurrentVersion = status.CurrentVersion,
            LatestVersion = status.LatestVersion,
            DownloadUrl = packageUri.ToString(),
            PackageSha256 = actualHash,
            PackagePath = destination,
            IsPackageReady = true,
            IsInstallScheduled = false,
            Notes = "update.package_staged",
            IsUpdateAvailable = status.IsUpdateAvailable,
            CheckedAtUtc = DateTime.UtcNow
        };
    }

    public async Task<UpdateStatusDto> InstallPreparedUpdateAsync(CancellationToken cancellationToken = default)
    {
        var pendingUpdate = await fileStore.ReadAsync(PendingUpdatePath, new PendingUpdate(), cancellationToken);
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

        // ONCEKI denemenin durum dosyasi SILINIR. Aksi halde eski bir "failed" kaydi yeni denemenin
        // uzerine golge duser: panel, daha kurulum baslamadan gecmis bir basarisizligi bu denemenin
        // sonucu gibi raporlar. Silince kural netlesir: last-attempt.json HER ZAMAN son denemeye aittir
        // ve dosyanin YOKLUGU "calistirici henuz hicbir sey yazmadi" demektir.
        try
        {
            File.Delete(fileStore.ResolvePath(LastAttemptPath));
        }
        catch
        {
            // En iyi caba: dosya kilitliyse hukum yine surum karsilastirmasindan cikar.
        }

        await installerLauncher.ScheduleAsync(packagePath, actualHash, cancellationToken);
        pendingUpdate.InstallScheduledAtUtc = DateTime.UtcNow;
        await fileStore.WriteAsync(PendingUpdatePath, pendingUpdate, cancellationToken);

        var status = await CheckForUpdateAsync(forceRefresh: false, cancellationToken);
        status.Notes = "update.install_scheduled";
        return status;
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

    private sealed class GithubConfig
    {
        public string Repo { get; set; } = string.Empty;
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
