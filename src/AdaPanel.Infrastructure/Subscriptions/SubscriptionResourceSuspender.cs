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
    ISchedulerService? schedulerService = null,
    IMailService? mailService = null,
    IDatabaseService? databaseService = null,
    IDatabaseOwnershipService? databaseOwnership = null)
{
    internal const string SitePrefix = "site:";
    internal const string FtpPrefix = "ftp:";
    internal const string CronPrefix = "cron:";
    internal const string MailPrefix = "mail:";
    internal const string DbUserPrefix = "dbuser:";
    private const string MailListKey = "mail:*";
    private const string DbListKey = "dbuser:*";
    private const string FtpListKey = "ftp:*";

    /// <summary>Abonelik kimliğine bağlı cron görevi öneki (CronController.GetOwnerPrefixAsync ile AYNI biçim).</summary>
    internal static string CronOwnerPrefix(Guid userId) => $"u{userId:N}--";

    /// <summary>Kaynakları kapatır (idempotent: tekrar çağrılabilir, önceki hal korunur).</summary>
    /// <param name="legacyClose">
    /// Yalniz <see cref="RetryAsync"/> kaydi OLMAYAN eski bir askiyi/pasifi yeniden kapatirken true verir. Eski kod siteleri
    /// zaten durdurmustu; onlarin onceki hali bilinmez, bu yuzden "Stopped" gorunen siteler de OpenBefore'a girer
    /// (eski "hepsini ac" varsayimi korunur). Aksi halde kayit olusur, Restore legacy yoluna dusmez ve musterinin siteleri
    /// sessizce kapali kalirdi. FTP/cron mevcut durumlarina gore islenir.
    /// </param>
    public async Task<SuspensionReport> CloseAsync(Guid userId, IReadOnlyCollection<string> sites, CancellationToken cancellationToken,
        bool legacyClose = false)
    {
        var state = await customerScopes.GetSuspensionStateAsync(userId, cancellationToken) ?? new SubscriptionSuspensionState();
        var failedSites = new List<string>();
        var ftpClosed = 0;
        var cronClosed = 0;
        var mailClosed = 0;
        var dbClosed = 0;

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

            if (!open && legacyClose)
            {
                AddOnce(state.OpenBefore, key);
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

        // 4) Posta kutuları: Stalwart'ta hesap izinleri kapatılır (posta SİLİNMEZ, geri okunur). MailEnable için
        //    kanıtlanmış yöntem yok → "desteklenmiyor" olarak KAYDEDİLİR (kapattım denmez).
        if (mailService is null)
        {
            AddOnce(state.Unsupported, "mail");
        }
        else
        {
            mailClosed = await CloseMailAsync(userId, state, cancellationToken);
        }

        // 5) Veritabanı kullanıcıları: MariaDB/MySQL ACCOUNT LOCK. Yalnız TÜM yetkisi bu aboneliğin
        //    veritabanlarında olan hesaplar kilitlenir; başka kiracıyla paylaşılan hesap kilitlenmez (raporlanır).
        if (databaseService is null || databaseOwnership is null)
        {
            AddOnce(state.Unsupported, "database");
        }
        else
        {
            dbClosed = await CloseDatabaseUsersAsync(userId, state, cancellationToken);
        }

        await customerScopes.SetSuspensionStateAsync(userId, state, cancellationToken);

        var extras = new List<string>();
        if (ftpClosed + cronClosed > 0)
        {
            extras.Add($"{ftpClosed} FTP account(s) and {cronClosed} scheduled task(s) disabled.");
        }

        if (mailClosed + dbClosed > 0)
        {
            extras.Add($"{mailClosed} mailbox(es) and {dbClosed} database account(s) suspended.");
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
    /// Yalnız <b>önceki hal kaydı OLMAYAN</b> bir askıyı kaldırırken (bu düzeltmeden ÖNCE alınmış askılar, ya da
    /// hiç askıda olmayan aboneliğe yapılan açık "askıyı kaldır" çağrısı) başlatılacak siteler; null ise kayıt
    /// yoksa hiçbir şey açılmaz (zorlama turundaki yeniden deneme böyle çağırır: kendiliğinden site AÇMAZ). Kayıt yokken sitelerin önceki halini
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
                else if (key.StartsWith(MailPrefix, StringComparison.Ordinal) && mailService is not null)
                {
                    await mailService.SetAccountActiveAsync(
                        new AdaPanel.Core.Dtos.Mail.SetMailAccountActiveRequest { Address = key[MailPrefix.Length..], Active = true }, cancellationToken);
                    otherRestored++;
                }
                else if (key.StartsWith(DbUserPrefix, StringComparison.Ordinal) && databaseService is not null)
                {
                    var parts = key[DbUserPrefix.Length..].Split('|', 3);
                    if (parts.Length != 3) throw new NotSupportedException($"Malformed database account key '{key}'.");
                    await databaseService.SetUserLockedAsync(parts[0], parts[1], parts[2], false, cancellationToken);
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
            extras.Add($"{otherRestored} FTP account(s)/scheduled task(s)/mailbox(es)/database account(s) re-enabled (only those open before suspension: FTP, tasks, mailboxes, database accounts).");
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
        if (shouldBeClosed)
        {
            // Kayit YOKSA ama kapali olmaliysa (bu duzeltmeden onceki askilar, ya da kayit hic yazilamadi)
            // kapatma yeniden uygulanir: FTP/cron/posta durumu da boylece kaydedilir.
            return state is null || state.PendingClose.Count > 0
                ? await CloseAsync(userId, sites, cancellationToken, legacyClose: state is null)
                : null;
        }

        return state is null ? null : await RestoreAsync(userId, cancellationToken);
    }

    /// <summary>Posta kutularını kapatır; kapatılan sayıyı döner. Sessiz başarı yok: hata PendingClose'a yazılır.</summary>
    private async Task<int> CloseMailAsync(Guid userId, SubscriptionSuspensionState state, CancellationToken cancellationToken)
    {
        var closed = 0;
        var unmeasured = false;
        try
        {
            var domains = await customerScopes.GetAllowedMailDomainsAsync(userId, cancellationToken);
            foreach (var domain in domains)
            {
                var workspace = await mailService!.GetWorkspaceAsync(domain, cancellationToken);
                if (!workspace.Installed || !workspace.Configured)
                {
                    continue; // posta sunucusu yok/yapılandırılmamış: kapatılacak kutu yok
                }

                if (!workspace.AccountsMeasured)
                {
                    AddOnce(state.PendingClose, MailListKey); // kutular ölçülemedi: kapatıldı DENMEZ, yeniden denenir
                    unmeasured = true;
                    continue;
                }

                foreach (var account in workspace.Accounts)
                {
                    var key = MailPrefix + account.Address;
                    if (account.Active == false)
                    {
                        state.PendingClose.Remove(key); // zaten kapalı (elle ya da önceki turda)
                        continue;
                    }

                    AddOnce(state.OpenBefore, key);
                    try
                    {
                        await mailService.SetAccountActiveAsync(
                            new AdaPanel.Core.Dtos.Mail.SetMailAccountActiveRequest { Address = account.Address, Active = false }, cancellationToken);
                        state.PendingClose.Remove(key);
                        closed++;
                    }
                    catch (NotSupportedException)
                    {
                        state.OpenBefore.Remove(key);
                        AddOnce(state.Unsupported, "mail");
                        return closed; // bu posta sunucusu hiçbir kutuyu kapatamaz: tek tek denemenin anlamı yok
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        AddOnce(state.PendingClose, key);
                        logger.LogWarning(exception, "Askı: posta kutusu kapatılamadı: {Account}", account.Address);
                    }
                }
            }

            if (!unmeasured) state.PendingClose.Remove(MailListKey);
        }
        catch (NotSupportedException)
        {
            AddOnce(state.Unsupported, "mail");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AddOnce(state.PendingClose, MailListKey);
            logger.LogWarning(exception, "Askı: posta kutuları listelenemedi.");
        }

        return closed;
    }

    /// <summary>Aboneliğin veritabanı hesaplarını kilitler (yalnız münhasır hesaplar); kilitlenen sayıyı döner.</summary>
    private async Task<int> CloseDatabaseUsersAsync(Guid userId, SubscriptionSuspensionState state, CancellationToken cancellationToken)
    {
        var closed = 0;
        try
        {
            var owned = await databaseOwnership!.GetOwnedAsync(userId, cancellationToken);
            foreach (var group in owned.GroupBy(item => item.Engine, StringComparer.OrdinalIgnoreCase))
            {
                IReadOnlyCollection<AdaPanel.Core.Dtos.Databases.DatabaseUserLockDto> locks;
                try
                {
                    locks = await databaseService!.GetUserLocksAsync(group.Key, group.Select(item => item.DatabaseName).ToArray(), cancellationToken);
                }
                catch (NotSupportedException)
                {
                    AddOnce(state.Unsupported, "database"); // PostgreSQL/MSSQL: kilit yöntemi yok
                    continue;
                }

                foreach (var account in locks)
                {
                    var key = $"{DbUserPrefix}{account.Engine}|{account.UserName}|{account.Host}";
                    if (!account.Exclusive)
                    {
                        // Başka kiracının veritabanına da yetkili: kilitlemek onu da keser → dokunulmaz, açıkça söylenir.
                        AddOnce(state.Unsupported, $"database:{account.UserName}@{account.Host} (shared with another subscription)");
                        continue;
                    }

                    if (account.Locked == true)
                    {
                        state.PendingClose.Remove(key); // zaten kilitli
                        continue;
                    }

                    AddOnce(state.OpenBefore, key);
                    try
                    {
                        await databaseService!.SetUserLockedAsync(account.Engine, account.UserName, account.Host, true, cancellationToken);
                        state.PendingClose.Remove(key);
                        closed++;
                    }
                    catch (NotSupportedException)
                    {
                        state.OpenBefore.Remove(key);
                        AddOnce(state.Unsupported, "database");
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        AddOnce(state.PendingClose, key);
                        logger.LogWarning(exception, "Askı: veritabanı hesabı kilitlenemedi: {Account}", account.UserName);
                    }
                }
            }

            state.PendingClose.Remove(DbListKey);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AddOnce(state.PendingClose, DbListKey);
            logger.LogWarning(exception, "Askı: veritabanı hesapları okunamadı.");
        }

        return closed;
    }

    private static void AddOnce(List<string> list, string item)
    {
        if (!list.Contains(item))
        {
            list.Add(item);
        }
    }
}
