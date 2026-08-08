using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Services;

public sealed class UserManagementService(
    AdaPanelDbContext dbContext,
    IPasswordHasher<PanelUser> passwordHasher,
    ICustomerScopeService customerScopeService,
    INotificationService notificationService,
    IPanelPreferencesService preferencesService,
    IPanelSecurityService securityService,
    ILogger<UserManagementService> logger,
    IAuditService auditService,
    IIisSiteService iisSiteService,
    IDatabaseOwnershipService databaseOwnership,
    ISchedulerService schedulerService,
    ISiteCascadeDeletionService siteCascadeDeletion,
    IApiTokenService apiTokenService) : IUserManagementService
{
    public async Task<IReadOnlyCollection<UserSummaryDto>> GetUsersAsync(Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var users = await dbContext.Users
            .OrderBy(user => user.UserName)
            .ToListAsync(cancellationToken);

        // Sahip (bayi) adlarını çözmek için TÜM kullanıcılardan ad haritası (filtreden önce).
        var nameById = users.ToDictionary(user => user.Id, user => user.UserName);

        // Bayi (Reseller) yalnızca KENDI sahip olduğu müşterileri + o müşterilerin alt-kullanıcılarını görür
        // (alt-kullanıcının OwnerId'si yoktur; parent bağı üzerinden dahil edilir). Admin herkesi görür.
        var actor = await dbContext.Users.FirstOrDefaultAsync(candidate => candidate.Id == actorUserId, cancellationToken);
        if (actor?.Role == UserRole.Reseller)
        {
            var visible = new HashSet<Guid>(await customerScopeService.GetOwnedUserIdsAsync(actorUserId, cancellationToken));
            foreach (var ownedId in visible.ToArray())
            {
                foreach (var subUserId in await customerScopeService.GetSubUserIdsAsync(ownedId, cancellationToken))
                {
                    visible.Add(subUserId);
                }
            }

            users = users.Where(user => visible.Contains(user.Id)).ToList();
        }

        var summaries = new List<UserSummaryDto>(users.Count);
        foreach (var user in users)
        {
            if (user.Role == UserRole.Customer)
            {
                var scope = await customerScopeService.GetScopeAsync(user.Id, cancellationToken);
                var usageMb = await customerScopeService.GetDiskUsageMbAsync(user.Id, cancellationToken);
                var ownerId = await customerScopeService.GetOwnerAsync(user.Id, cancellationToken);
                var dbCount = await databaseOwnership.CountForOwnerAsync(user.Id, cancellationToken);
                var parentId = await customerScopeService.GetParentSubscriptionAsync(user.Id, cancellationToken);
                summaries.Add(MapUser(user, scope, usageMb, ownerId, ResolveOwnerName(ownerId, nameById), dbCount,
                    parentId, ResolveOwnerName(parentId, nameById)));
            }
            else
            {
                summaries.Add(MapUser(user, new CustomerScope([], 0, 0, 0, 0, 0), 0, null, string.Empty));
            }
        }

        return summaries;
    }

    public async Task<UserSummaryDto> CreateUserAsync(CreateUserRequest request, Guid actorUserId, string? ipAddress, CancellationToken cancellationToken = default)
    {
        var normalizedUserName = request.UserName.Trim();
        var normalizedEmail = request.Email.Trim();
        var normalizedUserNameLookup = normalizedUserName.ToLowerInvariant();
        var normalizedEmailLookup = normalizedEmail.ToLowerInvariant();

        if (await dbContext.Users.AnyAsync(user => user.UserName.ToLower() == normalizedUserNameLookup, cancellationToken))
        {
            throw new InvalidOperationException("users.duplicate_username");
        }

        if (await dbContext.Users.AnyAsync(user => user.Email.ToLower() == normalizedEmailLookup, cancellationToken))
        {
            throw new InvalidOperationException("users.duplicate_email");
        }

        // Bayi (Reseller) yalnızca KENDISINE ait müşteri oluşturabilir: rol Customer'a, sahip
        // bayinin kendisine zorlanır (istek gövdesindeki role/ownerId yok sayılır).
        var actor = await dbContext.Users.FirstOrDefaultAsync(candidate => candidate.Id == actorUserId, cancellationToken);
        var actorIsReseller = actor?.Role == UserRole.Reseller;
        var role = actorIsReseller ? UserRole.Customer : Enum.Parse<UserRole>(request.Role, ignoreCase: true);
        var ownerId = actorIsReseller ? actorUserId : request.OwnerId;

        // Abonelik altı ek kullanıcı (Plesk "additional user"): yalnız Admin, mevcut TOP-LEVEL bir müşteriyi
        // parent seçerek oluşturur. Parent bir müşteri olmalı ve KENDISI alt-kullanıcı OLMAMALI (iç içe yok).
        Guid? parentUserId = null;
        if (!actorIsReseller && role == UserRole.Customer && request.ParentUserId is { } requestedParent)
        {
            var parent = await dbContext.Users.FirstOrDefaultAsync(candidate => candidate.Id == requestedParent, cancellationToken);
            if (parent is null || parent.Role != UserRole.Customer)
            {
                throw new InvalidOperationException("users.invalid_parent");
            }

            if (await customerScopeService.GetParentSubscriptionAsync(requestedParent, cancellationToken) is not null)
            {
                throw new InvalidOperationException("users.parent_is_subuser");
            }

            parentUserId = requestedParent;
        }

        var user = new PanelUser
        {
            UserName = normalizedUserName,
            Email = normalizedEmail,
            Role = role,
            PreferredLanguage = request.PreferredLanguage.Trim(),
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        user.PasswordHash = passwordHasher.HashPassword(user, request.Password);
        dbContext.Users.Add(user);

        await auditService.WriteAsync(
            "users.created",
            "users",
            $"User '{user.UserName}' created with role '{user.Role}'.",
            actorUserId,
            ipAddress,
            cancellationToken);

        CustomerScope scope;
        if (parentUserId is { } resolvedParent)
        {
            // Alt-kullanıcı: kendi kaynağı yok; parent'ın aboneliğini (site/kotalar) miras alır.
            await customerScopeService.SetParentSubscriptionAsync(user.Id, resolvedParent, cancellationToken);
            scope = await customerScopeService.GetScopeAsync(user.Id, cancellationToken);
        }
        else
        {
            scope = await ApplyScopeAsync(user, request.AssignedSites, request.MaxSites, request.MaxDiskMb, request.MaxDatabases, request.MaxMailboxes, request.MaxScheduledTasks, actorIsReseller ? actorUserId : null, cancellationToken);
            if (role == UserRole.Customer)
            {
                await customerScopeService.SetOwnerAsync(user.Id, ownerId, cancellationToken);
            }
        }

        if (request.SendWelcomeEmail)
        {
            await TrySendWelcomeEmailAsync(user, request.Password, cancellationToken);
        }

        return MapUser(user, scope, 0, ownerId, await ResolveOwnerNameAsync(ownerId, cancellationToken), 0,
            parentUserId, await ResolveOwnerNameAsync(parentUserId, cancellationToken));
    }

    /// <summary>
    /// Yeni kullanıcıya giriş bilgilerini içeren hoş geldin e-postası gönderir.
    /// SMTP yapılandırılmamışsa veya gönderim başarısızsa kullanıcı oluşturma akışı
    /// bozulmaz; durum yalnızca loglanır.
    /// </summary>
    private async Task TrySendWelcomeEmailAsync(PanelUser user, string password, CancellationToken cancellationToken)
    {
        try
        {
            if (!await notificationService.IsEmailConfiguredAsync(cancellationToken))
            {
                logger.LogInformation("Welcome email skipped for {UserName}: SMTP not configured.", user.UserName);
                return;
            }

            var preferences = await preferencesService.GetAsync(cancellationToken);
            var securePath = await securityService.GetSecureEntryPathAsync(cancellationToken);
            var panelName = string.IsNullOrWhiteSpace(preferences.PanelName) ? "AdaPanel" : preferences.PanelName;
            var loginUrl = BuildLoginUrl(preferences.ServerAddress, preferences.PreferredHttpsPort, securePath);
            var isTurkish = user.PreferredLanguage.StartsWith("tr", StringComparison.OrdinalIgnoreCase);

            var subject = isTurkish
                ? $"{panelName} hesabınız oluşturuldu"
                : $"Your {panelName} account is ready";
            var body = isTurkish
                ? $"""
                  Merhaba {user.UserName},

                  {panelName} kontrol paneli hesabınız oluşturuldu.

                  Kullanıcı adı: {user.UserName}
                  Geçici parola: {password}
                  Panel adresi: {loginUrl}

                  Güvenliğiniz için ilk girişten sonra parolanızı değiştirmeniz önerilir.
                  """
                : $"""
                  Hello {user.UserName},

                  Your {panelName} control panel account has been created.

                  Username: {user.UserName}
                  Temporary password: {password}
                  Panel address: {loginUrl}

                  For your security, please change your password after the first sign-in.
                  """;

            await notificationService.SendEmailToAsync(user.Email, subject, body, cancellationToken);
            logger.LogInformation("Welcome email sent to {Email}.", user.Email);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Welcome email could not be sent to {Email}.", user.Email);
        }
    }

    private static string BuildLoginUrl(string serverAddress, int httpsPort, string securePath)
    {
        var host = string.IsNullOrWhiteSpace(serverAddress) ? "<sunucu-adresi>" : serverAddress.Trim();
        var portPart = httpsPort == 443 ? string.Empty : $":{httpsPort}";
        return $"https://{host}{portPart}/{securePath}";
    }

    public async Task<UserSummaryDto> UpdateUserAsync(Guid userId, UpdateUserRequest request, Guid actorUserId, string? ipAddress, CancellationToken cancellationToken = default)
    {
        var user = await dbContext.Users.FirstOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken);
        if (user is null)
        {
            throw new InvalidOperationException("users.not_found");
        }

        var normalizedEmail = request.Email.Trim();
        var normalizedEmailLookup = normalizedEmail.ToLowerInvariant();
        if (!string.Equals(user.Email, normalizedEmail, StringComparison.OrdinalIgnoreCase) &&
            await dbContext.Users.AnyAsync(candidate => candidate.Email.ToLower() == normalizedEmailLookup && candidate.Id != userId, cancellationToken))
        {
            throw new InvalidOperationException("users.duplicate_email");
        }

        // Bayi (Reseller) yalnızca KENDI müşterisini güncelleyebilir; rol/sahip değiştiremez.
        var actor = await dbContext.Users.FirstOrDefaultAsync(candidate => candidate.Id == actorUserId, cancellationToken);
        var actorIsReseller = actor?.Role == UserRole.Reseller;
        if (actorIsReseller)
        {
            var currentOwner = await customerScopeService.GetOwnerAsync(userId, cancellationToken);
            if (user.Role != UserRole.Customer || currentOwner != actorUserId)
            {
                throw new AppException("users.forbidden", 403, 403);
            }
        }

        var newRole = actorIsReseller ? UserRole.Customer : Enum.Parse<UserRole>(request.Role, ignoreCase: true);
        var ownerId = actorIsReseller ? actorUserId : request.OwnerId;
        var willRemoveActiveAdmin = user.Role == UserRole.Administrator &&
            (newRole != UserRole.Administrator || !request.IsActive);

        if (willRemoveActiveAdmin)
        {
            var otherActiveAdmins = await dbContext.Users.CountAsync(candidate =>
                candidate.Id != userId &&
                candidate.Role == UserRole.Administrator &&
                candidate.IsActive, cancellationToken);

            if (otherActiveAdmins == 0)
            {
                throw new InvalidOperationException("users.last_admin_protected");
            }
        }

        // profesyonel paneller gibi abonelik askıya alma: aktiflik değişimini yakala (sonra sitelerini durdur/başlat).
        var wasActive = user.IsActive;

        user.Email = normalizedEmail;
        user.PreferredLanguage = request.PreferredLanguage.Trim();
        user.Role = newRole;
        user.IsActive = request.IsActive;
        user.UpdatedAtUtc = DateTime.UtcNow;

        await auditService.WriteAsync(
            "users.updated",
            "users",
            $"User '{user.UserName}' updated. Role='{user.Role}', Active='{user.IsActive}'.",
            actorUserId,
            ipAddress,
            cancellationToken);

        // Alt-kullanıcı ise: kapsamı parent'tan MIRAS; düzenlemede kendi site/kota YAZILMAZ (parent bağı korunur).
        var existingParent = user.Role == UserRole.Customer
            ? await customerScopeService.GetParentSubscriptionAsync(user.Id, cancellationToken)
            : null;

        CustomerScope scope;
        if (existingParent is not null)
        {
            scope = await customerScopeService.GetScopeAsync(user.Id, cancellationToken);
        }
        else
        {
            scope = await ApplyScopeAsync(user, request.AssignedSites, request.MaxSites, request.MaxDiskMb, request.MaxDatabases, request.MaxMailboxes, request.MaxScheduledTasks, actorIsReseller ? actorUserId : null, cancellationToken);
            if (user.Role == UserRole.Customer)
            {
                await customerScopeService.SetOwnerAsync(user.Id, ownerId, cancellationToken);
            }
        }

        // Aktiflik değiştiyse (askıya al / sürdür) müşterinin atanan sitelerini durdur/başlat.
        // ALT-KULLANICI: PAYLAŞILAN siteleri DURDURMA (yalnız o login IsActive=false ile engellenir) → boş kapsam geç.
        var suspensionScope = existingParent is not null ? new CustomerScope([], 0, 0, 0, 0, 0) : scope;
        await ApplySuspensionStateAsync(user, suspensionScope, wasActive, actorUserId, ipAddress, cancellationToken);

        var usageMb = user.Role == UserRole.Customer
            ? await customerScopeService.GetDiskUsageMbAsync(user.Id, cancellationToken)
            : 0;
        var dbCount = user.Role == UserRole.Customer
            ? await databaseOwnership.CountForOwnerAsync(user.Id, cancellationToken)
            : 0;
        var resolvedOwner = user.Role == UserRole.Customer ? ownerId : null;
        var parentId = user.Role == UserRole.Customer
            ? await customerScopeService.GetParentSubscriptionAsync(user.Id, cancellationToken)
            : null;
        return MapUser(user, scope, usageMb, resolvedOwner, await ResolveOwnerNameAsync(resolvedOwner, cancellationToken), dbCount,
            parentId, await ResolveOwnerNameAsync(parentId, cancellationToken));
    }

    /// <summary>Customer rolünde kapsamı (siteler + limitler) kaydeder; diğer rollerde varsa temizler.</summary>
    private async Task<CustomerScope> ApplyScopeAsync(PanelUser user, string[] assignedSites, int maxSites, int maxDiskMb, int maxDatabases, int maxMailboxes, int maxScheduledTasks, Guid? resellerActorId, CancellationToken cancellationToken)
    {
        if (user.Role == UserRole.Customer)
        {
            // maxSites > 0 ise atanan site sayısı sınırı aşamaz.
            if (maxSites > 0 && assignedSites.Length > maxSites)
            {
                throw new InvalidOperationException("users.too_many_sites");
            }

            // GÜVENLİK (çapraz-kiracı): bayi YALNIZ kendi kapsamındaki siteleri atayabilir.
            //
            // Burada eskiden sadece SAYI denetleniyordu; sitelerin KİME ait olduğu hiç
            // sorulmuyordu. Bu, bayi için tam bir kiracı kaçışıydı: müşterisine başkasının
            // sitesini atayan bayi, o siteyi kendi etkin kapsamına da sokuyordu
            // (GetResellerSitesAsync = kendi siteleri + sahip olduğu müşterilerin siteleri).
            // Ardından EnsureSiteAsync geçiyor ve dosyalar, veritabanı, mail, hatta silme
            // açılıyordu.
            //
            // Yönetici kısıtsızdır (mevcut davranış). Bayinin kendi sitelerini ve mevcut
            // müşterilerinin sitelerini atamaya devam etmesi de bozulmaz: kontrol tam olarak
            // o kümeye bakıyor.
            if (resellerActorId is { } bayiId)
            {
                var izinli = await customerScopeService.GetResellerSitesAsync(bayiId, cancellationToken);
                var yabanci = assignedSites.FirstOrDefault(site =>
                    !izinli.Contains(site, StringComparer.OrdinalIgnoreCase));
                if (yabanci is not null)
                {
                    throw new AppException("users.site_forbidden", 403, 403);
                }
            }

            await customerScopeService.SetScopeAsync(user.Id, assignedSites, maxSites, maxDiskMb, cancellationToken);
            await customerScopeService.SetExtraLimitsAsync(user.Id, maxDatabases, maxMailboxes, maxScheduledTasks, cancellationToken);
            return await customerScopeService.GetScopeAsync(user.Id, cancellationToken);
        }

        await customerScopeService.RemoveUserAsync(user.Id, cancellationToken);
        return new CustomerScope([], 0, 0, 0, 0, 0);
    }

    /// <summary>
    /// profesyonel paneller gibi abonelik yaşam döngüsü: Customer'ın aktifliği true→false olunca (askıya alma)
    /// atanan sitelerini durdur (web siteleri 503 döner), false→true olunca (sürdürme) başlat.
    /// En iyi çaba; tek bir sitenin hatası kullanıcı güncelleme akışını bozmaz. (Mail/FTP askıya
    /// alma sonraki adım; panel girişi zaten IsActive=false ile engellenir.)
    /// </summary>
    private async Task ApplySuspensionStateAsync(
        PanelUser user,
        CustomerScope scope,
        bool wasActive,
        Guid actorUserId,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        if (user.Role != UserRole.Customer || wasActive == user.IsActive || scope.Sites.Count == 0)
        {
            return;
        }

        var suspended = !user.IsActive;
        foreach (var site in scope.Sites)
        {
            try
            {
                if (suspended)
                {
                    await iisSiteService.StopSiteAsync(site, cancellationToken);
                }
                else
                {
                    await iisSiteService.StartSiteAsync(site, cancellationToken);
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Abonelik {State} site '{Site}' üzerinde uygulanamadı (kullanıcı: {User}).",
                    suspended ? "durdurma" : "başlatma",
                    site,
                    user.UserName);
            }
        }

        await auditService.WriteAsync(
            suspended ? "users.suspended" : "users.resumed",
            "users",
            $"Customer '{user.UserName}' {(suspended ? "suspended" : "resumed")}; {scope.Sites.Count} site(s) {(suspended ? "stopped" : "started")}.",
            actorUserId,
            ipAddress,
            cancellationToken);
    }

    public async Task<UserSummaryDto> UnlockUserAsync(Guid userId, Guid actorUserId, string? ipAddress, CancellationToken cancellationToken = default)
    {
        var user = await dbContext.Users.FirstOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken)
            ?? throw new InvalidOperationException("users.not_found");

        // Bayi yalnız KENDİ müşterisinin kilidini açabilir (silme/güncellemeyle aynı kapsam kuralı).
        var actor = await dbContext.Users.FirstOrDefaultAsync(candidate => candidate.Id == actorUserId, cancellationToken);
        if (actor?.Role == UserRole.Reseller)
        {
            var currentOwner = await customerScopeService.GetOwnerAsync(userId, cancellationToken);
            if (user.Role != UserRole.Customer || currentOwner != actorUserId)
            {
                throw new AppException("users.forbidden", 403, 403);
            }
        }

        user.LockedUntilUtc = null;
        user.FailedLoginCount = 0;
        user.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditService.WriteAsync(
            "users.unlocked",
            "users",
            $"Login lock cleared for '{user.UserName}'.",
            actorUserId,
            ipAddress,
            cancellationToken);

        var scope = user.Role == UserRole.Customer
            ? await customerScopeService.GetScopeAsync(user.Id, cancellationToken)
            : new CustomerScope([], 0, 0, 0, 0, 0);
        var usageMb = user.Role == UserRole.Customer
            ? await customerScopeService.GetDiskUsageMbAsync(user.Id, cancellationToken)
            : 0;
        var dbCount = user.Role == UserRole.Customer
            ? await databaseOwnership.CountForOwnerAsync(user.Id, cancellationToken)
            : 0;
        var ownerId = user.Role == UserRole.Customer
            ? await customerScopeService.GetOwnerAsync(user.Id, cancellationToken)
            : null;
        var parentId = user.Role == UserRole.Customer
            ? await customerScopeService.GetParentSubscriptionAsync(user.Id, cancellationToken)
            : null;

        return MapUser(
            user,
            scope,
            usageMb,
            ownerId,
            await ResolveOwnerNameAsync(ownerId, cancellationToken),
            dbCount,
            parentId,
            await ResolveOwnerNameAsync(parentId, cancellationToken));
    }

    public async Task DeleteUserAsync(Guid userId, Guid actorUserId, bool removeSites, string? ipAddress, CancellationToken cancellationToken = default)
    {
        var user = await dbContext.Users.FirstOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken)
            ?? throw new InvalidOperationException("users.not_found");

        if (userId == actorUserId)
        {
            throw new InvalidOperationException("users.cannot_delete_self");
        }

        // Bayi yalnız KENDİ müşterisini silebilir; yıkıcı mod (site silme) admin'e özeldir.
        var actor = await dbContext.Users.FirstOrDefaultAsync(candidate => candidate.Id == actorUserId, cancellationToken);
        if (actor?.Role == UserRole.Reseller)
        {
            var currentOwner = await customerScopeService.GetOwnerAsync(userId, cancellationToken);
            if (user.Role != UserRole.Customer || currentOwner != actorUserId)
            {
                throw new AppException("users.forbidden", 403, 403);
            }

            removeSites = false;
        }

        if (user.Role == UserRole.Administrator)
        {
            var otherActiveAdmins = await dbContext.Users.CountAsync(candidate =>
                candidate.Id != userId &&
                candidate.Role == UserRole.Administrator &&
                candidate.IsActive, cancellationToken);
            if (otherActiveAdmins == 0)
            {
                throw new InvalidOperationException("users.last_admin_protected");
            }
        }

        var isSubUser = user.Role == UserRole.Customer
            && await customerScopeService.GetParentSubscriptionAsync(userId, cancellationToken) is not null;
        var scope = user.Role == UserRole.Customer && !isSubUser
            ? await customerScopeService.GetScopeAsync(userId, cancellationToken)
            : new CustomerScope([], 0, 0, 0, 0, 0);

        // 1) Top-level müşteri: altındaki ek kullanıcı login'leri de silinir (Plesk aboneliği kaldırma davranışı).
        if (user.Role == UserRole.Customer && !isSubUser)
        {
            foreach (var subUserId in await customerScopeService.GetSubUserIdsAsync(userId, cancellationToken))
            {
                var subUser = await dbContext.Users.FirstOrDefaultAsync(candidate => candidate.Id == subUserId, cancellationToken);
                if (subUser is not null)
                {
                    dbContext.Users.Remove(subUser);
                }

                await customerScopeService.RemoveUserAsync(subUserId, cancellationToken);
                await TryDeleteOwnedCronTasksAsync(subUserId, cancellationToken);
            }
        }

        // 2) Aboneliğin cron görevleri (u<id>-- ad alanı) Görev Zamanlayıcı'dan temizlenir (best-effort).
        await TryDeleteOwnedCronTasksAsync(userId, cancellationToken);

        // 3) Siteler: removeSites=true → dosyalar + eşleşen DB'lerle cascade silinir. false → siteler
        //    SUNUCUDA KALIR (yönetime devir); yalnız panel kayıtları temizlenir.
        if (removeSites)
        {
            foreach (var siteName in scope.Sites)
            {
                try
                {
                    await siteCascadeDeletion.DeleteAsync(siteName, deleteFiles: true, deleteDatabases: true, cancellationToken);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Kullanıcı silme: site cascade silinemedi: {Site}", siteName);
                }
            }
        }

        // 4) DB sahiplik KAYITLARI temizlenir (removeSites=false ise veritabanlarının kendisi durur).
        try
        {
            foreach (var owned in await databaseOwnership.GetOwnedAsync(userId, cancellationToken))
            {
                await databaseOwnership.RemoveAsync(owned.Engine, owned.DatabaseName, cancellationToken);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Kullanıcı silme: DB sahiplik kayıtları temizlenemedi.");
        }

        // 5) Kapsam kaydı + kullanıcı satırı. Audit yazımı SaveChanges'ı tetikler (mevcut desen).
        await customerScopeService.RemoveUserAsync(userId, cancellationToken);
        dbContext.Users.Remove(user);

        await auditService.WriteAsync(
            "users.deleted",
            "users",
            $"User '{user.UserName}' ({user.Role}) deleted. removeSites={removeSites}; {scope.Sites.Count} site(s) in scope.",
            actorUserId,
            ipAddress,
            cancellationToken);
    }

    /// <summary>Kullanıcının ad alanındaki (u&lt;id&gt;--) planlı görevleri siler; hata silmeyi durdurmaz.</summary>
    private async Task TryDeleteOwnedCronTasksAsync(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            var prefix = $"u{userId:N}--";
            foreach (var task in (await schedulerService.GetTasksAsync(cancellationToken))
                .Where(task => task.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    await schedulerService.DeleteTaskAsync(task.Name, cancellationToken);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Kullanıcı silme: cron görevi silinemedi: {Task}", task.Name);
                }
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Kullanıcı silme: cron görevleri listelenemedi.");
        }
    }

    // --- Abonelik altı ek kullanıcılar (Plesk "additional user"): top-level müşteri kendi altındaki
    // login'leri yönetir. Alt-kullanıcının kendi kaynağı yoktur; site/kota/cron/DB parent'tan miras. ---

    /// <summary>Aktörün top-level (alt-kullanıcı olmayan) bir Customer olduğunu doğrular ve kaydını döner.</summary>
    private async Task<PanelUser> EnsureTopLevelCustomerAsync(Guid parentUserId, CancellationToken cancellationToken)
    {
        var parent = await dbContext.Users.FirstOrDefaultAsync(candidate => candidate.Id == parentUserId, cancellationToken);
        if (parent is null || parent.Role != UserRole.Customer ||
            await customerScopeService.GetParentSubscriptionAsync(parentUserId, cancellationToken) is not null)
        {
            throw new AppException("users.sub_forbidden", 403, 403);
        }

        return parent;
    }

    private async Task<SubUserDto> MapSubUserAsync(PanelUser user, CancellationToken cancellationToken)
    {
        var sections = await customerScopeService.GetSubUserSectionsAsync(user.Id, cancellationToken);
        return new SubUserDto
        {
            Id = user.Id,
            UserName = user.UserName,
            Email = user.Email,
            IsActive = user.IsActive,
            LastLoginAtUtc = user.LastLoginAtUtc,
            CreatedAtUtc = user.CreatedAtUtc,
            AllowedSections = sections?.ToArray()
        };
    }

    public async Task<IReadOnlyCollection<SubUserDto>> GetSubUsersAsync(Guid parentUserId, CancellationToken cancellationToken = default)
    {
        await EnsureTopLevelCustomerAsync(parentUserId, cancellationToken);

        var subIds = await customerScopeService.GetSubUserIdsAsync(parentUserId, cancellationToken);
        var idSet = subIds.ToHashSet();
        var users = await dbContext.Users
            .Where(user => idSet.Contains(user.Id))
            .OrderBy(user => user.UserName)
            .ToListAsync(cancellationToken);

        var result = new List<SubUserDto>(users.Count);
        foreach (var user in users)
        {
            result.Add(await MapSubUserAsync(user, cancellationToken));
        }

        return result;
    }

    public async Task<SubUserDto> CreateSubUserAsync(Guid parentUserId, CreateSubUserRequest request, string? ipAddress, CancellationToken cancellationToken = default)
    {
        var parent = await EnsureTopLevelCustomerAsync(parentUserId, cancellationToken);

        var normalizedUserName = request.UserName.Trim();
        var normalizedEmail = request.Email.Trim();
        var userNameLookup = normalizedUserName.ToLowerInvariant();
        var emailLookup = normalizedEmail.ToLowerInvariant();

        if (await dbContext.Users.AnyAsync(user => user.UserName.ToLower() == userNameLookup, cancellationToken))
        {
            throw new InvalidOperationException("users.duplicate_username");
        }

        if (await dbContext.Users.AnyAsync(user => user.Email.ToLower() == emailLookup, cancellationToken))
        {
            throw new InvalidOperationException("users.duplicate_email");
        }

        var user = new PanelUser
        {
            UserName = normalizedUserName,
            Email = normalizedEmail,
            Role = UserRole.Customer,
            PreferredLanguage = parent.PreferredLanguage,
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        user.PasswordHash = passwordHasher.HashPassword(user, request.Password);
        dbContext.Users.Add(user);

        await auditService.WriteAsync(
            "users.sub_created",
            "users",
            $"Additional user '{user.UserName}' created under subscription '{parent.UserName}'.",
            parentUserId,
            ipAddress,
            cancellationToken);

        await customerScopeService.SetParentSubscriptionAsync(user.Id, parentUserId, cancellationToken);
        await customerScopeService.SetSubUserSectionsAsync(user.Id, request.AllowedSections, cancellationToken);

        return await MapSubUserAsync(user, cancellationToken);
    }

    public async Task<SubUserDto> UpdateSubUserAsync(Guid parentUserId, Guid subUserId, UpdateSubUserRequest request, string? ipAddress, CancellationToken cancellationToken = default)
    {
        var parent = await EnsureTopLevelCustomerAsync(parentUserId, cancellationToken);
        var user = await GetOwnedSubUserAsync(parentUserId, subUserId, cancellationToken);

        var normalizedEmail = request.Email.Trim();
        var emailLookup = normalizedEmail.ToLowerInvariant();
        if (!string.Equals(user.Email, normalizedEmail, StringComparison.OrdinalIgnoreCase) &&
            await dbContext.Users.AnyAsync(candidate => candidate.Email.ToLower() == emailLookup && candidate.Id != subUserId, cancellationToken))
        {
            throw new InvalidOperationException("users.duplicate_email");
        }

        if (!string.IsNullOrWhiteSpace(request.Password))
        {
            if (request.Password.Length < 12)
            {
                throw new InvalidOperationException("users.password_too_short");
            }

            user.PasswordHash = passwordHasher.HashPassword(user, request.Password);

            // Parolayi BASKASI degistirdi: alt kullanicinin urettigi API anahtarlari da dusurulur.
            // Aksi halde erisimi kesilen kisi anahtariyla MCP uzerinden calismaya devam edebilirdi.
            await apiTokenService.RevokeAllForUserAsync(user.Id, cancellationToken);
        }

        user.Email = normalizedEmail;
        // Askıya alma yalnız BU login'i kapatır; paylaşılan abonelik siteleri etkilenmez (kendi kaynağı yok).
        user.IsActive = request.IsActive;
        user.UpdatedAtUtc = DateTime.UtcNow;

        await auditService.WriteAsync(
            "users.sub_updated",
            "users",
            $"Additional user '{user.UserName}' updated under subscription '{parent.UserName}'. Active='{user.IsActive}'.",
            parentUserId,
            ipAddress,
            cancellationToken);

        await customerScopeService.SetSubUserSectionsAsync(user.Id, request.AllowedSections, cancellationToken);

        return await MapSubUserAsync(user, cancellationToken);
    }

    public async Task DeleteSubUserAsync(Guid parentUserId, Guid subUserId, string? ipAddress, CancellationToken cancellationToken = default)
    {
        var parent = await EnsureTopLevelCustomerAsync(parentUserId, cancellationToken);
        var user = await GetOwnedSubUserAsync(parentUserId, subUserId, cancellationToken);

        // Alt-kullanıcının kendi kaynağı yoktur (cron/DB abonelik kimliğine yazılır) → silmek güvenlidir.
        dbContext.Users.Remove(user);
        await customerScopeService.RemoveUserAsync(subUserId, cancellationToken);

        await auditService.WriteAsync(
            "users.sub_deleted",
            "users",
            $"Additional user '{user.UserName}' deleted from subscription '{parent.UserName}'.",
            parentUserId,
            ipAddress,
            cancellationToken);
    }

    /// <summary>Hedefin GERÇEKTEN aktörün alt-kullanıcısı olduğunu doğrular (başka aboneliğinkine dokunulamaz).</summary>
    private async Task<PanelUser> GetOwnedSubUserAsync(Guid parentUserId, Guid subUserId, CancellationToken cancellationToken)
    {
        var user = await dbContext.Users.FirstOrDefaultAsync(candidate => candidate.Id == subUserId, cancellationToken);
        if (user is null || user.Role != UserRole.Customer ||
            await customerScopeService.GetParentSubscriptionAsync(subUserId, cancellationToken) != parentUserId)
        {
            throw new InvalidOperationException("users.not_found");
        }

        return user;
    }

    private string ResolveOwnerName(Guid? ownerId, IReadOnlyDictionary<Guid, string> nameById)
        => ownerId is { } id && nameById.TryGetValue(id, out var name) ? name : string.Empty;

    private async Task<string> ResolveOwnerNameAsync(Guid? ownerId, CancellationToken cancellationToken)
    {
        if (ownerId is not { } id)
        {
            return string.Empty;
        }

        var owner = await dbContext.Users.FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        return owner?.UserName ?? string.Empty;
    }

    private static UserSummaryDto MapUser(PanelUser user, CustomerScope scope, long diskUsageMb, Guid? ownerId = null, string ownerName = "", int databaseCount = 0, Guid? parentId = null, string parentName = "")
    {
        return new UserSummaryDto
        {
            ParentUserId = parentId,
            ParentName = parentName,
            DatabaseCount = databaseCount,
            Id = user.Id,
            UserName = user.UserName,
            Email = user.Email,
            Role = user.Role.ToString(),
            PreferredLanguage = user.PreferredLanguage,
            IsActive = user.IsActive,
            IsTwoFactorEnabled = user.IsTwoFactorEnabled,
            LastLoginAtUtc = user.LastLoginAtUtc,
            // Süresi geçmiş kilidi "kilitli" göstermeyiz (AuthService de geçmiş kilide takılmaz).
            LockedUntilUtc = user.LockedUntilUtc > DateTime.UtcNow ? user.LockedUntilUtc : null,
            CreatedAtUtc = user.CreatedAtUtc,
            AssignedSites = scope.Sites.ToArray(),
            MaxSites = scope.MaxSites,
            MaxDiskMb = scope.MaxDiskMb,
            MaxDatabases = scope.MaxDatabases,
            MaxMailboxes = scope.MaxMailboxes,
            MaxScheduledTasks = scope.MaxScheduledTasks,
            DiskUsageMb = diskUsageMb,
            OwnerId = ownerId,
            OwnerName = ownerName
        };
    }
}
