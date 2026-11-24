using AdaPanel.Core.Common;
using AdaPanel.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Subscriptions;

/// <summary>Bir askı/açma turunun sonucu: site cümlesi için başarısız siteler + diğer kaynakların cümlesi.</summary>
/// <param name="SitesRequested">İşlenen site sayısı.</param>
/// <param name="SitesFailed">Kapatılamayan/açılamayan siteler (ADIYLA).</param>
/// <param name="Extras">FTP/cron/posta/veritabanı için okunur cümle; söyleyecek bir şey yoksa boş.</param>
/// <param name="HasPending">Yeniden denenecek (kapatılamayan ya da açılamayan) kaynak var mı.</param>
public sealed record SuspensionReport(int SitesRequested, IReadOnlyList<string> SitesFailed, string Extras, bool HasPending);

/// <summary>
/// M2: bir aboneliğin kaynaklarını (IIS siteleri, FTP hesapları, zamanlanmış görevler) askıda KAPATIR ve
/// açılırken YALNIZ askıdan önce açık olanları geri açar. Panelin kapatamadığı kaynak türleri
/// (posta, veritabanı kullanıcıları) <see cref="SubscriptionSuspensionState.Unsupported"/> olarak
/// kaydedilir ve dikkat öğesi olarak gösterilir; "kapattım" denmez.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden ayrı sınıf:</b> aynı karar iki yoldan verilir — otomatik zorlama
/// (<see cref="SubscriptionEnforcementService"/>) ve yöneticinin müşteriyi elle pasife aldığı yol
/// (<c>UserManagementService</c>). Mantık ikiye kopyalanırsa biri geride kalır (bu depoda ölçülen kalıp).
/// </para>
/// <para>
/// <b>Önceki hal:</b> askıdan ÖNCE açık olan her kaynak <see cref="SubscriptionSuspensionState.OpenBefore"/>'a
/// yazılır; müşterinin elle durdurduğu site/hesap/görev oraya girmez ve açılışta başlatılmaz.
/// Askı tekrar çağrılırsa (yeniden deneme, sebep değişimi) liste ASLA küçülmez: kapattığımız bir site
/// artık "kapalı" göründüğü için önceki halini unutmak, açılışta onu açık bırakmamıza yol açardı.
/// </para>
/// <para>
/// <b>Başarısızlık sessiz değil:</b> kapatılamayan/açılamayan her kaynak <c>PendingClose</c>/<c>PendingRestore</c>'a
/// yazılır; zorlama turu bunları her seferinde yeniden dener ve dikkat akışı (AttentionService) uyarı üretir.
/// </para>
/// </remarks>
public sealed class SubscriptionResourceSuspender(
    ICustomerScopeService customerScopes,
    IIisSiteService iisSiteService,
    ILogger<SubscriptionResourceSuspender> logger,
    IFtpService? ftpService = null,
    ISchedulerService? schedulerService = null)
{
    internal const string SitePrefix = "site:";
    internal const string FtpPrefix = "ftp:";
    internal const string CronPrefix = "cron:";
    private const string FtpListKey = "ftp:*";

    /// <summary>Abonelik kimliğine bağlı cron görevi öneki (CronController.GetOwnerPrefixAsync ile AYNI biçim).</summary>
    internal static string CronOwnerPrefix(Guid userId) => $"u{userId:N}--";

    /// <summary>Kaynakları kapatır (idempotent: tekrar çağrılabilir, önceki hal korunur).</summary>
    public async Task<SuspensionReport> CloseAsync(Guid userId, IReadOnlyCollection<string> sites, CancellationToken cancellationToken)
    {
        var state = await customerScopes.GetSuspensionStateAsync(userId, cancellationToken) ?? new SubscriptionSuspensionState();
        var failedSites = new List<string>();
        var ftpClosed = 0;
        var cronClosed = 0;

        // 1) Siteler.
        foreach (var site in sites)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = SitePrefix + site;
            var open = true; // ölçülemezse AÇIK say: durdurmayı dene (güvenli taraf)
            try
            {
                var current = await iisSiteService.GetSiteAsync(site, cancellationToken);
                // Yalnız AÇIKÇA "Stopped" kapalı sayılır; Started/Starting/Unknown/boş → durdurmayı dene.
                open = !string.Equals(current.State, "Stopped", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Askı: site durumu okunamadı, açık sayılıyor: {Site}", site);
            }

            if (!open)
            {
                state.PendingClose.Remove(key); // zaten kapalı (elle ya da önceki turda)
                continue;
            }

            AddOnce(state.OpenBefore, key);
            try
            {
                await iisSiteService.StopSiteAsync(site, cancellationToken);
                state.PendingClose.Remove(key);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failedSites.Add(site);
                AddOnce(state.PendingClose, key);
                logger.LogWarning(exception, "Askıya alma sırasında site durdurulamadı: {Site}", site);
            }
        }

        // 2) FTP hesapları (sitelere bağlı).
        if (ftpService is null)
        {
            AddOnce(state.Unsupported, "ftp");
        }
        else
        {
            try
            {
                var siteSet = new HashSet<string>(sites, StringComparer.OrdinalIgnoreCase);
                foreach (var account in (await ftpService.GetFtpUsersAsync(cancellationToken)).Where(item => siteSet.Contains(item.SiteName)))
                {
                    var key = FtpPrefix + account.UserName;
                    if (!account.IsEnabled)
                    {
                        state.PendingClose.Remove(key);
                        continue;
                    }

                    AddOnce(state.OpenBefore, key);
                    try
                    {
                        await ftpService.SetFtpUserEnabledAsync(account.UserName, false, cancellationToken);
                        state.PendingClose.Remove(key);
                        ftpClosed++;
                    }
                    catch (NotSupportedException)
                    {
                        state.OpenBefore.Remove(key);
                        AddOnce(state.Unsupported, "ftp");
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        AddOnce(state.PendingClose, key);
                        logger.LogWarning(exception, "Askı: FTP hesabı kapatılamadı: {Account}", account.UserName);
                    }
                }

                state.PendingClose.Remove(FtpListKey);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                AddOnce(state.PendingClose, FtpListKey);
                logger.LogWarning(exception, "Askı: FTP hesapları listelenemedi.");
            }
        }

        // 3) Zamanlanmış görevler (abonelik önekli).
        if (schedulerService is null)
        {
            AddOnce(state.Unsupported, "cron");
        }
        else
        {
            try
            {
                var prefix = CronOwnerPrefix(userId);
                foreach (var task in (await schedulerService.GetTasksAsync(cancellationToken))
                             .Where(item => item.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    var key = CronPrefix + task.Name;
                    if (string.Equals(task.State, "Disabled", StringComparison.OrdinalIgnoreCase))
                    {
                        state.PendingClose.Remove(key);
                        continue;
                    }

                    AddOnce(state.OpenBefore, key);
                    try
                    {
                        await schedulerService.SetTaskEnabledAsync(task.Name, false, cancellationToken);
                        state.PendingClose.Remove(key);
                        cronClosed++;
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        AddOnce(state.PendingClose, key);
                        logger.LogWarning(exception, "Askı: görev kapatılamadı: {Task}", task.Name);
                    }
                }

                state.PendingClose.Remove("cron:*");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                AddOnce(state.PendingClose, "cron:*");
                logger.LogWarning(exception, "Askı: zamanlanmış görevler listelenemedi.");
            }
        }

        // 4) Posta ve veritabanı kullanıcıları: panelde GÜVENLE kapatan bir yol YOK. Bunu başarı gibi
        //    göstermek yerine açıkça kaydederiz (dikkat öğesi + denetim cümlesi).
        AddOnce(state.Unsupported, "mail");
        AddOnce(state.Unsupported, "database");

        await customerScopes.SetSuspensionStateAsync(userId, state, cancellationToken);

        var extras = new List<string>();
        if (ftpClosed + cronClosed > 0)
        {
            extras.Add($"{ftpClosed} FTP account(s) and {cronClosed} scheduled task(s) disabled.");
        }

        var pendingNonSite = state.PendingClose.Where(item => !item.StartsWith(SitePrefix, StringComparison.Ordinal)).ToArray();
        if (pendingNonSite.Length > 0)
        {
            extras.Add($"NOT DISABLED (will be retried): {string.Join(", ", pendingNonSite)}.");
        }

        if (state.Unsupported.Count > 0)
        {
            extras.Add($"CANNOT BE SUSPENDED BY THE PANEL (still open): {string.Join(", ", state.Unsupported)}.");
        }

        return new SuspensionReport(sites.Count, failedSites, string.Join(' ', extras), state.PendingClose.Count > 0);
    }

    /// <summary>Askıdan ÖNCE açık olan kaynakları geri açar; başaramadıklarını yeniden denenmek üzere tutar.</summary>
    /// <param name="userId">Abonelik.</param>
    /// <param name="legacySites">
    /// Yalnız <b>önceki hal kaydı OLMAYAN</b> bir askıyı kaldırırken (bu düzeltmeden ÖNCE alınmış askılar)
    /// başlatılacak siteler; null ise kayıt yoksa hiçbir şey açılmaz. Kayıt yokken sitelerin önceki halini
    /// bilemeyiz; hepsini açmak (eski davranış) askıda kalmış müşteriyi sonsuza dek kapalı bırakmaktan iyidir.
    /// </param>
    public async Task<SuspensionReport> RestoreAsync(Guid userId, CancellationToken cancellationToken,
        IReadOnlyCollection<string>? legacySites = null)
    {
        var state = await customerScopes.GetSuspensionStateAsync(userId, cancellationToken);
        if (state is null)
        {
            if (legacySites is null || legacySites.Count == 0)
            {
                return new SuspensionReport(0, [], string.Empty, false);
            }

            var legacyFailed = new List<string>();
            foreach (var site in legacySites)
            {
                try
                {
                    await iisSiteService.StartSiteAsync(site, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    legacyFailed.Add(site);
                    logger.LogWarning(exception, "Askı kaldırılırken site başlatılamadı: {Site}", site);
                }
            }

            // Başlatılamayan siteler yeniden denenmek üzere kaydedilir (sessiz başarı yok).
            if (legacyFailed.Count > 0)
            {
                var retry = new SubscriptionSuspensionState();
                foreach (var site in legacyFailed)
                {
                    retry.OpenBefore.Add(SitePrefix + site);
                    retry.PendingRestore.Add(SitePrefix + site);
                }

                await customerScopes.SetSuspensionStateAsync(userId, retry, cancellationToken);
            }

            return new SuspensionReport(legacySites.Count, legacyFailed, string.Empty, legacyFailed.Count > 0);
        }

        // Hiç kapatılamayanlar zaten açık: geri açılacak bir şey yok.
        state.OpenBefore.RemoveAll(item => state.PendingClose.Contains(item));
        state.PendingClose.Clear();

        var failedSites = new List<string>();
        var restoredSites = 0;
        var siteRequested = 0;
        var otherRestored = 0;

        foreach (var key in state.OpenBefore.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (key.StartsWith(SitePrefix, StringComparison.Ordinal))
                {
                    siteRequested++;
                    await iisSiteService.StartSiteAsync(key[SitePrefix.Length..], cancellationToken);
                    restoredSites++;
                }
                else if (key.StartsWith(FtpPrefix, StringComparison.Ordinal) && ftpService is not null)
                {
                    await ftpService.SetFtpUserEnabledAsync(key[FtpPrefix.Length..], true, cancellationToken);
                    otherRestored++;
                }
                else if (key.StartsWith(CronPrefix, StringComparison.Ordinal) && schedulerService is not null)
                {
                    await schedulerService.SetTaskEnabledAsync(key[CronPrefix.Length..], true, cancellationToken);
                    otherRestored++;
                }
                else
                {
                    throw new NotSupportedException($"No handler to restore '{key}'.");
                }

                state.OpenBefore.Remove(key);
                state.PendingRestore.Remove(key);
            }
            catch (Exception exception) when (exception is AppException { HttpStatusCode: 404 })
            {
                // Kaynak bu arada SİLİNMİŞ: geri açılacak bir şey kalmadı (sonsuza dek bekleyen uyarı olmasın).
                logger.LogInformation(exception, "Askı kaldırma: kaynak artık yok, atlandı: {Key}", key);
                state.OpenBefore.Remove(key);
                state.PendingRestore.Remove(key);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (key.StartsWith(SitePrefix, StringComparison.Ordinal))
                {
                    failedSites.Add(key[SitePrefix.Length..]);
                }

                AddOnce(state.PendingRestore, key);
                logger.LogWarning(exception, "Askı kaldırılırken kaynak açılamadı: {Key}", key);
            }
        }

        state.Unsupported.Clear();
        var done = state.OpenBefore.Count == 0 && state.PendingRestore.Count == 0;
        await customerScopes.SetSuspensionStateAsync(userId, done ? null : state, cancellationToken);

        var extras = new List<string>();
        if (otherRestored > 0)
        {
            extras.Add($"{otherRestored} FTP account(s)/scheduled task(s) re-enabled (only those open before suspension).");
        }

        var pendingNonSite = state.PendingRestore.Where(item => !item.StartsWith(SitePrefix, StringComparison.Ordinal)).ToArray();
        if (pendingNonSite.Length > 0)
        {
            extras.Add($"STILL DISABLED (will be retried): {string.Join(", ", pendingNonSite)}.");
        }

        return new SuspensionReport(siteRequested, failedSites, string.Join(' ', extras), !done);
    }

    /// <summary>
    /// Zorlama turunda: bekleyen adım varsa yeniden dener. <paramref name="shouldBeClosed"/> abonelik şu an
    /// askıda (ya da yönetici tarafından pasifte) mi.
    /// </summary>
    public async Task<SuspensionReport?> RetryAsync(Guid userId, bool shouldBeClosed, IReadOnlyCollection<string> sites, CancellationToken cancellationToken)
    {
        var state = await customerScopes.GetSuspensionStateAsync(userId, cancellationToken);
        if (state is null)
        {
            return null;
        }

        if (shouldBeClosed)
        {
            return state.PendingClose.Count > 0 ? await CloseAsync(userId, sites, cancellationToken) : null;
        }

        return await RestoreAsync(userId, cancellationToken);
    }

    private static void AddOnce(List<string> list, string item)
    {
        if (!list.Contains(item))
        {
            list.Add(item);
        }
    }
}
