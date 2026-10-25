using System.Text;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.RunningJobs;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Automation;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Subscriptions;

/// <inheritdoc />
public sealed class SubscriptionBackupScheduleService(
    JsonFileStore fileStore,
    IBackupService backupService,
    ICustomerScopeService customerScopes,
    IServicePlanService servicePlans,
    ISecretProtector secretProtector,
    IConcurrencyLock concurrencyLock,
    IRunningJobService runningJobs,
    ILogger<SubscriptionBackupScheduleService> logger,
    // İSTEĞE BAĞLI: yalnızca ekranda gösterilecek kullanıcı ADI için. Zorunlu olsaydı bu servisin
    // birim testleri bir EF bağlamı kurmak zorunda kalırdı; ad bulunamazsa boş kalır, iş yürür.
    AdaPanel.Data.Persistence.AdaPanelDbContext? dbContext = null) : ISubscriptionBackupScheduleService
{
    private const string StatePath = "config/subscription-backups.json";

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<SubscriptionBackupScheduleDto>> GetSchedulesAsync(CancellationToken cancellationToken = default)
    {
        var kayitlar = await LoadAsync(cancellationToken);
        var sonuc = new List<SubscriptionBackupScheduleDto>();
        foreach (var kayit in kayitlar)
        {
            if (Guid.TryParse(kayit.UserId, out var userId))
            {
                sonuc.Add(await ToDtoAsync(userId, kayit, cancellationToken));
            }
        }

        return sonuc
            .OrderBy(item => item.UserName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.UserId)
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<SubscriptionBackupScheduleDto> GetScheduleAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var kayitlar = await LoadAsync(cancellationToken);
        var kayit = Bul(kayitlar, userId) ?? new ScheduleRecord { UserId = userId.ToString("N") };
        return await ToDtoAsync(userId, kayit, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SubscriptionBackupScheduleDto> UpdateScheduleAsync(
        Guid userId,
        UpdateSubscriptionBackupScheduleRequest request,
        CancellationToken cancellationToken = default)
    {
        var guncel = await fileStore.UpdateAsync(
            StatePath,
            Array.Empty<ScheduleRecord>(),
            mevcut =>
            {
                var liste = mevcut.ToList();
                var kayit = Bul(liste, userId);
                if (kayit is null)
                {
                    kayit = new ScheduleRecord { UserId = userId.ToString("N") };
                    liste.Add(kayit);
                }

                kayit.Enabled = request.Enabled;

                // Program NORMALİZE edilerek saklanır; doğrulama tek yerde (BackupSchedule).
                var plan = BackupSchedule.Normalize(
                    request.ScheduleMode,
                    request.IntervalHours,
                    request.ScheduleHour,
                    request.ScheduleMinute,
                    request.ScheduleDayOfWeek,
                    request.ScheduleDayOfMonth,
                    request.ScheduleSecondHour,
                    request.ScheduleSecondMinute);
                kayit.ScheduleMode = plan.Mode;
                kayit.IntervalHours = plan.IntervalHours;
                kayit.ScheduleHour = plan.Hour;
                kayit.ScheduleMinute = plan.Minute;
                kayit.ScheduleDayOfWeek = plan.DayOfWeek;
                kayit.ScheduleDayOfMonth = plan.DayOfMonth;
                kayit.ScheduleSecondHour = plan.SecondHour;
                kayit.ScheduleSecondMinute = plan.SecondMinute;
                kayit.RetentionDays = Math.Clamp(request.RetentionDays, 0, 3650);
                kayit.KeepCount = Math.Clamp(request.KeepCount, 0, 1000);
                kayit.Incremental = request.Incremental;
                kayit.FullBackupEveryDays = Math.Clamp(request.FullBackupEveryDays, 1, 365);
                kayit.IncludeFiles = request.IncludeFiles;
                kayit.IncludeDatabases = request.IncludeDatabases;
                kayit.IncludeMail = request.IncludeMail;
                kayit.IncludeFtp = request.IncludeFtp;

                // Hedef listesi BackupService'in tek kaynağından gelir; burada elle liste tutulmaz.
                kayit.TargetType = Backup.BackupService.NormalizeScheduleTargetType(request.TargetType);
                kayit.RemoteUrl = request.RemoteUrl?.Trim() ?? string.Empty;
                kayit.RemoteUserName = request.RemoteUserName?.Trim() ?? string.Empty;
                kayit.RemoteRegion = request.RemoteRegion?.Trim() ?? string.Empty;

                if (kayit.TargetType == "local")
                {
                    kayit.RemotePasswordProtected = string.Empty;
                }
                else if (!string.IsNullOrEmpty(request.RemotePassword))
                {
                    // Parola DPAPI/DataProtection ile korunur; ekran onu geri okuyamaz. Boş
                    // gönderilmesi "değiştirme" demektir, "sil" değil.
                    kayit.RemotePasswordProtected = secretProtector.Protect(request.RemotePassword);
                }

                return liste.ToArray();
            },
            cancellationToken);

        var yeni = Bul(guncel, userId)!;
        return await ToDtoAsync(userId, yeni, cancellationToken);
    }

    /// <inheritdoc />
    public async Task RemoveScheduleAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var kimlik = userId.ToString("N");
        await fileStore.UpdateAsync(
            StatePath,
            Array.Empty<ScheduleRecord>(),
            mevcut => mevcut
                .Where(item => !string.Equals(item.UserId, kimlik, StringComparison.OrdinalIgnoreCase))
                .ToArray(),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<SubscriptionBackupScheduleDto> RunNowAsync(Guid userId, CancellationToken cancellationToken = default) =>
        RunNowCoreAsync(userId, null, cancellationToken);

    public Task<SubscriptionBackupScheduleDto> RunNowAsync(Guid userId, Func<CancellationToken, Task> beforeMutation,
        CancellationToken cancellationToken = default) => RunNowCoreAsync(userId, beforeMutation, cancellationToken);

    private async Task<SubscriptionBackupScheduleDto> RunNowCoreAsync(Guid userId,
        Func<CancellationToken, Task>? beforeMutation, CancellationToken cancellationToken)
    {
        var kayitlar = await LoadAsync(cancellationToken);
        var kayit = Bul(kayitlar, userId) ?? new ScheduleRecord { UserId = userId.ToString("N") };
        var expectedContext = beforeMutation is null ? null : ScheduleContext(Bul(kayitlar, userId));
        var approval = beforeMutation is null ? null : new BackupExecutionApproval(async token =>
        {
            await beforeMutation(token);
            if (ScheduleContext(Bul(await LoadAsync(token), userId)) != expectedContext)
                throw new AppException("backup.schedule_changed", 409, 409);
        });

        await concurrencyLock.RunExclusiveAsync(
            ConcurrencyKeys.SubscriptionBackup,
            async () =>
            {
                if (approval is not null) await approval.CheckAsync(cancellationToken);
                await CalistirAsync(userId, kayit, cancellationToken, approval, expectedContext);
            },
            cancellationToken);

        return await GetScheduleAsync(userId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task RunDueAsync(CancellationToken cancellationToken = default)
    {
        var kayitlar = await LoadAsync(cancellationToken);
        // ZAMAN BİR KEZ OKUNUR ve bütün abonelikler için aynı ana göre karar verilir. Turun
        // kendisi saatler sürebilir (elli müşteri = elli arşiv, hepsi tek kilidin içinde sırayla);
        // her abonelik için saati yeniden okusaydık, sıranın sonundakiler kendi telafi
        // penceresinin dışına düşer ve sessizce yedeksiz kalırdı.
        var simdi = DateTime.UtcNow;
        var dilim = TimeZoneInfo.Local;

        foreach (var kayit in kayitlar.Where(item => item.Enabled))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParse(kayit.UserId, out var userId))
            {
                continue;
            }

            // KOTA HER TURDA YENİDEN ÇÖZÜLÜR: plan sonradan daraltıldığında kaydedilmiş eski
            // aralığın kotayı sonsuza kadar delmesi kabul edilemez.
            var plan = await KotayaCekilmisPlanAsync(kayit, userId, cancellationToken);
            if (!BackupSchedule.ZamaniGeldi(plan.Plan, kayit.LastRunUtc, simdi, dilim))
            {
                continue;
            }

            try
            {
                // ÇALIŞAN İŞ KAYDI — ARKA PLAN TURU İÇİN DE. Elle tetiklenen tur bu kaydı
                // BackupController'da açıyordu; zamanlanmış tur açmıyordu ve bu, kapatılan hatanın
                // yarısının açık kalması demekti: gecenin üçünde başlayan iki saatlik bir yedek
                // sürerken müşteri panele girdiğinde ekran BOŞ görünüyor, "yedeğim alınmıyor" deyip
                // elle ikinci bir yedek tetikliyordu.
                //
                // SAHİP = ABONELİK (ölçüldü, tahmin edilmedi): (1) bu turun yaptığı iş
                // BackupSubscriptionAsync(userId)'dir, yani kaynağın sahibi aboneliktir;
                // (2) elle tetiklenen AYNI iş (BackupController.RunScheduleNowAsync) kaydı zaten
                // `subjectUserId: userId` ile açıyor — aynı işin sahibi tetikleyene göre değişemez;
                // (3) sahipsiz bırakılsaydı (sunucu geneli yedeklerdeki gibi) kayıt kiracı
                // süzgecinden geçemez ve müşteri KENDİ aboneliğinin yedeğini göremezdi. Etiket de
                // elle tetiklenen yolla aynı: arayüz aynı biçimden aynı adı çözüyor.
                await using var job = await runningJobs.BeginAsync(
                    RunningJobKinds.Backup, userId.ToString("N"), userId, cancellationToken);

                // The worker's authority is the configured program, not an interactive session.
                // A queued snapshot must not execute or recreate a removed/changed program.
                var expectedContext = ScheduleContext(kayit);
                var approval = new BackupExecutionApproval(async token =>
                {
                    var current = Bul(await LoadAsync(token), userId);
                    if (current is null || !current.Enabled || ScheduleContext(current) != expectedContext)
                        throw new AppException("backup.schedule_changed", 409, 409);
                });
                async Task EnsureStillDueAsync(CancellationToken token)
                {
                    // A manual run may have completed after this batch was loaded, or a plan
                    // may have changed while another subscription held the global/source gate.
                    // Keep the batch's calendar instant so a long queue cannot lose catch-up
                    // eligibility, but measure the latest run and quota before producing.
                    var current = Bul(await LoadAsync(token), userId);
                    if (current is null || !current.Enabled || ScheduleContext(current) != expectedContext)
                        throw new AppException("backup.schedule_changed", 409, 409);
                    var currentPlan = await KotayaCekilmisPlanAsync(current, userId, token);
                    token.ThrowIfCancellationRequested();
                    if (!BackupSchedule.ZamaniGeldi(currentPlan.Plan, current.LastRunUtc, simdi, dilim))
                        throw new ScheduledRunNoLongerDueException();
                }
                await concurrencyLock.RunExclusiveAsync(
                    ConcurrencyKeys.SubscriptionBackup,
                    async () =>
                    {
                        await approval.CheckAsync(cancellationToken);
                        await EnsureStillDueAsync(cancellationToken);
                        await CalistirAsync(userId, kayit, cancellationToken, approval, expectedContext,
                            EnsureStillDueAsync);
                    },
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ScheduledRunNoLongerDueException)
            {
                // No archive was produced. Preserve the preceding execution's outcome and
                // continue with the next subscription; this is not a failed backup attempt.
                logger.LogDebug("Abonelik yedek turunun zamani kuyrukta degisti: {UserId}", userId);
            }
            // SÜZGEÇ DAR TUTULUYOR: "409" tek başına yetmez, çünkü yedek yolunda başka 409'lar da
            // var (örn. kota). Yalnız çalışan-iş kaydının çakışma anahtarları ("runningjob.*")
            // erteleme sayılır; geri kalan her şey gerçek bir hatadır ve aşağıda loglanır.
            catch (AppException exception) when (
                exception.HttpStatusCode == 409 &&
                exception.MessageKey.StartsWith("runningjob.", StringComparison.Ordinal))
            {
                // BU ABONELİĞİN BAŞKA BİR YEDEK İŞİ SÜRÜYOR (ör. müşteri aynı anda elle
                // tetiklemiş). Kayıt TÜR + KİRACI başına tek yuvalıdır ve bu bilinçli: aynı yuvaya
                // ikinci bir kayıt yazmak birincinin izini siler, panel de bittiğinde hangisinin
                // bittiğini söyleyemez. Yuva kiracıyı içerdiği için KOMŞU bir aboneliğin yedeği
                // artık bu turu ertelemez — eskiden ertelerdi ve tek bir uzun yedek bütün
                // programları kilitliyordu.
                //
                // Bu abonelik SESSİZCE ATLANMIYOR ve KAYBOLMUYOR: LastRunUtc'ye dokunulmadığı için
                // program hâlâ "zamanı gelmiş" sayılır ve işçi bir dakika sonra yeniden dener.
                // Alternatif -- kaydı hiç açmamak -- daha kötüydü: iki yedek gerçekten aynı anda
                // koşabilir ve panel yalnızca birini gösterirdi.
                logger.LogWarning(
                    "Abonelik yedek turu ertelendi ({UserId}): baska bir yedek isi suruyor. "
                    + "Program kaydi guncellenmedi, bir sonraki turda (1 dk) yeniden denenecek.",
                    userId);
            }
            catch (Exception exception)
            {
                // Tek bir aboneliğin hatası TURU DÜŞÜRMEZ; diğer abonelikler yedeklenmeye devam eder.
                logger.LogWarning(exception, "Abonelik yedek turu basarisiz: {UserId}", userId);
            }
        }
    }

    /// <summary>
    /// Tek bir aboneliğin yedeğini alır, saklama politikasını uygular ve SONUCU KAYDA YAZAR.
    /// </summary>
    /// <remarks>
    /// Sonuç her hâlükârda yazılır (başarı da başarısızlık da): yalnız başarıda yazılsaydı,
    /// sürekli patlayan bir program "hiç çalışmamış" gibi görünür ve <c>LastRunUtc</c>
    /// güncellenmediği için her turda yeniden denenip sunucuyu meşgul ederdi.
    /// </remarks>
    private async Task CalistirAsync(Guid userId, ScheduleRecord kayit, CancellationToken cancellationToken,
        BackupExecutionApproval? approval = null, string? expectedContext = null,
        Func<CancellationToken, Task>? beforeScheduledBackup = null)
    {
        var ozet = new StringBuilder();
        var durum = "completed";
        var prePruned = new BackupPruneResultDto();

        try
        {
            var request = new BackupSubscriptionRequest
            {
                UserId = userId,
                IncludeFiles = kayit.IncludeFiles,
                IncludeDatabases = kayit.IncludeDatabases,
                IncludeMail = kayit.IncludeMail,
                IncludeFtp = kayit.IncludeFtp,
                Incremental = kayit.Incremental,
                FullBackupEveryDays = kayit.FullBackupEveryDays,
                TargetType = kayit.TargetType,
                RemoteUrl = kayit.RemoteUrl,
                RemoteUserName = kayit.RemoteUserName,
                RemotePassword = UzakParolaCoz(kayit),
                RemoteRegion = kayit.RemoteRegion
            };
            async Task BeforeProductionAsync(CancellationToken token)
            {
                if (approval is not null) await approval.CheckAsync(token);
                if (beforeScheduledBackup is not null)
                {
                    await beforeScheduledBackup(token);
                    // Quota resolution itself awaits JSON reads. Recheck the configured
                    // program before admitting the producer after that wait too.
                    if (approval is not null) await approval.CheckAsync(token);
                }
            }
            async Task<BackupPruneResultDto> PrunePhaseAsync(bool beforeProduction)
            {
                Func<CancellationToken, Task>? recheck = beforeProduction
                    ? BeforeProductionAsync
                    : approval is null ? null : approval.CheckAsync;
                if (recheck is not null) await recheck(cancellationToken);
                var remote = string.Equals(kayit.TargetType, "local", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : new BackupPruneRemoteOptions
                    {
                        RemoteUrl = kayit.RemoteUrl,
                        UserName = kayit.RemoteUserName,
                        Password = UzakParolaCoz(kayit),
                        Region = kayit.RemoteRegion
                    };
                return recheck is null
                    ? await backupService.PruneSubscriptionAsync(userId, kayit.RetentionDays, kayit.KeepCount, remote, cancellationToken)
                    : await backupService.PruneSubscriptionAsync(userId, kayit.RetentionDays, kayit.KeepCount, remote, recheck, cancellationToken);
            }

            try
            {
                prePruned = await PrunePhaseAsync(beforeProduction: true);
            }
            catch (Exception exception) when (exception is not OperationCanceledException &&
                exception is not ScheduledRunNoLongerDueException && approval?.Rejected(exception) != true)
            {
                durum = "completed_with_errors";
                ozet.Append($"pre-prune: {exception.Message}. ");
                logger.LogWarning(exception, "Abonelik yedegi oncesi budama basarisiz: {UserId}", userId);
            }

            var record = approval is null && beforeScheduledBackup is null
                ? await backupService.BackupSubscriptionAsync(request, cancellationToken)
                : await backupService.BackupSubscriptionAsync(request, BeforeProductionAsync, cancellationToken);

            var mod = string.Equals(record.BackupMode, "incremental", StringComparison.OrdinalIgnoreCase)
                ? $"artimsal (zincir #{record.ChainIndex})"
                : "tam";
            ozet.Append($"{record.ItemCount} oge, {mod} yedek: {record.Name} ({record.Size / 1024 / 1024} MB).");

            // KISMI BAŞARI DÜRÜST RAPORLANIR: arşive giremeyen her öğe burada görünür.
            // Metin TEK KAYNAKTAN gelir (SubscriptionBackupPlanner.OzetMetni): burada ikinci bir
            // kopya yazılsaydı, biri düzeltilip diğeri unutulduğunda ekranların biri kısmi
            // başarısızlığı söylemeye devam eder, diğeri sessizce "tamam" derdi.
            if (record.Warnings.Count > 0)
            {
                durum = "completed_with_errors";
                ozet.Append(' ');
                ozet.Append(SubscriptionBackupPlanner.OzetMetni(
                    record.ItemCount,
                    Math.Max(0, record.ItemCount - record.Warnings.Count),
                    record.Warnings));
            }

            // Budama YALNIZCA bu aboneliğin yedeklerini kapsar ve zinciri kırmaz
            // (bkz. BackupPrunePlanner: zincir tek bir geri yükleme noktasıdır).
            var postPruned = await PrunePhaseAsync(beforeProduction: false);
            var budanan = new BackupPruneResultDto
            {
                Removed = prePruned.Removed + postPruned.Removed,
                LocalDeleteFailed = prePruned.LocalDeleteFailed + postPruned.LocalDeleteFailed,
                RemoteDeleteFailed = prePruned.RemoteDeleteFailed + postPruned.RemoteDeleteFailed,
                Failures = [.. prePruned.Failures, .. postPruned.Failures],
                RetainedExpired = postPruned.RetainedExpired,
                RetentionNotes = [.. postPruned.RetentionNotes]
            };
            if (budanan.Removed > 0)
            {
                ozet.Append($" {budanan.Removed} eski yedek silindi.");
            }
            if (budanan.RetainedExpired > 0)
            {
                ozet.Append($" {budanan.RetainedExpired} suresi dolmus yedek geri yukleme korumasi nedeniyle tutuldu.");
                foreach (var note in budanan.RetentionNotes)
                    ozet.Append($" retention: {note}.");
            }

            // BUDAMA BAŞARISIZLIĞI DURUMU DEĞİŞTİRİR: aynı hata sınıfı gece otomasyonunda ölçüldü
            // (panel "Tamamlandı" derken dosyalar FTP'de duruyordu). Uzak kopyası silinemeyen
            // kayıt hedefte YETİM kalır; bunu söylemeyen bir özet, kullanıcının panele bakıp
            // "temiz" sanmasının tek sebebidir. Serbest metin dilden bağımsız teknik biçimdedir.
            if (budanan.HasFailures)
            {
                durum = "completed_with_errors";
                ozet.Append(
                    $" prune (basarisiz silme denemeleri): remote_delete_failed={budanan.RemoteDeleteFailed}; "
                    + $"local_delete_failed={budanan.LocalDeleteFailed}.");
                foreach (var basarisiz in budanan.Failures)
                {
                    ozet.Append($" prune: {basarisiz}.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not ScheduledRunNoLongerDueException && approval?.Rejected(exception) != true)
        {
            durum = "failed";
            if (ozet.Length > 0) ozet.Append(' ');
            ozet.Append(exception.Message);
            var preSummary = AutomationService.SummarizePrune(prePruned);
            ozet.Append($" pre-prune: {preSummary.Sentence}.");
            foreach (var line in preSummary.TechnicalLines) ozet.Append($" {line}.");
            logger.LogError(exception, "Abonelik yedegi alinamadi: {UserId}", userId);
        }

        var simdi = DateTime.UtcNow;
        if (approval is not null) await approval.CheckAsync(cancellationToken);
        var detay = ozet.ToString();
        kayit.LastRunUtc = simdi;
        kayit.LastStatus = durum;
        kayit.LastDetails = detay;

        // ATOMİK yazım: aynı anda başka bir aboneliğin turu da sonucunu yazıyor olabilir.
        var kimlik = userId.ToString("N");
        await fileStore.UpdateAsync(
            StatePath,
            Array.Empty<ScheduleRecord>(),
            mevcut =>
            {
                var liste = mevcut.ToList();
                var hedef = liste.FirstOrDefault(item => string.Equals(item.UserId, kimlik, StringComparison.OrdinalIgnoreCase));
                // Compare inside the JSON update too: a program removed after the last
                // asynchronous check must not be resurrected by this older execution.
                if (expectedContext is not null && ScheduleContext(hedef) != expectedContext)
                    throw new AppException("backup.schedule_changed", 409, 409);
                if (hedef is null)
                {
                    // "Şimdi çalıştır" kaydı olmayan bir abonelikte de çalışabilir; sonucu kaybetmeyiz.
                    kayit.UserId = kimlik;
                    liste.Add(kayit);
                }
                else
                {
                    hedef.LastRunUtc = simdi;
                    hedef.LastStatus = durum;
                    hedef.LastDetails = detay;
                }

                return liste.ToArray();
            },
            cancellationToken);
    }

    /// <summary>Kotaya çekilmiş program + kotanın devreye girip girmediği.</summary>
    private sealed record KotaliPlan(BackupSchedulePlan Plan, bool Throttled, int MaxBackupsPerDay);

    private sealed class ScheduledRunNoLongerDueException : Exception { }

    /// <summary>
    /// Kayıttaki programı plan kotasına çeker.
    /// </summary>
    private async Task<KotaliPlan> KotayaCekilmisPlanAsync(ScheduleRecord kayit, Guid userId, CancellationToken cancellationToken)
    {
        var maxGunluk = await MaxBackupsPerDayAsync(userId, cancellationToken);
        return KotayaCek(kayit, maxGunluk);
    }

    /// <summary>Kota daraltmasının SAF hâli (test edilebilir olması için ayrıldı).</summary>
    internal static KotaliPlanSonucu KotayaCek(
        string? mode,
        int intervalHours,
        int hour,
        int minute,
        int dayOfWeek,
        int dayOfMonth,
        int maxBackupsPerDay,
        int? secondHour = null,
        int secondMinute = 0)
    {
        var plan = BackupSchedule.Normalize(mode, intervalHours, hour, minute, dayOfWeek, dayOfMonth, secondHour, secondMinute);
        if (plan.HasSecondTime && maxBackupsPerDay == 1)
            return new KotaliPlanSonucu(plan with { SecondHour = null }, true);
        if (plan.Takvimli)
        {
            return new KotaliPlanSonucu(plan, false);
        }

        var kota = SubscriptionBackupQuota.Resolve(plan.IntervalHours, maxBackupsPerDay);
        return new KotaliPlanSonucu(plan with { IntervalHours = kota.IntervalHours }, kota.Throttled);
    }

    /// <summary>Kota daraltmasının sonucu (saf hesabın çıktısı).</summary>
    internal sealed record KotaliPlanSonucu(BackupSchedulePlan Plan, bool Throttled);

    private static KotaliPlan KotayaCek(ScheduleRecord kayit, int maxBackupsPerDay)
    {
        var sonuc = KotayaCek(
            kayit.ScheduleMode,
            kayit.IntervalHours,
            kayit.ScheduleHour,
            kayit.ScheduleMinute,
            kayit.ScheduleDayOfWeek,
            kayit.ScheduleDayOfMonth,
            maxBackupsPerDay,
            kayit.ScheduleSecondHour,
            kayit.ScheduleSecondMinute);
        return new KotaliPlan(sonuc.Plan, sonuc.Throttled, maxBackupsPerDay);
    }

    private async Task<int> MaxBackupsPerDayAsync(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            var scope = await customerScopes.GetScopeAsync(userId, cancellationToken);
            if (string.IsNullOrWhiteSpace(scope.PlanId))
            {
                return 0;
            }

            var plan = await servicePlans.GetPlanAsync(scope.PlanId, cancellationToken);
            return plan?.MaxBackupsPerDay ?? 0;
        }
        catch (Exception exception)
        {
            // Kota ÖLÇÜLEMİYORSA sınırsız sayılır: ölçemediğimiz bir sınır yüzünden müşterinin
            // yedeğini hiç almamak, kotayı korumaktan çok daha pahalıdır.
            logger.LogWarning(exception, "Abonelik yedek kotasi okunamadi: {UserId}", userId);
            return 0;
        }
    }

    private async Task<SubscriptionBackupScheduleDto> ToDtoAsync(Guid userId, ScheduleRecord kayit, CancellationToken cancellationToken)
    {
        var planId = string.Empty;
        var planAdi = string.Empty;
        var maxGunluk = 0;

        try
        {
            var scope = await customerScopes.GetScopeAsync(userId, cancellationToken);
            planId = scope.PlanId;
            planAdi = scope.PlanName;
            if (!string.IsNullOrWhiteSpace(planId))
            {
                maxGunluk = (await servicePlans.GetPlanAsync(planId, cancellationToken))?.MaxBackupsPerDay ?? 0;
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Abonelik kapsami okunamadi: {UserId}", userId);
        }

        var kota = KotayaCek(kayit, maxGunluk);

        return new SubscriptionBackupScheduleDto
        {
            UserId = userId,
            UserName = KullaniciAdi(userId),
            Enabled = kayit.Enabled,
            ScheduleMode = kota.Plan.Mode,
            IntervalHours = kota.Plan.IntervalHours,
            ScheduleHour = kota.Plan.Hour,
            ScheduleMinute = kota.Plan.Minute,
            ScheduleDayOfWeek = kota.Plan.DayOfWeek,
            ScheduleDayOfMonth = kota.Plan.DayOfMonth,
            ScheduleSecondHour = kota.Plan.SecondHour,
            ScheduleSecondMinute = kota.Plan.SecondMinute,
            RetentionDays = kayit.RetentionDays,
            KeepCount = kayit.KeepCount,
            Incremental = kayit.Incremental,
            FullBackupEveryDays = kayit.FullBackupEveryDays,
            IncludeFiles = kayit.IncludeFiles,
            IncludeDatabases = kayit.IncludeDatabases,
            IncludeMail = kayit.IncludeMail,
            IncludeFtp = kayit.IncludeFtp,
            TargetType = kayit.TargetType,
            RemoteUrl = kayit.RemoteUrl,
            RemoteUserName = kayit.RemoteUserName,
            RemoteRegion = kayit.RemoteRegion,
            RemotePasswordSet = !string.IsNullOrWhiteSpace(kayit.RemotePasswordProtected),
            LastRunUtc = kayit.LastRunUtc,
            LastStatus = kayit.LastStatus,
            LastDetails = kayit.LastDetails,
            NextRunUtc = kayit.Enabled
                ? BackupSchedule.SonrakiZaman(kota.Plan, kayit.LastRunUtc, DateTime.UtcNow, TimeZoneInfo.Local)
                : null,
            PlanId = planId,
            PlanName = planAdi,
            MaxBackupsPerDay = maxGunluk,
            ThrottledByQuota = kota.Throttled
        };
    }

    private string KullaniciAdi(Guid userId)
    {
        if (dbContext is null)
        {
            return string.Empty;
        }

        try
        {
            return dbContext.Users.FirstOrDefault(item => item.Id == userId)?.UserName ?? string.Empty;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Kullanici adi okunamadi: {UserId}", userId);
            return string.Empty;
        }
    }

    private string UzakParolaCoz(ScheduleRecord kayit)
    {
        if (string.IsNullOrWhiteSpace(kayit.RemotePasswordProtected))
        {
            return string.Empty;
        }

        try
        {
            return secretProtector.Unprotect(kayit.RemotePasswordProtected);
        }
        catch
        {
            logger.LogWarning("Abonelik uzak yedek parolasi cozulemedi; uzak yukleme kimliksiz denenecek.");
            return string.Empty;
        }
    }

    private async Task<List<ScheduleRecord>> LoadAsync(CancellationToken cancellationToken) =>
        (await fileStore.ReadAsync(StatePath, Array.Empty<ScheduleRecord>(), cancellationToken)).ToList();

    private static ScheduleRecord? Bul(IEnumerable<ScheduleRecord> kayitlar, Guid userId)
    {
        var kimlik = userId.ToString("N");
        return kayitlar.FirstOrDefault(item => string.Equals(item.UserId, kimlik, StringComparison.OrdinalIgnoreCase));
    }

    private static string ScheduleContext(ScheduleRecord? record) => record is null ? "missing" :
        System.Text.Json.JsonSerializer.Serialize(new
        {
            record.UserId, record.Enabled, record.ScheduleMode, record.IntervalHours, record.ScheduleHour,
            record.ScheduleMinute, record.ScheduleDayOfWeek, record.ScheduleDayOfMonth,
            record.ScheduleSecondHour, record.ScheduleSecondMinute, record.RetentionDays, record.KeepCount,
            record.Incremental, record.FullBackupEveryDays, record.IncludeFiles, record.IncludeDatabases,
            record.IncludeMail, record.IncludeFtp, record.TargetType, record.RemoteUrl, record.RemoteUserName,
            record.RemoteRegion, record.RemotePasswordProtected
        });

    /// <summary>Diske serileştirilen program kaydı (ayarlar + son çalışma sonucu).</summary>
    private sealed class ScheduleRecord
    {
        /// <summary>Abonelik sahibinin kimliği ("N" biçimi).</summary>
        public string UserId { get; set; } = string.Empty;

        public bool Enabled { get; set; }

        // GERİYE DÖNÜK UYUM: eski subscription-backups.json dosyalarında bu alan yoktur ve
        // "interval" olarak okunur — mevcut abonelik programları hiç değişmeden sürer.
        public string ScheduleMode { get; set; } = BackupSchedule.Interval;

        public int IntervalHours { get; set; } = 24;

        public int ScheduleHour { get; set; } = 3;

        public int ScheduleMinute { get; set; }

        public int ScheduleDayOfWeek { get; set; } = 1;

        public int ScheduleDayOfMonth { get; set; } = 1;

        /// <summary>İsteğe bağlı ikinci günlük saat; null = kapalı. Sunucunun yerel saati.</summary>
        public int? ScheduleSecondHour { get; set; }

        public int ScheduleSecondMinute { get; set; }

        public int RetentionDays { get; set; } = 14;

        public int KeepCount { get; set; }

        public bool Incremental { get; set; }

        public int FullBackupEveryDays { get; set; } = 7;

        public bool IncludeFiles { get; set; } = true;

        public bool IncludeDatabases { get; set; } = true;

        public bool IncludeMail { get; set; } = true;

        public bool IncludeFtp { get; set; } = true;

        public string TargetType { get; set; } = "local";

        public string RemoteUrl { get; set; } = string.Empty;

        public string RemoteUserName { get; set; } = string.Empty;

        public string RemoteRegion { get; set; } = string.Empty;

        public string RemotePasswordProtected { get; set; } = string.Empty;

        public DateTime? LastRunUtc { get; set; }

        public string LastStatus { get; set; } = string.Empty;

        public string LastDetails { get; set; } = string.Empty;
    }
}
