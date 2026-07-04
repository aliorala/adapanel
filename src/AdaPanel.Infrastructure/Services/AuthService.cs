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
    JsonFileStore fileStore,
    IOptions<PanelRuntimeOptions> runtimeOptions,
    ILogger<AuthService> logger) : IAuthService
{
    private readonly PanelRuntimeOptions runtimeOptions = runtimeOptions.Value;

    public async Task<LoginResultDto> LoginAsync(LoginRequest request, string? ipAddress, CancellationToken cancellationToken = default)
    {
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

        if (user.LockedUntilUtc.HasValue && user.LockedUntilUtc.Value > DateTime.UtcNow)
        {
            throw new InvalidOperationException("auth.user_locked");
        }

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

            await auditService.WriteAsync("auth.login.failed", "auth", $"Invalid password provided for '{user.UserName}'.", user.Id, ipAddress, cancellationToken);
            await bruteForceProtection.RegisterFailedAttemptAsync(ipAddress, cancellationToken);
            throw new InvalidOperationException("auth.invalid_credentials");
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

        var store = await fileStore.ReadAsync(RecoveryStorePath(userId), new RecoveryCodeStore(), cancellationToken);
        if (store.Codes.Count == 0)
        {
            return false;
        }

        var hash = HashRecoveryCode(normalized);
        if (!store.Codes.Any(stored => string.Equals(stored, hash, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        // Tek kullanımlık: eşleşen kodu kaldır.
        var remaining = store.Codes.Where(stored => !string.Equals(stored, hash, StringComparison.OrdinalIgnoreCase)).ToList();
        await fileStore.WriteAsync(RecoveryStorePath(userId), new RecoveryCodeStore { Codes = remaining }, cancellationToken);
        return true;
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

        var token = GenerateResetToken();
        var store = await fileStore.ReadAsync(PasswordResetStorePath, new PasswordResetStore(), cancellationToken);
        var now = DateTime.UtcNow;
        // Süresi dolmuş kayıtları ve aynı kullanıcının eski taleplerini temizle.
        store.Entries = store.Entries
            .Where(entry => entry.ExpiresUtc > now && entry.UserId != user.Id)
            .ToList();
        store.Entries.Add(new PasswordResetEntry
        {
            UserId = user.Id,
            TokenHash = HashToken(token),
            ExpiresUtc = now.Add(PasswordResetLifetime)
        });
        await fileStore.WriteAsync(PasswordResetStorePath, store, cancellationToken);

        await TrySendResetEmailAsync(user, token, cancellationToken);
        await auditService.WriteAsync("auth.password_reset.requested", "auth", $"Password reset requested for '{user.UserName}'.", user.Id, null, cancellationToken);
    }

    public async Task ResetPasswordAsync(string token, string newPassword, CancellationToken cancellationToken = default)
    {
        var normalizedToken = (token ?? string.Empty).Trim();
        if (normalizedToken.Length == 0)
        {
            throw new AppException("auth.reset_token_invalid", 400, 400);
        }

        var store = await fileStore.ReadAsync(PasswordResetStorePath, new PasswordResetStore(), cancellationToken);
        var hash = HashToken(normalizedToken);
        var now = DateTime.UtcNow;
        var entry = store.Entries.FirstOrDefault(item =>
            string.Equals(item.TokenHash, hash, StringComparison.OrdinalIgnoreCase) && item.ExpiresUtc > now);

        if (entry is null)
        {
            throw new AppException("auth.reset_token_invalid", 400, 400);
        }

        var user = await dbContext.Users.FirstOrDefaultAsync(candidate => candidate.Id == entry.UserId, cancellationToken)
            ?? throw new AppException("auth.reset_token_invalid", 400, 400);

        user.PasswordHash = passwordHasher.HashPassword(user, newPassword);
        user.FailedLoginCount = 0;
        user.LockedUntilUtc = null;
        user.UpdatedAtUtc = now;

        // Token tek kullanımlık: kullanıcının tüm sıfırlama kayıtlarını sil.
        store.Entries = store.Entries.Where(item => item.UserId != entry.UserId).ToList();
        await fileStore.WriteAsync(PasswordResetStorePath, store, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditService.WriteAsync("auth.password_reset.completed", "auth", $"Password reset completed for '{user.UserName}'.", user.Id, null, cancellationToken);
    }

    private async Task TrySendResetEmailAsync(PanelUser user, string token, CancellationToken cancellationToken)
    {
        try
        {
            var preferences = await preferencesService.GetAsync(cancellationToken);
            var securePath = await securityService.GetSecureEntryPathAsync(cancellationToken);
            var panelName = string.IsNullOrWhiteSpace(preferences.PanelName) ? "AdaPanel" : preferences.PanelName;
            var host = string.IsNullOrWhiteSpace(preferences.ServerAddress) ? "<sunucu-adresi>" : preferences.ServerAddress.Trim();
            var portPart = preferences.PreferredHttpsPort == 443 ? string.Empty : $":{preferences.PreferredHttpsPort}";
            var resetUrl = $"https://{host}{portPart}/{securePath}?resetToken={Uri.EscapeDataString(token)}";
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

    private static string GenerateResetToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim()))).ToLowerInvariant();

    private sealed class PasswordResetStore
    {
        public List<PasswordResetEntry> Entries { get; set; } = [];
    }

    private sealed class PasswordResetEntry
    {
        public Guid UserId { get; set; }
        public string TokenHash { get; set; } = string.Empty;
        public DateTime ExpiresUtc { get; set; }
    }

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
