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
    IPanelPreferencesService preferencesService,
    IArkaPlanIsKuyrugu arkaPlanIsKuyrugu,
    ICustomerScopeService customerScopeService,
    IApiTokenService apiTokenService,
    JsonFileStore fileStore,
    IOptions<PanelRuntimeOptions> runtimeOptions,
    ILogger<AuthService> logger) : IAuthService
{
    private readonly PanelRuntimeOptions runtimeOptions = runtimeOptions.Value;

    /// <summary>
    /// "Kullanıcı yok" yolunda doğrulanan SAHTE karmanın sahibi. Veritabanına HİÇ yazılmaz; yalnız
    /// karma algoritmasının istediği bir nesne olduğu için var (ASP.NET Identity karmayı kullanıcı
    /// nesnesine bağlamaz, ama imza onu ister).
    /// </summary>
    private static readonly PanelUser SahteKullanici = new()
    {
        Id = Guid.Empty,
        UserName = "-",
        Email = "-"
    };

    /// <summary>
    /// Süreç ömrü boyunca tek kez üretilen sahte karma. Her istekte yeniden ÜRETİLSEYDİ maliyet
    /// doğrulamanın iki katı olur ve zaman farkı bu sefer ters yönde ölçülebilir hâle gelirdi.
    /// Yarışta iki kez üretilmesi zararsızdır: değer yalnız atılacak bir doğrulamada kullanılıyor.
    /// </summary>
    private static string? sahteKarma;

    private static string SahteKarmaAl(IPasswordHasher<PanelUser> passwordHasher) =>
        sahteKarma ??= passwordHasher.HashPassword(SahteKullanici, "AdaPanel-zaman-esitleme-degeri");

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
            // KULLANICI YOKKEN DE PAROLA KARMASI HESAPLANIR ("sahte" karmaya karşı).
            //
            // ÖLÇÜLEN AÇIK: bu yol eskiden anında dönüyordu, kullanıcı varken ise PBKDF2 doğrulaması
            // yapılıyordu (ASP.NET Identity v3, on binlerce yineleme = onlarca milisaniye). Aradaki
            // fark ağ üzerinden bile rahatça ölçülebilir; yani panel, hiçbir parola bilmeyen birine
            // "bu kullanıcı adı var mı" sorusunu YANIT SÜRESİYLE cevaplıyordu. Kullanıcı adlarını
            // toplamak, hedefli parola denemesinin ve oltalamanın ilk adımıdır.
            //
            // Çözüm, iki yolun AYNI işi yapması: burada da bir karma doğrulanır (sonucu atılır).
            // Sahte karma bir kez üretilip süreç boyunca saklanır — her istekte yeniden üretmek
            // doğrulamanın İKİ katı iş demekti ve bu sefer fark ters yönde ölçülebilir olurdu.
            passwordHasher.VerifyHashedPassword(SahteKullanici, SahteKarmaAl(passwordHasher), request.Password);

            await auditService.WriteAsync("auth.login.failed", "auth", $"Login failed for unknown user '{lookup}'.", null, ipAddress, cancellationToken);
            await bruteForceProtection.RegisterFailedAttemptAsync(ipAddress, cancellationToken);
            throw new InvalidOperationException("auth.invalid_credentials");
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

            // YANLIŞ PAROLADA HER ZAMAN AYNI YANIT — "auth.user_locked" ARTIK DÖNMÜYOR.
            //
            // İki sebep, ikisi de ölçülmüş:
            // (1) NUMARALANDIRMA: "kilitli" yanıtı yalnız VAR OLAN bir hesap için üretilebilirdi.
            //     Saldırgan bir adı beş kez yanlış deneyip altıncıda mesajın değişip değişmediğine
            //     bakarak, hiçbir parola bilmeden hesabın varlığını doğrulayabiliyordu.
            // (2) MESAJ YANLIŞTI: bu panelde kilit girişi ENGELLEMEZ — doğru parola kilidi kaldırıp
            //     içeri alır (yukarıdaki gerekçeye bakın). Yani "hesabınız 15 dakika kilitli" diyen
            //     ekran, kullanıcıya beklemesi gerektiğini söylüyordu; oysa doğru parolayla o an
            //     girebilirdi. Doğru olmayan bir uyarı, olmayan bir uyarıdan kötüdür.
            // Kilidin izi kaybolmuyor: denetim kaydında auth.login.failed ve kilit kalkarken
            // auth.login.lock_cleared yazılıyor.
            throw new InvalidOperationException("auth.invalid_credentials");
        }

        // HESAP PASİF Mİ — PAROLA DOĞRULANDIKTAN SONRA.
        //
        // Bu denetim eskiden paroladan ÖNCEYDİ ve doğrudan bir numaralandırma kapısıydı: herhangi
        // biri, parolayı hiç bilmeden yalnız bir kullanıcı adı göndererek "auth.user_inactive"
        // yanıtını alıyor ve hesabın VAR OLDUĞUNU öğreniyordu. Şimdi yanıt yalnız parolayı (ve
        // aşağıda ikinci adımı değil — pasif hesapta oraya hiç gelinmez) DOĞRU bilen kişiye
        // gösteriliyor: yani hesabın sahibine. Bilmeyen için yanıt "auth.invalid_credentials"tır.
        //
        // Mesajın korunması bilinçli: hesabı kapatılan müşteri "parolam mı yanlış" diye sıfırlama
        // e-postasıyla uğraşmasın, durumu görsün.
        if (!user.IsActive)
        {
            await auditService.WriteAsync("auth.login.blocked", "auth", $"Inactive user '{user.UserName}' attempted to sign in.", user.Id, ipAddress, cancellationToken);
            throw new InvalidOperationException("auth.user_inactive");
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

            // KOD DOĞRU OLMAK YETMEZ, DAHA ÖNCE KULLANILMAMIŞ DA OLMALI.
            //
            // TOTP kodu 30 saniyelik bir adımdan üretilir ve ±1 adım tolerans yüzünden AYNI KOD 90
            // saniye geçerlidir. Eskiden yalnız "geçerli mi" soruluyordu; yani bir kimlik avı
            // sayfasına girilen ya da omuz üstünden okunan kodla saldırgan İKİNCİ bir oturum
            // açabilirdi. Adım tüketilemiyorsa (aynı ya da daha eski bir adım) kod GEÇERSİZ sayılır
            // ve aşağıdaki başarısızlık yolu — kilit sayacı, IP sayacı, denetim kaydı — aynen işler.
            var totpStep = totpService.VerifyCode(secret, request.TwoFactorCode);
            var totpValid = totpStep is { } adim &&
                await TwoFactorRecoveryCodes.TryConsumeTotpStepAsync(fileStore, user.Id, adim, cancellationToken);

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

        // KURULUM ADIMI DA TÜKETİR. Aynı kodun hem 2FA'yı açmaya hem de hemen ardından giriş
        // yapmaya yaraması, tekrar oynatma korumasını daha doğduğu anda delerdi: iki yol tek
        // sayacı paylaşmalı.
        var step = totpService.VerifyCode(secret, code);
        if (step is not { } adim ||
            !await TwoFactorRecoveryCodes.TryConsumeTotpStepAsync(fileStore, user.Id, adim, cancellationToken))
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

        await TwoFactorRecoveryCodes.ClearAsync(fileStore, user.Id, cancellationToken);

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

        // Yazma, kilit açma, anahtar düşürme ve uzunluk kuralı TEK gövdeden geçer
        // (bkz. PasswordAssignment): parolayı yazan her yol aynı üçlüyü uygulamak zorundadır ve
        // burada elle tekrarlanan kopya, üçüncü bir çağrı yolu eklendiğinde eksik kalırdı.
        var revoked = await PasswordAssignment.ApplyAsync(
            dbContext, passwordHasher, apiTokenService, user, request.NewPassword, cancellationToken);

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

        // Parola yazma disiplininin tamamı (uzunluk kuralı, kilit açma, anahtar düşürme) TEK
        // gövdeden geçer: bkz. PasswordAssignment. Buradaki yetki/kapsam denetimi yukarıda kaldı,
        // çünkü onun kuralı her çağrı yolunda farklıdır ve ortak gövdeye taşınamaz.
        //
        // ÇEREZ oturumları ayrıca düşürülmüyor çünkü zaten düşüyor: CookieSessionValidator çerezdeki
        // kimlik damgasını parola KARMASINDAN türetir, karma değişince eski çerez en geç bir dakika
        // içinde reddedilir.
        var revoked = await PasswordAssignment.ApplyAsync(
            dbContext, passwordHasher, apiTokenService, target, request.NewPassword, cancellationToken);

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
        // ADRES İSTEKTEN DEĞİL AYARDAN: bu bağlantı tek kullanımlık bir GİRİŞ belirteci taşır ve
        // müşterinin tarayıcısına yönlendirilir. Eskiden ayar boşsa isteğin `Host` başlığına
        // düşülüyordu; `Host` istemcinin serbest metnidir, yani belirteci yabancı bir ana ada
        // götüren bir bağlantı ürettirilebilirdi. Yedek kaldırıldı ve hiçbir şey kaybolmadı:
        // `PanelPreferencesService` `ServerAddress`'i asla boş bırakmaz (bkz. PanelAddress belgesi).
        var url = PanelAddress.BuildBaseUrl(preferences.ServerAddress, preferences.PreferredHttpsPort)
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

        // WriteAsync DEĞİL UpdateAsync: dosya kurtarma kodlarının yanında SON TÜKETİLEN TOTP
        // ADIMINI da taşıyor. Üzerine düz yazsaydık, kurtarma kodlarını yenilemek (2FA kurulumunun
        // son adımı da dahil) tekrar oynatma sayacını sıfırlar ve az önce kullanılmış bir kod
        // yeniden geçerli hâle gelirdi — koruma tam da kurulduğu anda delinirdi.
        await fileStore.UpdateAsync(
            TwoFactorRecoveryCodes.PathFor(userId),
            new TwoFactorRecoveryCodes.Store(),
            mevcut => new TwoFactorRecoveryCodes.Store
            {
                Codes = hashes,
                LastUsedTotpStep = mevcut.LastUsedTotpStep
            },
            cancellationToken);
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
            TwoFactorRecoveryCodes.PathFor(userId),
            new TwoFactorRecoveryCodes.Store(),
            mevcut =>
            {
                if (!mevcut.Codes.Any(stored => string.Equals(stored, hash, StringComparison.OrdinalIgnoreCase)))
                {
                    return mevcut;
                }

                tuketildi = true;
                return new TwoFactorRecoveryCodes.Store
                {
                    Codes = mevcut.Codes
                        .Where(stored => !string.Equals(stored, hash, StringComparison.OrdinalIgnoreCase))
                        .ToList(),
                    // Aynı dosyada duran TOTP tekrar oynatma sayacı KORUNUR; düşürülseydi kurtarma
                    // kodu kullanmak, az önce harcanmış bir TOTP kodunu yeniden geçerli kılardı.
                    LastUsedTotpStep = mevcut.LastUsedTotpStep
                };
            },
            cancellationToken);

        return tuketildi;
    }

    // ---- Parola sıfırlama ----

    // Belirteç deposu ve ömrü TEK kaynaktan (ParolaSifirlamaPostasi): üreten arka plan işi, tüketen
    // aşağıdaki ResetPasswordAsync. İki yerde ayrı sabit tutmak, birinin değişip ötekinin
    // kalmasıyla "bağlantı hiç çalışmıyor" arızasını üretirdi.
    private const string PasswordResetStorePath = ParolaSifirlamaPostasi.BelirtecDepoYolu;

    /// <summary>
    /// Sıfırlama bağlantısı ister. YANIT YOLU, ADRESİN PANELDE OLUP OLMADIĞINI HİÇ SORMAZ.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ÖLÇÜLEN AÇIK.</b> Bu metot eskiden yanıt yolunda karar veriyordu: adres kayıtlıysa SMTP
    /// ayarı okunuyor, belirteç üretiliyor, GERÇEK BİR SMTP BAĞLANTISI açılıyor ve denetim kaydı
    /// yazılıyordu; kayıtlı değilse hiçbiri olmuyordu. Yanıt gövdesi ikisinde de aynıydı ama SÜRE
    /// değildi. Bu makinede ölçülen maliyetler: SMTP bağlantısı kapalı bir porta bile
    /// <b>2066 ms</b>, yanıt vermeyen bir adrese <b>10 sn</b>'de hâlâ bekliyor (MailKit varsayılan
    /// zaman aşımı 120000 ms); aynı yoldaki dosya işleri ortanca <b>0,72 ms</b> ve <b>0,17 ms</b>.
    /// Yani fark SMTP'nin kendisiydi, saniyeler mertebesindeydi ve ağ üzerinden rahatça ölçülürdü:
    /// panel, hiçbir parola bilmeyen birine "bu e-posta panelde kayıtlı mı" sorusunu YANIT
    /// SÜRESİYLE cevaplıyordu. Hesap listesi çıkaran saldırgan sonra o hesaplara odaklanır.
    /// </para>
    /// <para>
    /// <b>Neden sabit gecikme değil:</b> yukarıdaki sayılar doğru gecikmeyi seçmenin imkânsız
    /// olduğunu gösteriyor (2 sn seçilse karadelik senaryosunda fark yine sızar, 120 sn seçilse
    /// panel kullanılamaz). İşin TAMAMI — adres araması dahil — arka plana alındı; yanıt yolunda
    /// dal KALMADI, ölçülecek fark da kalmadı.
    /// </para>
    /// <para>
    /// Meşru kullanıcının postayı gerçekten alması ve gönderim hatasının GÖRÜNÜR olması
    /// <see cref="ParolaSifirlamaPostasi"/>'nın sorumluluğunda (üç sonuç da denetim kaydına düşer).
    /// </para>
    /// </remarks>
    public Task RequestPasswordResetAsync(string email, CancellationToken cancellationToken = default)
    {
        var normalized = (email ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length == 0)
        {
            // Adres HİÇ verilmemiş: sızacak bir bilgi de yok, iş üretmeye gerek yok.
            return Task.CompletedTask;
        }

        // Kuyruğa yazmak dizeden bağımsız, sabit maliyetli tek bir işlemdir: adres kayıtlı da olsa
        // değil de olsa BURADA yapılan iş birebir aynıdır. İptal belirteci bilerek TAŞINMAZ — iş
        // isteğin ömrüne bağlı değil (istemci bağlantıyı kapatsa da posta gitmeli).
        arkaPlanIsKuyrugu.Kuyrukla(
            "auth.password_reset",
            (saglayici, iptal) => ParolaSifirlamaPostasi.GonderAsync(saglayici, normalized, iptal));

        return Task.CompletedTask;
    }

    public async Task ResetPasswordAsync(string token, string newPassword, CancellationToken cancellationToken = default)
    {
        // PAROLA KURALI BELİRTEÇ TÜKETİLMEDEN ÖNCE ÖLÇÜLÜR.
        //
        // Sıra tersken şu oluyordu: belirteç atomik olarak tüketiliyor, sonra PasswordAssignment
        // içindeki uzunluk kuralı patlıyordu. Yani kullanıcı YENİ PAROLAYI KISA YAZDIĞI İÇİN
        // sıfırlama bağlantısı YANIYOR, "tekrar deneyin" diyen ekranın arkasında link ölmüş oluyor
        // ve yeni bir e-posta istemek gerekiyordu. Kuralı önce uygulamak belirteci korur; kural tek
        // kaynaktan (PasswordPolicy) geldiği için ikinci bir doğrulama yazılmıyor, aynı kapı iki
        // kez çağrılıyor (ikinci çağrı PasswordAssignment içinde, yazmanın hemen yanında kalır).
        PasswordPolicy.Ensure(newPassword);

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

        // Parola KURTARILDI: hesabın ele geçirilmiş olabileceği varsayılır. Uzunluk kuralı, kilit
        // açma ve anahtarların düşürülmesi ortak gövdeden gelir (bkz. PasswordAssignment) —
        // anahtarlar düşmezse saldırgan parola sıfırlansa bile MCP üzerinden erişimini sürdürür.
        // Token yukarıda ATOMİK olarak tüketildi; burada ayrıca silmeye gerek yok.
        var revokedTokens = await PasswordAssignment.ApplyAsync(
            dbContext, passwordHasher, apiTokenService, user, newPassword, cancellationToken);

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

    // Sıfırlama postasının gövdesi ARKA PLAN İŞİNE taşındı (ParolaSifirlamaPostasi): yanıt yolunda
    // e-posta göndermek, adresin panelde olup olmadığını yanıt süresiyle söylüyordu.

    // Belirteç üretimi, özetleme ve depo biçimi SingleUseTokenStore'a taşındı: parola sıfırlama ile
    // tek tıkla giriş bağlantısı aynı disiplini paylaşıyor, iki kopya bırakılmadı.

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
