using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Auth;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Security;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdaPanel.Infrastructure.Services;

public sealed class AuthService(
    AdaPanelDbContext dbContext,
    IPasswordHasher<PanelUser> passwordHasher,
    ISecretProtector secretProtector,
    ITotpService totpService,
    IAuditService auditService,
    IBruteForceProtectionService bruteForceProtection,
    INotificationService notificationService,
    IPanelPreferencesService preferencesService,
    IPanelSecurityService securityService,
    ICustomerScopeService customerScopeService,
    IApiTokenService apiTokenService,
    JsonFileStore fileStore,
    IOptions<PanelRuntimeOptions> runtimeOptions,
    ILogger<AuthService> logger) : IAuthService
{
    private readonly PanelRuntimeOptions runtimeOptions = runtimeOptions.Value;

    public async Task<LoginResultDto> LoginAsync(LoginRequest request, string? ipAddress, CancellationToken cancellationToken = default)
    {
        // Engelli IP kimlik bilgisi DOĞRULANMADAN reddedilir. Bu denetim olmadan brute-force koruması
        // tamamen Windows Firewall kuralına bağlıydı; kural yazılamadığında (firewall servisi kapalı,
        // grup ilkesiyle yönetilen firewall, yetki yok) engelleme HİÇ gerçekleşmiyor, üstelik ban
        // kaydı var olduğu için bir daha da denenmiyordu — panel "engellendi" derken saldırgan
        // sınırsız deneme yapabiliyordu.
        if (await bruteForceProtection.IsBannedAsync(ipAddress, cancellationToken))
        {
            await auditService.WriteAsync("auth.login.blocked", "auth", $"Login attempt from banned address '{ipAddress}'.", null, ipAddress, cancellationToken);
            throw new InvalidOperationException("auth.ip_banned");
        }

        var lookup = request.UserNameOrEmail.Trim();
        var normalizedLookup = lookup.ToLowerInvariant();

        var user = await dbContext.Users
            .FirstOrDefaultAsync(candidate =>
                candidate.UserName.ToLower() == normalizedLookup ||
                candidate.Email.ToLower() == normalizedLookup,
                cancellationToken);

        if (user is null)
        {
            await auditService.WriteAsync("auth.login.failed", "auth", $"Login failed for unknown user '{lookup}'.", null, ipAddress, cancellationToken);
            await bruteForceProtection.RegisterFailedAttemptAsync(ipAddress, cancellationToken);
            throw new InvalidOperationException("auth.invalid_credentials");
        }

        if (!user.IsActive)
        {
            await auditService.WriteAsync("auth.login.blocked", "auth", $"Inactive user '{user.UserName}' attempted to sign in.", user.Id, ipAddress, cancellationToken);
            throw new InvalidOperationException("auth.user_inactive");
        }

        // HESAP KİLİDİ: kilitliyken parola YİNE DE doğrulanır. DOĞRU parola (ve varsa 2FA) kilidi
        // kaldırıp girişe izin verir; YANLIŞ parola kilidi olduğu gibi bırakır.
        // Neden güvenli: asıl hız sınırlayıcı IP katmanıdır (BruteForceProtectionService varsayılan
        // AÇIK: 15 dk içinde 10 hata -> IP 60 dk Windows Firewall'da bloklanır) ve kilitliyken yapılan
        // her hatalı deneme o sayacı da besler. Parolayı bilen zaten girebilirdi; kilidin tek etkisi
        // MEŞRU kullanıcıyı 15 dk dışarıda tutmaktı ve panelde başka giriş yolu her zaman olmayabilir.
        // TAKAS: dağıtık (çok IP'li) bir tahmin saldırısında hesap-başına 5-deneme/15 dk üst sınırı
        // ortadan kalkar; kalan savunmalar IP engeli, güçlü parola ve iki adımlı doğrulamadır.
        var wasLocked = user.LockedUntilUtc.HasValue && user.LockedUntilUtc.Value > DateTime.UtcNow;

        var passwordResult = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (passwordResult == PasswordVerificationResult.Failed)
        {
            user.FailedLoginCount += 1;
            user.UpdatedAtUtc = DateTime.UtcNow;

            if (user.FailedLoginCount >= 5)
            {
                user.LockedUntilUtc = DateTime.UtcNow.AddMinutes(runtimeOptions.LoginLockoutMinutes);
                user.FailedLoginCount = 0;
            }

            // Sayaç/kilit değişikliğini AÇIKÇA kaydet. Eskiden yalnızca auditService'in AYNI
            // DbContext'i SaveChanges çağırması sayesinde tesadüfen kalıcı oluyordu; denetim kaydı
            // bir gün başka bir yere (dosya/uzak) yazsa kilit sessizce çalışmayı bırakırdı.
            await dbContext.SaveChangesAsync(cancellationToken);

            await auditService.WriteAsync("auth.login.failed", "auth", $"Invalid password provided for '{user.UserName}'.", user.Id, ipAddress, cancellationToken);
            await bruteForceProtection.RegisterFailedAttemptAsync(ipAddress, cancellationToken);

            // Kilitliyken yanlış parola: kullanıcı neden giremediğini bilsin (kilit süresi UZAMAZ).
            throw new InvalidOperationException(wasLocked ? "auth.user_locked" : "auth.invalid_credentials");
        }

        if (user.IsTwoFactorEnabled)
        {
            var protectedSecret = user.TwoFactorSecretProtected;
            if (string.IsNullOrWhiteSpace(protectedSecret))
            {
                throw new InvalidOperationException("auth.two_factor_not_configured");
            }

            if (string.IsNullOrWhiteSpace(request.TwoFactorCode))
            {
                return new LoginResultDto
                {
                    IsAuthenticated = false,
                    RequiresTwoFactor = true
                };
            }

            var secret = secretProtector.Unprotect(protectedSecret);
            var totpValid = totpService.VerifyCode(secret, request.TwoFactorCode);
            // TOTP geçersizse, girilen değeri bir kurtarma kodu olarak dene (tek kullanımlık).
            var recoveryUsed = !totpValid && await TryConsumeRecoveryCodeAsync(user.Id, request.TwoFactorCode, cancellationToken);
            if (!totpValid && !recoveryUsed)
            {
                // İKİNCİ ADIM DA HESAP KİLİDİNİ BESLER.
                //
                // Burada yalnız IP sayacı besleniyordu; hesabın kendi sayacı hiç artmıyordu. Yani
                // parolayı ele geçirmiş bir saldırgan için hesap başına deneme sınırı YOKTU: 6
                // haneli TOTP ve kurtarma kodları sadece IP katmanına karşı korunuyordu. O katman
                // da tek başına yeterli değil — firewall kuralı yazılamadığında engel gerçekleşmiyor
                // (BruteForceProtectionService.ApplyFirewallBlockAsync false döner) ve saldırgan
                // IP değiştirerek de dolaşabilir.
                //
                // Parola yolundaki kilitle AYNI eşik ve süre kullanılıyor; iki yol arasında farklı
                // davranmak için bir sebep yok.
                user.FailedLoginCount += 1;
                user.UpdatedAtUtc = DateTime.UtcNow;
                if (user.FailedLoginCount >= 5)
                {
                    user.LockedUntilUtc = DateTime.UtcNow.AddMinutes(runtimeOptions.LoginLockoutMinutes);
                    user.FailedLoginCount = 0;
                }

                await dbContext.SaveChangesAsync(cancellationToken);

                await auditService.WriteAsync("auth.2fa.failed", "auth", $"Invalid two-factor code provided for '{user.UserName}'.", user.Id, ipAddress, cancellationToken);
                await bruteForceProtection.RegisterFailedAttemptAsync(ipAddress, cancellationToken);
                throw new InvalidOperationException("auth.invalid_two_factor_code");
            }

            if (recoveryUsed)
            {
                await auditService.WriteAsync("auth.2fa.recovery_used", "auth", $"Recovery code used to sign in for '{user.UserName}'.", user.Id, ipAddress, cancellationToken);
            }
        }

        user.FailedLoginCount = 0;
        user.LockedUntilUtc = null;
        user.LastLoginAtUtc = DateTime.UtcNow;
        user.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        // Kilit doğru kimlik bilgisiyle kaldırıldıysa bunu DENETİM KAYDINA yaz: "kilitliydi ama
        // doğru parolayla girildi" izlenebilir kalsın (güvenlik incelemesi için önemli).
        if (wasLocked)
        {
            await auditService.WriteAsync(
                "auth.login.lock_cleared",
                "auth",
                $"Account lock cleared for '{user.UserName}' by a successful sign-in.",
                user.Id,
                ipAddress,
                cancellationToken);
        }

        await bruteForceProtection.RegisterSuccessAsync(ipAddress, cancellationToken);
        await auditService.WriteAsync("auth.login.succeeded", "auth", $"User '{user.UserName}' signed in.", user.Id, ipAddress, cancellationToken);
        logger.LogInformation("User {UserName} signed in to AdaPanel", user.UserName);

        return new LoginResultDto
        {
            IsAuthenticated = true,
            RequiresTwoFactor = false,
            User = MapCurrentUser(user)
        };
    }

    public async Task<CurrentSessionDto> GetCurrentSessionAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        var requiresSetupWizard = !await dbContext.Users.AnyAsync(user => user.Role == UserRole.Administrator, cancellationToken);

        if (!(principal.Identity?.IsAuthenticated ?? false))
        {
            return new CurrentSessionDto
            {
                IsAuthenticated = false,
                RequiresSetupWizard = requiresSetupWizard
            };
        }

        var userIdClaim = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdClaim, out var userId))
        {
            return new CurrentSessionDto
            {
                IsAuthenticated = false,
                RequiresSetupWizard = requiresSetupWizard
            };
        }

        var user = await dbContext.Users.FirstOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken);
        if (user is null || !user.IsActive)
        {
            return new CurrentSessionDto
            {
                IsAuthenticated = false,
                RequiresSetupWizard = requiresSetupWizard
            };
        }

        var impersonatorName = principal.FindFirstValue("impersonator_name");

        // Abonelik altı ek kullanıcı bilgisi: frontend menüyü izinlere göre filtreler (asıl
        // dayatma backend'de RequireSubUserSection ile yapılır; burası yalnız UX içindir).
        var isSubUser = false;
        string[]? allowedSections = null;
        if (user.Role == UserRole.Customer &&
            await customerScopeService.GetParentSubscriptionAsync(user.Id, cancellationToken) is not null)
        {
            isSubUser = true;
            allowedSections = (await customerScopeService.GetSubUserSectionsAsync(user.Id, cancellationToken))?.ToArray();
        }

        return new CurrentSessionDto
        {
            IsAuthenticated = true,
            RequiresSetupWizard = requiresSetupWizard,
            User = MapCurrentUser(user),
            IsImpersonating = !string.IsNullOrWhiteSpace(impersonatorName),
            ImpersonatorName = impersonatorName,
            IsSubUser = isSubUser,
            AllowedSections = allowedSections
        };
    }

    public async Task<CurrentUserDto> GetUserByIdAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await GetUserAsync(userId, cancellationToken);
        return MapCurrentUser(user);
    }

    public async Task<TwoFactorSetupDto> BeginTwoFactorSetupAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await GetUserAsync(userId, cancellationToken);
        var secret = totpService.GenerateSecret();

        user.PendingTwoFactorSecretProtected = secretProtector.Protect(secret);
        user.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        return new TwoFactorSetupDto
        {
            IsTwoFactorEnabled = user.IsTwoFactorEnabled,
            HasPendingSetup = true,
            ManualEntryKey = secret,
            ProvisioningUri = totpService.BuildProvisioningUri(runtimeOptions.TotpIssuer, user.Email, secret)
        };
    }

    public async Task<TwoFactorConfirmDto> ConfirmTwoFactorSetupAsync(Guid userId, string code, string? ipAddress, CancellationToken cancellationToken = default)
    {
        var user = await GetUserAsync(userId, cancellationToken);
        if (string.IsNullOrWhiteSpace(user.PendingTwoFactorSecretProtected))
        {
            throw new InvalidOperationException("auth.two_factor_setup_not_started");
        }

        var secret = secretProtector.Unprotect(user.PendingTwoFactorSecretProtected);
        if (!totpService.VerifyCode(secret, code))
        {
            throw new InvalidOperationException("auth.invalid_two_factor_code");
        }

        user.TwoFactorSecretProtected = user.PendingTwoFactorSecretProtected;
        user.PendingTwoFactorSecretProtected = null;
        user.IsTwoFactorEnabled = true;
        user.UpdatedAtUtc = DateTime.UtcNow;

        var recoveryCodes = await GenerateAndStoreRecoveryCodesAsync(user.Id, cancellationToken);

        await auditService.WriteAsync("auth.2fa.enabled", "auth", $"Two-factor authentication enabled for '{user.UserName}'.", user.Id, ipAddress, cancellationToken);
        return new TwoFactorConfirmDto
        {
            User = MapCurrentUser(user),
            RecoveryCodes = recoveryCodes
        };
    }

    public async Task<RecoveryCodesDto> RegenerateRecoveryCodesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await GetUserAsync(userId, cancellationToken);
        if (!user.IsTwoFactorEnabled)
        {
            throw new InvalidOperationException("auth.two_factor_not_configured");
        }

        var codes = await GenerateAndStoreRecoveryCodesAsync(userId, cancellationToken);
        return new RecoveryCodesDto { Codes = codes };
    }

    public async Task<CurrentUserDto> DisableTwoFactorAsync(Guid userId, string password, string? ipAddress, CancellationToken cancellationToken = default)
    {
        var user = await GetUserAsync(userId, cancellationToken);
        var passwordResult = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (passwordResult == PasswordVerificationResult.Failed)
        {
            throw new InvalidOperationException("auth.invalid_credentials");
        }

        user.IsTwoFactorEnabled = false;
        user.TwoFactorSecretProtected = null;
        user.PendingTwoFactorSecretProtected = null;
        user.UpdatedAtUtc = DateTime.UtcNow;

        await fileStore.WriteAsync(RecoveryStorePath(user.Id), new RecoveryCodeStore(), cancellationToken);

        await auditService.WriteAsync("auth.2fa.disabled", "auth", $"Two-factor authentication disabled for '{user.UserName}'.", user.Id, ipAddress, cancellationToken);
        return MapCurrentUser(user);
    }

    public async Task<ChangePasswordResultDto> ChangePasswordAsync(
        Guid userId,
        ChangePasswordRequest request,
        string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        var user = await GetUserAsync(userId, cancellationToken);

        // MEVCUT parola şart. Aksi hâlde çalınmış bir oturum çerezi tek başına hesabı kalıcı olarak
        // devralmaya yeterdi: saldırgan parolayı değiştirir, gerçek sahibi dışarıda kalırdı.
        var verification = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword);
        if (verification == PasswordVerificationResult.Failed)
        {
            throw new InvalidOperationException("auth.invalid_credentials");
        }

        // Aynı parolayı "değiştirmek" işlemi anlamsız kılar ve kullanıcıya yanlış bir güvenlik
        // duygusu verir — özellikle sızıntı şüphesiyle geldiyse.
        if (passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.NewPassword) != PasswordVerificationResult.Failed)
        {
            throw new InvalidOperationException("auth.password_unchanged");
        }

        // Uzunluk kuralı TEK kaynaktan: veri açıklaması yalnız MVC yolunda çalışır, bu servis
        // MCP araçlarından ve iç akışlardan da çağrılabiliyor (bkz. PasswordPolicy).
        PasswordPolicy.Ensure(request.NewPassword);

        user.PasswordHash = passwordHasher.HashPassword(user, request.NewPassword);
        user.UpdatedAtUtc = DateTime.UtcNow;
        // Parola değişimi kilidi de açar: kullanıcı zaten kimliğini mevcut parolayla kanıtladı.
        user.FailedLoginCount = 0;
        user.LockedUntilUtc = null;
        await dbContext.SaveChangesAsync(cancellationToken);

        // Anahtarlar paroladan BAĞIMSIZ kimlik bilgileridir. Parola değiştiren kişi çoğu zaman
        // "erişimi kesmek" istediği için gelir; anahtar ayakta kalırsa MCP üzerinden erişim sürer ve
        // kullanıcı bunu fark etmez. Kaç anahtarın düştüğü yanıtta bildirilir, sessizce kesilmez.
        var revoked = await apiTokenService.RevokeAllForUserAsync(user.Id, cancellationToken);

        await auditService.WriteAsync(
            "auth.password_changed",
            "auth",
            $"Password changed by '{user.UserName}'. {revoked} API token(s) revoked.",
            user.Id,
            ipAddress,
            cancellationToken);

        return new ChangePasswordResultDto { RevokedApiTokens = revoked };
    }

    public async Task<SetUserPasswordResultDto> SetPasswordForUserAsync(
        Guid targetUserId,
        SetUserPasswordRequest request,
        Guid actorUserId,
        string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        // KENDİ hesabı bu uçtan geçemez. Aksi hâlde çalınmış bir yönetici çerezi, mevcut parolayı
        // hiç bilmeden hesabı kalıcı olarak devralabilirdi — ChangePasswordAsync'teki mevcut-parola
        // koşulu, yanında böyle bir uç durdukça hiçbir şey korumaz.
        if (targetUserId == actorUserId)
        {
            throw new AppException("users.forbidden", 403, 403);
        }

        var erisim = await ManagedUserGuard.EnsureManagedAsync(
            dbContext, customerScopeService, actorUserId, targetUserId, cancellationToken);
        var target = erisim.Target;

        PasswordPolicy.Ensure(request.NewPassword);

        target.PasswordHash = passwordHasher.HashPassword(target, request.NewPassword);
        target.UpdatedAtUtc = DateTime.UtcNow;
        // Kilit de açılır: yönetici parolayı sıfırlarken amacı kullanıcıyı içeri sokmaktır, hatalı
        // denemelerden kalan kilidi ayrıca temizlemek zorunda kalmamalı (ResetPasswordAsync da böyle).
        target.FailedLoginCount = 0;
        target.LockedUntilUtc = null;

        // AÇIKÇA kaydet. Denetim kaydı aynı DbContext'i kullandığı için burada SaveChanges çağırmasa
        // da yazım tesadüfen kalıcı olurdu; kayıt bir gün başka bir yere taşınırsa parola sessizce
        // değişmemiş olurdu (aynı gerekçe LoginAsync'te de yazılı).
        await dbContext.SaveChangesAsync(cancellationToken);

        // ÇEREZ oturumları ayrıca düşürülmüyor çünkü zaten düşüyor: CookieSessionValidator çerezdeki
        // kimlik damgasını parola KARMASINDAN türetir, karma değişince eski çerez en geç bir dakika
        // içinde reddedilir. İkinci bir mekanizma yazmak, ikisinin birbirinden ayrı düşmesi demekti.
        //
        // API anahtarları paroladan BAĞIMSIZ kimlik bilgileridir; düşürülmezlerse parolası sıfırlanan
        // kişi MCP/REST üzerinden erişimini sürdürür.
        var revoked = await apiTokenService.RevokeAllForUserAsync(target.Id, cancellationToken);

        // Denetim kaydına PAROLA YAZILMAZ: kayıt, korumaya çalıştığı sırrın ikinci deposu olurdu.
        await auditService.WriteAsync(
            "auth.password_set_by_admin",
            "auth",
            $"Password set for '{target.UserName}' by '{erisim.Actor?.UserName}'. {revoked} API token(s) revoked.",
            actorUserId,
            ipAddress,
            cancellationToken);

        return new SetUserPasswordResultDto { RevokedApiTokens = revoked };
    }

    // ---- Tek kullanımlık giriş bağlantısı (faturalama sisteminin "Panele git" düğmesi) ----

    /// <summary>
    /// Belirteç deposu. Disiplinin tamamı parola sıfırlamayla ORTAK bir yerden gelir
    /// (<see cref="SingleUseTokenStore"/>): dosyada yalnız SHA-256 özeti durur, bulma ve tüketme
    /// dosya kilidi altında tek parçadır.
    /// </summary>
    private const string SsoTokenStorePath = "config/sso-tokens.json";

    /// <summary>
    /// Belirtecin ömrü.
    /// <para>
    /// DAKİKALARLA ölçülür çünkü belirteç URL'de gider: panel istek günlüğüne, önündeki ters vekilin
    /// günlüğüne ve müşterinin tarayıcı geçmişine DÜŞER. Bunu ortadan kaldıramayız (tarayıcı özel
    /// başlık gönderemez), ama değersizleştirebiliriz — belirteç tek kullanımlıktır ve günlüğü
    /// sonradan okuyan biri çoktan tüketilmiş, süresi geçmiş bir dize bulur. İki dakika, faturalama
    /// sisteminin yönlendirmesi için fazlasıyla yeterli; insanın bağlantıyı kopyalayıp saklaması için
    /// değil.
    /// </para>
    /// </summary>
    private static readonly TimeSpan SsoTokenLifetime = TimeSpan.FromMinutes(2);

    public async Task<SsoLinkDto> CreateSsoLinkAsync(
        Guid targetUserId,
        Guid actorUserId,
        string? requestHost,
        string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        var erisim = await ManagedUserGuard.EnsureManagedAsync(
            dbContext, customerScopeService, actorUserId, targetUserId, cancellationToken);
        var target = erisim.Target;

        // PASİF hesap için bağlantı ÜRETİLMEZ: normal giriş yolu (LoginAsync) pasif kullanıcıyı
        // "auth.user_inactive" ile reddediyor. Burada üretmek, hesabı kapatmanın etrafından dolaşan
        // ikinci bir giriş kapısı açmak olurdu.
        //
        // ASKIDAKİ ABONELİK İÇİN İSE BAĞLANTI ÜRETİLİR ve bu bilinçlidir: panelde askı, girişi değil
        // SİTELERİ kapatır (ISubscriptionEnforcementService belgesi — müşteri panele girip neden
        // askıda olduğunu, kotasını ve faturasını görebilmeli, çözüm orada). Tam da ödemesi geciken
        // müşteri için "Panele git" düğmesini ölü bırakmak, askının amacına ters düşerdi.
        if (!target.IsActive)
        {
            throw new AppException("auth.user_inactive", 403, 403);
        }

        // Belirteç üretimi + depolama ORTAK yerden: aynı kullanıcının önceki bağlantısı da burada
        // geçersizleşir (parola sıfırlamayla aynı kural).
        var (token, expiresAt) = await SingleUseTokenStore.IssueAsync(
            fileStore, SsoTokenStorePath, target.Id, SsoTokenLifetime, cancellationToken);

        var preferences = await preferencesService.GetAsync(cancellationToken);

        // Bağlantı GİZLİ GİRİŞ YOLUNU taşımaz: kullanım ucu (/api/v1/auth/sso) gizli yol başlığından
        // muaftır çünkü onu TARAYICI gezer (aynı gerekçe /api/v1/dbtools için de yazılı). Belirtecin
        // kendisi 256 bitlik, tek kullanımlık ve dakikalık ömürlü; IP kısıtlaması da bu uçtan önce
        // uygulanmaya devam eder.
        var url = PanelAddress.BuildBaseUrl(preferences.ServerAddress, preferences.PreferredHttpsPort, requestHost)
            + $"/api/v1/auth/sso?token={Uri.EscapeDataString(token)}";

        // BELİRTEÇ KAYDA YAZILMAZ; yalnız kimin kimin adına, ne zamana kadar geçerli bir bağlantı
        // ürettiği yazılır.
        await auditService.WriteAsync(
            "auth.sso_link.created",
            "auth",
            $"One-time sign-in link created for '{target.UserName}' by '{erisim.Actor?.UserName}'; valid until {expiresAt:O}.",
            actorUserId,
            ipAddress,
            cancellationToken);

        return new SsoLinkDto { Url = url, ExpiresAtUtc = expiresAt };
    }

    public async Task<CurrentUserDto?> TryRedeemSsoTokenAsync(
        string token,
        string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        // Bulma + TÜKETME tek parça olarak ortak depodan yapılır (SingleUseTokenStore): ikinci
        // kullanım, süre aşımı ve eşzamanlılık kararlarının tamamı orada, tek yerde.
        var sahipId = await SingleUseTokenStore.TryConsumeAsync(fileStore, SsoTokenStorePath, token, cancellationToken);

        if (sahipId is not { } tokenUserId)
        {
            // Geçersiz / süresi geçmiş / İKİNCİ KEZ kullanılmış. Üçü de aynı yanıtı alır; ayırmak
            // yalnız deneme yapana bilgi verirdi.
            await auditService.WriteAsync(
                "auth.sso_link.rejected",
                "auth",
                "A one-time sign-in link was rejected: unknown, expired or already used.",
                null,
                ipAddress,
                cancellationToken);
            return null;
        }

        var user = await dbContext.Users.FirstOrDefaultAsync(candidate => candidate.Id == tokenUserId, cancellationToken);
        if (user is null || !user.IsActive)
        {
            // Bağlantı üretildikten SONRA hesap kapatılmış olabilir; belirteç o kararı geçersiz kılmamalı.
            await auditService.WriteAsync(
                "auth.sso_link.rejected",
                "auth",
                "A one-time sign-in link was rejected: the account is deleted or inactive.",
                user?.Id,
                ipAddress,
                cancellationToken);
            return null;
        }

        user.LastLoginAtUtc = now;
        user.UpdatedAtUtc = now;
        await dbContext.SaveChangesAsync(cancellationToken);

        // İKİ ADIMLI DOĞRULAMA BU YOLDA SORULMAZ ve bu bilinçli bir karardır: belirteci üretebilen
        // kimlik zaten yönetici (ya da müşterinin bayisi) yetkisindedir ve aynı hesaba kimliğe bürünme
        // ya da parola belirleme ile de ulaşabilir. İkinci adımı burada sormak, hiçbir saldırganı
        // durdurmayıp yalnız meşru "Panele git" düğmesini bozardı. İz denetim kaydında kalır.
        await auditService.WriteAsync(
            "auth.sso_link.redeemed",
            "auth",
            $"One-time sign-in link redeemed for '{user.UserName}'.",
            user.Id,
            ipAddress,
            cancellationToken);

        return MapCurrentUser(user);
    }

    private async Task<IReadOnlyCollection<string>> GenerateAndStoreRecoveryCodesAsync(Guid userId, CancellationToken cancellationToken)
    {
        var codes = new List<string>(RecoveryCodeCount);
        var hashes = new List<string>(RecoveryCodeCount);
        for (var index = 0; index < RecoveryCodeCount; index++)
        {
            var code = GenerateRecoveryCode();
            codes.Add(code);
            hashes.Add(HashRecoveryCode(code));
        }

        await fileStore.WriteAsync(RecoveryStorePath(userId), new RecoveryCodeStore { Codes = hashes }, cancellationToken);
        return codes;
    }

    private async Task<bool> TryConsumeRecoveryCodeAsync(Guid userId, string? candidate, CancellationToken cancellationToken)
    {
        var normalized = NormalizeRecoveryCode(candidate);
        if (normalized.Length == 0)
        {
            return false;
        }

        var hash = HashRecoveryCode(normalized);

        // TEK KULLANIMLIK GÜVENCESİ KİLİT İSTER. Önceden oku → eşleştir → yaz sırası kilitsizdi:
        // aynı kodu taşıyan iki eşzamanlı istek, ikisi de dosyayı yazılmadan önce okur, ikisi de
        // kodu listede bulur ve İKİSİ DE true döner. Kurtarma kodu TOTP'yi tamamen atladığı için
        // bu, iki adımlı doğrulamanın eşzamanlılık altında delinmesi demekti.
        //
        // UpdateAsync yol başına kilit tutar; okuma, karar ve yazma tek parçadır.
        var tuketildi = false;
        await fileStore.UpdateAsync(
            RecoveryStorePath(userId),
            new RecoveryCodeStore(),
            mevcut =>
            {
                if (!mevcut.Codes.Any(stored => string.Equals(stored, hash, StringComparison.OrdinalIgnoreCase)))
                {
                    return mevcut;
                }

                tuketildi = true;
                return new RecoveryCodeStore
                {
                    Codes = mevcut.Codes
                        .Where(stored => !string.Equals(stored, hash, StringComparison.OrdinalIgnoreCase))
                        .ToList()
                };
            },
            cancellationToken);

        return tuketildi;
    }

    // ---- Parola sıfırlama ----

    private const string PasswordResetStorePath = "config/password-resets.json";
    private static readonly TimeSpan PasswordResetLifetime = TimeSpan.FromHours(1);

    public async Task RequestPasswordResetAsync(string email, CancellationToken cancellationToken = default)
    {
        var normalized = (email ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length == 0)
        {
            return;
        }

        var user = await dbContext.Users.FirstOrDefaultAsync(candidate => candidate.Email.ToLower() == normalized, cancellationToken);
        // Kullanıcı numaralandırmasını engelle: bulunmasa da sessizce dön.
        if (user is null || !user.IsActive)
        {
            return;
        }

        // SMTP yoksa link gönderilemez; token üretmenin anlamı yok.
        if (!await notificationService.IsEmailConfiguredAsync(cancellationToken))
        {
            logger.LogWarning("Password reset requested for {Email} but SMTP is not configured.", user.Email);
            return;
        }

        // TEK DOSYA, TÜM KULLANICILAR ORTAK ve kilitsiz oku-değiştir-yaz iki yönde de bozulurdu
        // (A'nın yeni token'ı kaybolur ya da B'nin TÜKETİLMİŞ token'ı geri dirilir). Kilit,
        // özetleme ve eski kaydın temizlenmesi ortak depoda (SingleUseTokenStore) — giriş
        // bağlantısıyla aynı yerde, tek uygulama.
        var (token, _) = await SingleUseTokenStore.IssueAsync(
            fileStore, PasswordResetStorePath, user.Id, PasswordResetLifetime, cancellationToken);

        await TrySendResetEmailAsync(user, token, cancellationToken);
        await auditService.WriteAsync("auth.password_reset.requested", "auth", $"Password reset requested for '{user.UserName}'.", user.Id, null, cancellationToken);
    }

    public async Task ResetPasswordAsync(string token, string newPassword, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        // Token'ı BULMAK ve TÜKETMEK tek parça olmalı; ayrı yapılırsa aynı link iki kez
        // kullanılabilir. Kullanıcı bulunamazsa aşağıda hata atılır — token yine de tüketilmiştir,
        // bu bilinçli: geçersiz bir token'ı ayakta tutmanın bir yararı yok. Uygulaması ortak depoda.
        var sahipId = await SingleUseTokenStore.TryConsumeAsync(fileStore, PasswordResetStorePath, token, cancellationToken);
        if (sahipId is not { } tokenUserId)
        {
            throw new AppException("auth.reset_token_invalid", 400, 400);
        }

        var user = await dbContext.Users.FirstOrDefaultAsync(candidate => candidate.Id == tokenUserId, cancellationToken)
            ?? throw new AppException("auth.reset_token_invalid", 400, 400);

        // Uzunluk kuralı TEK kaynaktan (veri açıklaması yalnız MVC yolunda çalışır).
        PasswordPolicy.Ensure(newPassword);

        user.PasswordHash = passwordHasher.HashPassword(user, newPassword);
        user.FailedLoginCount = 0;
        user.LockedUntilUtc = null;
        user.UpdatedAtUtc = now;

        // Token yukarıda ATOMİK olarak tüketildi; burada ayrıca silmeye gerek yok.
        await dbContext.SaveChangesAsync(cancellationToken);

        // Parola KURTARILDI: hesabın ele geçirilmiş olabileceği varsayılır. Anahtarlar parolaya bağlı
        // olmayan ayrı kimlik bilgileridir; düşürülmezlerse saldırgan parola sıfırlansa bile MCP
        // üzerinden erişimini sürdürür. Kullanıcı yeni anahtarı panelden üretir.
        var revokedTokens = await apiTokenService.RevokeAllForUserAsync(user.Id, cancellationToken);

        await auditService.WriteAsync("auth.password_reset.completed", "auth", $"Password reset completed for '{user.UserName}'.", user.Id, null, cancellationToken);

        if (revokedTokens > 0)
        {
            await auditService.WriteAsync(
                "apitoken.revoked_on_reset",
                "auth",
                $"{revokedTokens} API token(s) revoked because the password of '{user.UserName}' was reset.",
                user.Id,
                null,
                cancellationToken);
        }
    }

    private async Task TrySendResetEmailAsync(PanelUser user, string token, CancellationToken cancellationToken)
    {
        try
        {
            var preferences = await preferencesService.GetAsync(cancellationToken);
            var securePath = await securityService.GetSecureEntryPathAsync(cancellationToken);
            var panelName = string.IsNullOrWhiteSpace(preferences.PanelName) ? "AdaPanel" : preferences.PanelName;
            // Adres kurgusu TEK yerde (PanelAddress): aynı üç satır hoş geldin e-postasında da vardı.
            var resetUrl = PanelAddress.BuildEntryUrl(preferences.ServerAddress, preferences.PreferredHttpsPort, securePath)
                + $"?resetToken={Uri.EscapeDataString(token)}";
            var isTurkish = user.PreferredLanguage.StartsWith("tr", StringComparison.OrdinalIgnoreCase);

            var subject = isTurkish ? $"{panelName} parola sıfırlama" : $"{panelName} password reset";
            var body = isTurkish
                ? $"""
                  Merhaba {user.UserName},

                  Parolanızı sıfırlamak için aşağıdaki bağlantıyı açın (1 saat geçerlidir):
                  {resetUrl}

                  Bu talebi siz yapmadıysanız bu e-postayı yok sayabilirsiniz; parolanız değişmez.
                  """
                : $"""
                  Hello {user.UserName},

                  Open the link below to reset your password (valid for 1 hour):
                  {resetUrl}

                  If you did not request this, you can ignore this email; your password stays unchanged.
                  """;

            await notificationService.SendEmailToAsync(user.Email, subject, body, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Password reset email could not be sent to {Email}.", user.Email);
        }
    }

    // Belirteç üretimi, özetleme ve depo biçimi SingleUseTokenStore'a taşındı: parola sıfırlama ile
    // tek tıkla giriş bağlantısı aynı disiplini paylaşıyor, iki kopya bırakılmadı.

    private static string RecoveryStorePath(Guid userId) => $"config/2fa/{userId:N}.json";

    private static string GenerateRecoveryCode()
    {
        // Belirsizlik yaratan karakterler (0/O, 1/I) çıkarıldı. Format: XXXXX-XXXXX
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var bytes = RandomNumberGenerator.GetBytes(10);
        var builder = new StringBuilder(11);
        for (var index = 0; index < bytes.Length; index++)
        {
            if (index == 5)
            {
                builder.Append('-');
            }

            builder.Append(alphabet[bytes[index] % alphabet.Length]);
        }

        return builder.ToString();
    }

    private static string NormalizeRecoveryCode(string? candidate) =>
        new((candidate ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string HashRecoveryCode(string code)
    {
        var normalized = NormalizeRecoveryCode(code);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private const int RecoveryCodeCount = 10;

    private sealed class RecoveryCodeStore
    {
        public List<string> Codes { get; set; } = [];
    }

    private async Task<PanelUser> GetUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await dbContext.Users.FirstOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken);
        if (user is null)
        {
            throw new InvalidOperationException("auth.user_not_found");
        }

        return user;
    }

    private static CurrentUserDto MapCurrentUser(PanelUser user)
    {
        return new CurrentUserDto
        {
            Id = user.Id,
            UserName = user.UserName,
            Email = user.Email,
            Role = user.Role.ToString(),
            PreferredLanguage = user.PreferredLanguage,
            IsTwoFactorEnabled = user.IsTwoFactorEnabled,
            IsActive = user.IsActive,
            LastLoginAtUtc = user.LastLoginAtUtc
        };
    }
}
