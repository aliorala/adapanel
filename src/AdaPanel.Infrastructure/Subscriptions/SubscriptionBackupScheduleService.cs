using System.Text;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Interfaces;
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
                kayit.IntervalHours = Math.Clamp(request.IntervalHours, 1, 24 * 30);
                kayit.RetentionDays = Math.Clamp(request.RetentionDays, 0, 3650);
                kayit.KeepCount = Math.Clamp(request.KeepCount, 0, 1000);
                kayit.Incremental = request.Incremental;
                kayit.FullBackupEveryDays = Math.Clamp(request.FullBackupEveryDays, 1, 365);
                kayit.IncludeFiles = request.IncludeFiles;
                kayit.IncludeDatabases = request.IncludeDatabases;
                kayit.IncludeMail = request.IncludeMail;
                kayit.IncludeFtp = request.IncludeFtp;

                var hedef = (request.TargetType ?? "local").Trim().ToLowerInvariant();
                kayit.TargetType = hedef is "ftp" or "s3" or "webdav" ? hedef : "local";
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
    public async Task<SubscriptionBackupScheduleDto> RunNowAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var kayitlar = await LoadAsync(cancellationToken);
        var kayit = Bul(kayitlar, userId) ?? new ScheduleRecord { UserId = userId.ToString("N") };

        await concurrencyLock.RunExclusiveAsync(
            ConcurrencyKeys.SubscriptionBackup,
            () => CalistirAsync(userId, kayit, cancellationToken),
            cancellationToken);

        return await GetScheduleAsync(userId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task RunDueAsync(CancellationToken cancellationToken = default)
    {
        var kayitlar = await LoadAsync(cancellationToken);
        var simdi = DateTime.UtcNow;

        foreach (var kayit in kayitlar.Where(item => item.Enabled))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParse(kayit.UserId, out var userId))
            {
                continue;
            }

            // KOTA HER TURDA YENİDEN ÇÖZÜLÜR: plan sonradan daraltıldığında kaydedilmiş eski
            // aralığın kotayı sonsuza kadar delmesi kabul edilemez.
            var kota = SubscriptionBackupQuota.Resolve(kayit.IntervalHours, await MaxBackupsPerDayAsync(userId, cancellationToken));
            if (!ZamaniGeldi(kayit.LastRunUtc, kota.IntervalHours, simdi))
            {
                continue;
            }

            try
            {
                await concurrencyLock.RunExclusiveAsync(
                    ConcurrencyKeys.SubscriptionBackup,
                    () => CalistirAsync(userId, kayit, cancellationToken),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
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
    private async Task CalistirAsync(Guid userId, ScheduleRecord kayit, CancellationToken cancellationToken)
    {
        var ozet = new StringBuilder();
        var durum = "completed";

        try
        {
            var record = await backupService.BackupSubscriptionAsync(new BackupSubscriptionRequest
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
            }, cancellationToken);

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

            var uzak = string.Equals(kayit.TargetType, "local", StringComparison.OrdinalIgnoreCase)
                ? null
                : new BackupPruneRemoteOptions
                {
                    UserName = kayit.RemoteUserName,
                    Password = UzakParolaCoz(kayit),
                    Region = kayit.RemoteRegion
                };

            // Budama YALNIZCA bu aboneliğin yedeklerini kapsar ve zinciri kırmaz
            // (bkz. BackupPrunePlanner: zincir tek bir geri yükleme noktasıdır).
            var budanan = await backupService.PruneSubscriptionAsync(
                userId,
                kayit.RetentionDays,
                kayit.KeepCount,
                uzak,
                cancellationToken);
            if (budanan > 0)
            {
                ozet.Append($" {budanan} eski yedek silindi.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            durum = "failed";
            ozet.Clear();
            ozet.Append(exception.Message);
            logger.LogError(exception, "Abonelik yedegi alinamadi: {UserId}", userId);
        }

        var simdi = DateTime.UtcNow;
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

    private static bool ZamaniGeldi(DateTime? sonCalisma, int aralikSaat, DateTime simdi) =>
        sonCalisma is null || simdi >= sonCalisma.Value.AddHours(Math.Max(1, aralikSaat));

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

        var kota = SubscriptionBackupQuota.Resolve(kayit.IntervalHours, maxGunluk);

        return new SubscriptionBackupScheduleDto
        {
            UserId = userId,
            UserName = KullaniciAdi(userId),
            Enabled = kayit.Enabled,
            IntervalHours = kota.IntervalHours,
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
                ? (kayit.LastRunUtc ?? DateTime.UtcNow).AddHours(kota.IntervalHours)
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

    /// <summary>Diske serileştirilen program kaydı (ayarlar + son çalışma sonucu).</summary>
    private sealed class ScheduleRecord
    {
        /// <summary>Abonelik sahibinin kimliği ("N" biçimi).</summary>
        public string UserId { get; set; } = string.Empty;

        public bool Enabled { get; set; }

        public int IntervalHours { get; set; } = 24;

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
