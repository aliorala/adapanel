using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Logs;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdaPanel.Infrastructure.Logs;

/// <summary>
/// Log döndürme: kapalı günlükleri ZSTD ile arşivler ve saklama kuralını uygular.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden gerekli:</b> panelde IIS erişim günlüklerini küçülten hiçbir şey yoktu. Otomasyondaki
/// "log temizliği" yalnız SİLİYOR, üstelik varsayılan olarak kapalı. Uzun çalışan bir sunucuda
/// <c>W3SVC&lt;id&gt;</c> klasörleri sessizce büyür ve diski dolduran şey çoğu zaman sitenin
/// kendisi değil, günlükleri olur.
/// </para>
/// <para>
/// <b>IIS'İN KENDİ DÖNDÜRMESİYLE NASIL GEÇİNİYOR — kararın özü:</b> IIS zaten günlük dosyasını
/// kendisi devrediyor (varsayılan: her gün yeni <c>u_exYYMMDD.log</c>). Panelin bunu taklit etmeye
/// çalışması (aktif dosyayı yeniden adlandırıp yenisini açmaya zorlamak) hem gereksiz hem
/// tehlikelidir: dosya <c>http.sys</c> tarafından AÇIK tutulur. Bu yüzden iş bölümü şudur —
/// <b>devretmeyi IIS yapar, devredilmiş (kapanmış) dosyalarla ne olacağını panel yapar.</b>
/// Panel hiçbir zaman aktif dosyaya dokunmaz.
/// </para>
/// <para>
/// <b>Kilit davranışı ÖLÇÜLDÜ (bu makine, canlı panel servisi + Serilog <c>shared: true</c>):</b>
/// aktif günlükte <c>FileShare.None</c> ile açma paylaşım ihlali veriyor, <c>Move</c> ve
/// <c>Delete</c> <c>IOException</c> ile reddediliyor; bir gün önceki (kapanmış) dosyada üçü de
/// serbest. Yani "en yeni dosyaya dokunma" kuralı ölçümle örtüşüyor. Ayrıca ölçüldü ki yazan taraf
/// dosyayı <c>FILE_SHARE_DELETE</c> ile açmışsa düz <c>File.Delete</c> BAŞARILI olur — bu yüzden
/// bütün silmeler <see cref="LogFileGuard"/> üzerinden yapılır.
/// </para>
/// <para>
/// <b>Sıkıştırma biçimi ödünç alınmadı, panelin TEK sıkıştırma kaynağı kullanıldı</b>
/// (<see cref="BackupDumpCompression"/>, ZSTD-3 + XXH64 sağlama). Ayrı bir sıkıştırıcı seçmek,
/// "panelde sıkıştırma kararı tek yerdedir" kuralını ikinci bir yere bölmek olurdu.
/// </para>
/// <para>
/// <b>Kazanç ÖLÇÜLDÜ (bu makinede, aynı ZSTD-3 yolundan):</b> canlı panel günlüğü 207.234 → 22.434
/// bayt (<b>9,2x</b>) ve 190.024 → 17.306 bayt (<b>11,0x</b>); 38,3 MB'lık IIS W3C erişim günlüğü
/// 38.295.931 → 1.983.163 bayt (<b>19,3x</b>, %94,8 kazanç, 1,09 sn). Erişim günlüğü yedek
/// dökümünden bile iyi sıkışıyor (orada ölçüm 4,31x idi): her satır aynı tarih biçimini, aynı
/// kullanıcı aracısını ve aynı yol kümesini tekrar eder. Yani diski dolduran şey büyük ölçüde
/// TEKRARDIR ve bu özellik onu ondalığa indirir.
/// </para>
/// <para>
/// <b>İSTATİSTİKLE ÇAKIŞMA (bilinçli):</b> <c>WebStatisticsService</c> site istatistiklerini ham
/// <c>*.log</c> dosyalarından üretir; sıkıştırılan gün istatistikten düşer. Bu yüzden site yaş
/// eşiğinin varsayılanı 30 gündür — panelin istatistik ekranının sorabildiği EN UZUN pencere.
/// </para>
/// </remarks>
public sealed class LogRotationService(
    IIisSiteService iisSiteService,
    JsonFileStore fileStore,
    IAuditService auditService,
    IOptions<PanelRuntimeOptions> runtimeOptions,
    ILogger<LogRotationService> logger) : ILogRotationService
{
    /// <summary>Politika + son tur durumu. DB göçü gerektirmez (panelin diğer ayar dosyalarıyla aynı kalıp).</summary>
    private const string StateFile = "config/log-rotation.json";

    private const string PanelTargetKey = "panel";
    private const string PanelTargetName = "AdaPanel";

    /// <summary>Ham günlük uzantısı — silme kapısına verilen izin listesi.</summary>
    private static readonly string[] LogExtension = [".log"];

    /// <summary>Arşiv uzantısı. <see cref="BackupDumpCompression.CompressedExtension"/> ile aynı olmalı.</summary>
    private static readonly string[] ArchiveExtension = [BackupDumpCompression.CompressedExtension];

    /// <summary>Arşiv adının sonu: <c>u_ex260817.log.zst</c>.</summary>
    private static readonly string ArchiveSuffix = ".log" + BackupDumpCompression.CompressedExtension;

    private readonly string dataRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(runtimeOptions.Value.DataRoot));

    public async Task<LogRotationStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var state = await ReadStateAsync(cancellationToken);
        // Kayıtlı durum ESKİ olabilir (site eklendi/silindi). Klasör ölçümleri her istekte tazelenir
        // ki ekranda "şu an diskte ne var" görünsün; tur sonuçları kayıttan gelir.
        var targets = await BuildTargetsAsync(null, state, cancellationToken);
        return Compose(state, targets);
    }

    public async Task<LogRotationStatusDto> UpdateSettingsAsync(
        UpdateLogRotationSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        var state = await fileStore.UpdateAsync(
            StateFile,
            new RotationState(),
            current =>
            {
                current.Settings = new LogRotationSettingsDto
                {
                    Enabled = request.Enabled,
                    Compress = request.Compress,
                    SiteLogMaxAgeDays = Math.Max(0, request.SiteLogMaxAgeDays),
                    PanelLogMaxAgeDays = Math.Max(0, request.PanelLogMaxAgeDays),
                    MaxFileSizeMB = Math.Max(0, request.MaxFileSizeMB),
                    SiteKeepCount = Math.Max(0, request.SiteKeepCount),
                    PanelKeepCount = Math.Max(0, request.PanelKeepCount),
                    MaxTotalSizeMB = Math.Max(0, request.MaxTotalSizeMB),
                    IntervalHours = Math.Clamp(request.IntervalHours, 1, 24 * 30)
                };
                return current;
            },
            cancellationToken);

        var targets = await BuildTargetsAsync(null, state, cancellationToken);
        return Compose(state, targets);
    }

    public async Task<LogRotationStatusDto> RunAsync(string? siteName, CancellationToken cancellationToken = default)
    {
        var state = await ReadStateAsync(cancellationToken);

        // Politika KAPALIYKEN elle çalıştırma da reddedilir. Düğmenin "kapalı" bir politikayı
        // sessizce uygulaması, ayarın ne işe yaradığını belirsiz yapardı.
        if (!state.Settings.Enabled)
        {
            throw new AppException("logs.rotation_disabled", 400, 400);
        }

        var normalSite = string.IsNullOrWhiteSpace(siteName) ? null : siteName.Trim();
        if (normalSite is not null)
        {
            // Site GERÇEKTEN var mı: yoksa IIS servisi 404 yükseltir ve uydurma bir klasör
            // üzerinde silme yapılmasının önüne geçilir.
            await iisSiteService.GetSiteAsync(normalSite, cancellationToken);
        }

        var targets = await BuildTargetsAsync(normalSite, state, cancellationToken);
        var islenmis = new List<LogRotationTargetDto>();

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            islenmis.Add(await ProcessTargetAsync(target, state.Settings, DateTime.UtcNow, cancellationToken));
        }

        var guncel = await fileStore.UpdateAsync(
            StateFile,
            new RotationState(),
            current =>
            {
                // Tek site turu, sunucu genelindeki takvimi KAYDIRMAZ.
                if (normalSite is null)
                {
                    current.LastRunUtc = DateTime.UtcNow;
                }

                current.Targets = Merge(current.Targets, islenmis);
                return current;
            },
            cancellationToken);

        await WriteAuditIfChangedAsync(islenmis, cancellationToken);

        // Tek site turundan sonra bile TÜM hedefler döner: ekran tek bir isteğin ardından
        // tutarlı bir tablo göstermeli, yarısı taze yarısı eski olmamalı.
        return Compose(guncel, await BuildTargetsAsync(null, guncel, cancellationToken));
    }

    public async Task<bool> RunDueAsync(CancellationToken cancellationToken = default)
    {
        var state = await ReadStateAsync(cancellationToken);
        if (!state.Settings.Enabled || !IsDue(state, DateTime.UtcNow))
        {
            return false;
        }

        await RunAsync(null, cancellationToken);
        return true;
    }

    public async Task<LogRotationTargetDto> GetSiteStatusAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var state = await ReadStateAsync(cancellationToken);
        var targets = await BuildTargetsAsync(siteName, state, cancellationToken);
        return targets.FirstOrDefault() ?? new LogRotationTargetDto
        {
            Key = SiteKey(siteName),
            Name = siteName,
            SiteName = siteName,
            Status = "skipped"
        };
    }

    /// <summary>Zamanı geldi mi? İlk kurulumda (hiç çalışmadıysa) evet.</summary>
    internal static bool IsDue(RotationState state, DateTime nowUtc) =>
        state.LastRunUtc is not { } son || son.AddHours(Math.Max(1, state.Settings.IntervalHours)) <= nowUtc;

    /// <summary>
    /// İşlenecek klasörlerin listesi + her birinin ŞU ANKİ disk ölçümü.
    /// </summary>
    private async Task<List<LogRotationTargetDto>> BuildTargetsAsync(
        string? siteName,
        RotationState state,
        CancellationToken cancellationToken)
    {
        var sonuc = new List<LogRotationTargetDto>();

        if (siteName is null)
        {
            sonuc.Add(Measure(PanelTargetKey, PanelTargetName, string.Empty, Path.Combine(dataRoot, "logs"), state));
        }

        IReadOnlyCollection<Core.Dtos.Sites.SiteDto> siteler;
        try
        {
            siteler = await iisSiteService.GetSitesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // IIS okunamıyorsa panel hedefi yine de çalışsın: "hepsi ya da hiçbiri" davranışı,
            // IIS'i olmayan/erişilemeyen bir makinede panel günlüklerini de yönetimsiz bırakırdı.
            logger.LogWarning(exception, "IIS site listesi okunamadi; log dondurme yalniz panel gunlukleri icin calisacak.");
            return sonuc;
        }

        foreach (var site in siteler)
        {
            if (siteName is not null && !string.Equals(site.Name, siteName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            sonuc.Add(Measure(
                SiteKey(site.Name),
                site.Name,
                site.Name,
                IisLogPaths.ResolveSiteDirectory(site.Id),
                state));
        }

        return sonuc;
    }

    /// <summary>Klasörün şu anki ölçümü + kayıtlı son tur sonucu.</summary>
    private static LogRotationTargetDto Measure(string key, string name, string siteName, string directory, RotationState state)
    {
        var onceki = state.Targets.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));
        var hedef = new LogRotationTargetDto
        {
            Key = key,
            Name = name,
            SiteName = siteName,
            Directory = directory,
            DirectoryExists = Directory.Exists(directory),
            LastRotationUtc = onceki?.LastRotationUtc,
            RotatedFiles = onceki?.RotatedFiles ?? 0,
            DeletedArchives = onceki?.DeletedArchives ?? 0,
            FreedBytes = onceki?.FreedBytes ?? 0,
            SkippedLockedFiles = onceki?.SkippedLockedFiles ?? 0,
            SkippedUnsafeFiles = onceki?.SkippedUnsafeFiles ?? 0,
            Status = onceki?.Status ?? string.Empty,
            Error = onceki?.Error ?? string.Empty
        };

        try
        {
            if (hedef.DirectoryExists)
            {
                hedef.CurrentLogBytes = ReadLogFiles(directory).Sum(f => f.SizeBytes);
                hedef.CurrentArchiveBytes = ReadArchiveFiles(directory).Sum(f => f.SizeBytes);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Ölçüm başarısızsa sıfır gösterilir; asıl tur ayrıca kendi hatasını raporlar.
        }

        return hedef;
    }

    /// <summary>
    /// TEK KLASÖRÜN turu. Sırayla: sıkıştır → kaynağı sil → saklama kuralını uygula.
    /// </summary>
    private async Task<LogRotationTargetDto> ProcessTargetAsync(
        LogRotationTargetDto target,
        LogRotationSettingsDto settings,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var sonuc = new LogRotationTargetDto
        {
            Key = target.Key,
            Name = target.Name,
            SiteName = target.SiteName,
            Directory = target.Directory,
            DirectoryExists = Directory.Exists(target.Directory),
            LastRotationUtc = nowUtc,
            Status = "ok"
        };

        if (!sonuc.DirectoryExists)
        {
            // Yeni site: IIS ilk isteği alana kadar klasörü açmaz. Hata değil.
            sonuc.Status = "skipped";
            return sonuc;
        }

        if (!LogFileGuard.IsManageableDirectory(target.Directory))
        {
            // Klasörün KENDİSİ bir kavşak/bağlantı. Takip etmiyoruz — arkasında müşterinin
            // dosyaları olabilir. Sessiz geçmiyoruz: ekranda hata olarak görünür.
            sonuc.Status = "error";
            sonuc.Error = "directory_is_reparse_point";
            logger.LogWarning(
                "Log dondurme atlandi, klasor bir baglanti noktasi: {Directory}",
                target.Directory);
            return sonuc;
        }

        try
        {
            var panelMi = IsPanel(target.Key);
            var hamGunlukler = ReadLogFiles(target.Directory);
            var yasEsigi = LogRotationPlanner.AgeThresholdFor(settings, panelMi);

            if (settings.Compress)
            {
                var plan = LogRotationPlanner.PlanRotation(hamGunlukler, yasEsigi, settings.MaxFileSizeMB, nowUtc);
                foreach (var dosya in plan.Rotate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await RotateOneAsync(target.Directory, dosya, sonuc, cancellationToken);
                }
            }

            // Saklama, sıkıştırmadan SONRAKİ gerçek üzerinde hesaplanır: bu turda üretilen arşivler
            // de sayıya girmeli, yoksa sınır bir tur geç uygulanırdı.
            var arsivler = ReadArchiveFiles(target.Directory);
            var kalanGunlukler = ReadLogFiles(target.Directory);
            var silinecekler = LogRotationPlanner.PlanRetention(
                arsivler,
                kalanGunlukler,
                LogRotationPlanner.KeepCountFor(settings, panelMi),
                settings.MaxTotalSizeMB);

            foreach (var arsiv in silinecekler)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var yol = Path.Combine(target.Directory, arsiv.Name);
                var kapi = LogFileGuard.SafeDelete(target.Directory, yol, ArchiveExtension);
                switch (kapi)
                {
                    case LogFileGuardOutcome.Done:
                        sonuc.DeletedArchives++;
                        sonuc.FreedBytes += arsiv.SizeBytes;
                        break;
                    case LogFileGuardOutcome.InUse:
                        sonuc.SkippedLockedFiles++;
                        break;
                    case LogFileGuardOutcome.Missing:
                        break;
                    default:
                        sonuc.SkippedUnsafeFiles++;
                        logger.LogWarning("Arsiv silinmedi ({Outcome}): {Path}", kapi, yol);
                        break;
                }
            }

            sonuc.CurrentLogBytes = ReadLogFiles(target.Directory).Sum(f => f.SizeBytes);
            sonuc.CurrentArchiveBytes = ReadArchiveFiles(target.Directory).Sum(f => f.SizeBytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Bir klasörün hatası DİĞER klasörleri düşürmez; hata görünür kalır.
            sonuc.Status = "error";
            sonuc.Error = exception.Message;
            logger.LogWarning(exception, "Log dondurme basarisiz: {Directory}", target.Directory);
        }

        return sonuc;
    }

    /// <summary>Tek dosyanın sıkıştırılması: geçici arşiv → doğrula → yerine koy → kaynağı sil.</summary>
    private async Task RotateOneAsync(
        string directory,
        LogFileSnapshot dosya,
        LogRotationTargetDto sonuc,
        CancellationToken cancellationToken)
    {
        var kaynak = Path.Combine(directory, dosya.Name);

        var kapi = LogFileGuard.Inspect(directory, kaynak, LogExtension);
        if (kapi != LogFileGuardOutcome.Done)
        {
            sonuc.SkippedUnsafeFiles++;
            logger.LogWarning("Gunluk dondurulmedi ({Outcome}): {Path}", kapi, kaynak);
            return;
        }

        // Sıkıştırmadan ÖNCE sorulur: sonunda kaynağı sileceğiz. Yazılmakta olan bir dosyayı
        // sıkıştırmak yarım arşiv üretir ve aslı da silinemez.
        if (LogFileGuard.IsInUse(kaynak))
        {
            sonuc.SkippedLockedFiles++;
            return;
        }

        var arsiv = kaynak + BackupDumpCompression.CompressedExtension;
        var gecici = arsiv + ".tmp";

        try
        {
            await BackupDumpCompression.CompressFileAsync(kaynak, gecici, cancellationToken);

            var geciciBilgi = new FileInfo(gecici);
            if (!geciciBilgi.Exists || geciciBilgi.Length == 0)
            {
                // Boş/oluşmamış arşivle kaynağı silmek, günlüğü yok etmek olurdu.
                sonuc.SkippedUnsafeFiles++;
                logger.LogWarning("Sikistirma bos arsiv uretti, kaynak KORUNDU: {Path}", kaynak);
                return;
            }

            File.Move(gecici, arsiv, overwrite: true);

            var silme = LogFileGuard.SafeDelete(directory, kaynak, LogExtension);
            if (silme != LogFileGuardOutcome.Done)
            {
                // Kaynak silinemedi: arşivi geri alıyoruz. Aksi hâlde aynı gün hem ham hem
                // sıkıştırılmış olarak iki kez yer kaplar ve saklama sayımı yanıltıcı olur.
                LogFileGuard.SafeDelete(directory, arsiv, ArchiveExtension);
                if (silme == LogFileGuardOutcome.InUse)
                {
                    sonuc.SkippedLockedFiles++;
                }
                else
                {
                    sonuc.SkippedUnsafeFiles++;
                }

                logger.LogWarning("Kaynak gunluk silinemedi ({Outcome}); arsiv geri alindi: {Path}", silme, kaynak);
                return;
            }

            sonuc.RotatedFiles++;
            // Kazanç = eski boyut - arşiv boyutu (arşiv diskte KALIYOR, tamamı kazanç değil).
            sonuc.FreedBytes += Math.Max(0, dosya.SizeBytes - geciciBilgi.Length);
        }
        finally
        {
            if (File.Exists(gecici))
            {
                try
                {
                    File.Delete(gecici);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Geçici dosya kalsa bile zararsız; bir sonraki tur üzerine yazar.
                }
            }
        }
    }

    /// <summary>
    /// Klasördeki ham günlükler. <b>Yalnız üst düzey</b> — özyineleme YOK, dolayısıyla bir alt
    /// klasör kavşağının içine yürümek de mümkün değil.
    /// </summary>
    private static IReadOnlyCollection<LogFileSnapshot> ReadLogFiles(string directory) =>
        Snapshot(directory, "*.log", ad => !ad.EndsWith(ArchiveSuffix, StringComparison.OrdinalIgnoreCase));

    /// <summary>Klasördeki arşivler (<c>*.log.zst</c>).</summary>
    private static IReadOnlyCollection<LogFileSnapshot> ReadArchiveFiles(string directory) =>
        Snapshot(directory, "*" + BackupDumpCompression.CompressedExtension,
            ad => ad.EndsWith(ArchiveSuffix, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyCollection<LogFileSnapshot> Snapshot(string directory, string pattern, Func<string, bool> filtre)
    {
        try
        {
            return new DirectoryInfo(directory)
                .GetFiles(pattern, SearchOption.TopDirectoryOnly)
                .Where(file => filtre(file.Name))
                // Bağlantı noktası olan dosya listeye HİÇ girmez: karar aşamasında bile hedefinin
                // boyutunu/yaşını okumuş olmayalım.
                .Where(file => !file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .Select(file => new LogFileSnapshot(file.Name, file.Length, file.LastWriteTimeUtc))
                .ToArray();
        }
        catch (Exception exception) when (exception is DirectoryNotFoundException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private async Task<RotationState> ReadStateAsync(CancellationToken cancellationToken) =>
        await fileStore.ReadAsync(StateFile, new RotationState(), cancellationToken);

    private LogRotationStatusDto Compose(RotationState state, IReadOnlyCollection<LogRotationTargetDto> targets) =>
        new()
        {
            Settings = state.Settings,
            LastRunUtc = state.LastRunUtc,
            NextRunUtc = state.LastRunUtc?.AddHours(Math.Max(1, state.Settings.IntervalHours)),
            Targets = targets,
            TotalFreedBytes = targets.Sum(t => t.FreedBytes)
        };

    private static List<LogRotationTargetDto> Merge(
        List<LogRotationTargetDto> mevcut,
        IReadOnlyCollection<LogRotationTargetDto> yeni)
    {
        var harita = mevcut.ToDictionary(t => t.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var target in yeni)
        {
            harita[target.Key] = target;
        }

        return harita.Values.OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Gerçekten bir şey silindiyse/döndürüldüyse denetim kaydı bırakır.
    /// </summary>
    /// <remarks>
    /// Zamanlanmış turda kullanıcı yoktur (<c>userId = null</c> → "system"). Kayıt yine de şart:
    /// panel gece bir dosyayı sildiyse bunun bir izi olmalı. Hiçbir şey yapılmayan turlar
    /// yazılmaz — yoksa denetim kaydı her gün anlamsız satırlarla dolardı.
    /// </remarks>
    private async Task WriteAuditIfChangedAsync(
        IReadOnlyCollection<LogRotationTargetDto> targets,
        CancellationToken cancellationToken)
    {
        var dondurulen = targets.Sum(t => t.RotatedFiles);
        var silinen = targets.Sum(t => t.DeletedArchives);
        if (dondurulen == 0 && silinen == 0)
        {
            return;
        }

        var kazanc = targets.Sum(t => t.FreedBytes);
        var detay = $"{dondurulen} log file(s) rotated, {silinen} archive(s) deleted, {kazanc / (1024 * 1024)} MB reclaimed.";

        try
        {
            await auditService.WriteAsync("logs.rotation_executed", "logs", detay, null, null, cancellationToken);
        }
        catch (Exception exception)
        {
            // Denetim kaydı ASIL İŞİN yan ürünüdür; yazılamaması dosyaları geri getirmez.
            logger.LogError(exception, "Log dondurme denetim kaydi yazilamadi.");
        }
    }

    private static bool IsPanel(string key) => string.Equals(key, PanelTargetKey, StringComparison.OrdinalIgnoreCase);

    private static string SiteKey(string siteName) => $"site:{siteName}";

    /// <summary>Diske yazılan durum. DTO'ları doğrudan taşır: ayrı bir kayıt tipi ikinci bir eşleme yükü olurdu.</summary>
    internal sealed class RotationState
    {
        public LogRotationSettingsDto Settings { get; set; } = new();

        public DateTime? LastRunUtc { get; set; }

        public List<LogRotationTargetDto> Targets { get; set; } = [];
    }
}
