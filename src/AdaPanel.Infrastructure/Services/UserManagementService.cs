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
    IDatabaseOwnershipService databaseOwnership) : IUserManagementService
{
    public async Task<IReadOnlyCollection<UserSummaryDto>> GetUsersAsync(Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var users = await dbContext.Users
            .OrderBy(user => user.UserName)
            .ToListAsync(cancellationToken);

        // Sahip (bayi) adlarını çözmek için TÜM kullanıcılardan ad haritası (filtreden önce).
        var nameById = users.ToDictionary(user => user.Id, user => user.UserName);

        // Bayi (Reseller) yalnızca KENDI sahip olduğu müşterileri görür; admin herkesi görür.
        var actor = await dbContext.Users.FirstOrDefaultAsync(candidate => candidate.Id == actorUserId, cancellationToken);
        if (actor?.Role == UserRole.Reseller)
        {
            var owned = new HashSet<Guid>(await customerScopeService.GetOwnedUserIdsAsync(actorUserId, cancellationToken));
            users = users.Where(user => owned.Contains(user.Id)).ToList();
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
                summaries.Add(MapUser(user, scope, usageMb, ownerId, ResolveOwnerName(ownerId, nameById), dbCount));
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

        var scope = await ApplyScopeAsync(user, request.AssignedSites, request.MaxSites, request.MaxDiskMb, request.MaxDatabases, request.MaxMailboxes, request.MaxScheduledTasks, cancellationToken);
        if (role == UserRole.Customer)
        {
            await customerScopeService.SetOwnerAsync(user.Id, ownerId, cancellationToken);
        }

        if (request.SendWelcomeEmail)
        {
            await TrySendWelcomeEmailAsync(user, request.Password, cancellationToken);
        }

        return MapUser(user, scope, 0, ownerId, await ResolveOwnerNameAsync(ownerId, cancellationToken));
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

        var scope = await ApplyScopeAsync(user, request.AssignedSites, request.MaxSites, request.MaxDiskMb, request.MaxDatabases, request.MaxMailboxes, request.MaxScheduledTasks, cancellationToken);
        if (user.Role == UserRole.Customer)
        {
            await customerScopeService.SetOwnerAsync(user.Id, ownerId, cancellationToken);
        }

        // Aktiflik değiştiyse (askıya al / sürdür) müşterinin atanan sitelerini durdur/başlat.
        await ApplySuspensionStateAsync(user, scope, wasActive, actorUserId, ipAddress, cancellationToken);

        var usageMb = user.Role == UserRole.Customer
            ? await customerScopeService.GetDiskUsageMbAsync(user.Id, cancellationToken)
            : 0;
        var dbCount = user.Role == UserRole.Customer
            ? await databaseOwnership.CountForOwnerAsync(user.Id, cancellationToken)
            : 0;
        var resolvedOwner = user.Role == UserRole.Customer ? ownerId : null;
        return MapUser(user, scope, usageMb, resolvedOwner, await ResolveOwnerNameAsync(resolvedOwner, cancellationToken), dbCount);
    }

    /// <summary>Customer rolünde kapsamı (siteler + limitler) kaydeder; diğer rollerde varsa temizler.</summary>
    private async Task<CustomerScope> ApplyScopeAsync(PanelUser user, string[] assignedSites, int maxSites, int maxDiskMb, int maxDatabases, int maxMailboxes, int maxScheduledTasks, CancellationToken cancellationToken)
    {
        if (user.Role == UserRole.Customer)
        {
            // maxSites > 0 ise atanan site sayısı sınırı aşamaz.
            if (maxSites > 0 && assignedSites.Length > maxSites)
            {
                throw new InvalidOperationException("users.too_many_sites");
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

    private static UserSummaryDto MapUser(PanelUser user, CustomerScope scope, long diskUsageMb, Guid? ownerId = null, string ownerName = "", int databaseCount = 0)
    {
        return new UserSummaryDto
        {
            DatabaseCount = databaseCount,
            Id = user.Id,
            UserName = user.UserName,
            Email = user.Email,
            Role = user.Role.ToString(),
            PreferredLanguage = user.PreferredLanguage,
            IsActive = user.IsActive,
            IsTwoFactorEnabled = user.IsTwoFactorEnabled,
            LastLoginAtUtc = user.LastLoginAtUtc,
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
