using System.Security.Claims;
using AdaPanel.Api.Extensions;
using AdaPanel.Core.Dtos.Auth;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController(
    IAntiforgery antiforgery,
    IAuthService authService,
    IAuditService auditService) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("csrf")]
    public ActionResult<ApiResponse<object>> GetCsrfToken()
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return Ok(ApiResponse<object>.Ok(new
        {
            requestToken = tokens.RequestToken ?? string.Empty
        }));
    }

    [AllowAnonymous]
    [HttpGet("session")]
    public async Task<ActionResult<ApiResponse<CurrentSessionDto>>> GetSessionAsync(CancellationToken cancellationToken)
    {
        var session = await authService.GetCurrentSessionAsync(User, cancellationToken);

        if ((User.Identity?.IsAuthenticated ?? false) && !session.IsAuthenticated)
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }

        return Ok(ApiResponse<CurrentSessionDto>.Ok(session));
    }

    [AllowAnonymous]
    [EnableRateLimiting("login")]
    [HttpPost("login")]
    public async Task<ActionResult<ApiResponse<LoginResultDto>>> LoginAsync(
        [FromBody] LoginRequest request,
        CancellationToken cancellationToken)
    {
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        var result = await authService.LoginAsync(request, ipAddress, cancellationToken);

        if (result.IsAuthenticated && result.User is not null)
        {
            await SignInAsync(result.User, request.RememberMe);
        }

        return Ok(ApiResponse<LoginResultDto>.Ok(result, "auth.login.success"));
    }

    [AllowAnonymous]
    [EnableRateLimiting("login")]
    [HttpPost("password/forgot")]
    public async Task<ActionResult<ApiResponse<object>>> ForgotPasswordAsync(
        [FromBody] ForgotPasswordRequest request,
        CancellationToken cancellationToken)
    {
        // Kullanıcı numaralandırmasını engellemek için her zaman aynı yanıt döner.
        await authService.RequestPasswordResetAsync(request.Email, cancellationToken);
        return Ok(ApiResponse<object>.Ok(new { }, "auth.password_reset.requested"));
    }

    [AllowAnonymous]
    [EnableRateLimiting("login")]
    [HttpPost("password/reset")]
    public async Task<ActionResult<ApiResponse<object>>> ResetPasswordAsync(
        [FromBody] ResetPasswordRequest request,
        CancellationToken cancellationToken)
    {
        await authService.ResetPasswordAsync(request.Token, request.NewPassword, cancellationToken);
        return Ok(ApiResponse<object>.Ok(new { }, "auth.password_reset.completed"));
    }

    /// <summary>panel "log in as customer": yönetici, hedef kullanıcı olarak oturum açar.</summary>
    [Authorize(Roles = "Administrator")]
    [HttpPost("impersonate/{userId:guid}")]
    public async Task<ActionResult<ApiResponse<CurrentUserDto>>> ImpersonateAsync(Guid userId, CancellationToken cancellationToken)
    {
        var adminId = User.GetRequiredUserId();
        var adminName = User.Identity?.Name ?? "admin";
        if (userId == adminId)
        {
            return BadRequest(ApiResponse<object>.Fail("auth.cannot_impersonate_self"));
        }

        var target = await authService.GetUserByIdAsync(userId, cancellationToken);
        await SignInAsync(target, isPersistent: false, impersonatorId: adminId, impersonatorName: adminName);
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        await auditService.WriteAsync("auth.impersonate.started", "auth", $"'{adminName}' impersonating '{target.UserName}'.", adminId, ip, cancellationToken);
        return Ok(ApiResponse<CurrentUserDto>.Ok(target, "auth.impersonate.started"));
    }

    /// <summary>Impersonation'ı bitirir: asıl yönetici hesabına geri döner.</summary>
    [Authorize]
    [HttpPost("impersonate/stop")]
    public async Task<ActionResult<ApiResponse<CurrentUserDto>>> StopImpersonateAsync(CancellationToken cancellationToken)
    {
        var impersonatorId = User.FindFirstValue("impersonator_id");
        if (!Guid.TryParse(impersonatorId, out var adminId))
        {
            return BadRequest(ApiResponse<object>.Fail("auth.not_impersonating"));
        }

        var admin = await authService.GetUserByIdAsync(adminId, cancellationToken);
        await SignInAsync(admin, isPersistent: false);
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        await auditService.WriteAsync("auth.impersonate.stopped", "auth", $"Returned to '{admin.UserName}'.", adminId, ip, cancellationToken);
        return Ok(ApiResponse<CurrentUserDto>.Ok(admin, "auth.impersonate.stopped"));
    }

    [Authorize]
    [HttpPost("logout")]
    public async Task<ActionResult<ApiResponse<object>>> LogoutAsync(CancellationToken cancellationToken)
    {
        var userId = User.GetRequiredUserId();
        var userName = User.Identity?.Name ?? "unknown";
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        await auditService.WriteAsync("auth.logout.succeeded", "auth", $"User '{userName}' signed out.", userId, ipAddress, cancellationToken);

        return Ok(ApiResponse<object>.Ok(new { }, "auth.logout.success"));
    }

    [Authorize]
    [HttpPost("2fa/begin")]
    public async Task<ActionResult<ApiResponse<TwoFactorSetupDto>>> BeginTwoFactorSetupAsync(CancellationToken cancellationToken)
    {
        var result = await authService.BeginTwoFactorSetupAsync(User.GetRequiredUserId(), cancellationToken);
        return Ok(ApiResponse<TwoFactorSetupDto>.Ok(result));
    }

    [Authorize]
    [HttpPost("2fa/confirm")]
    public async Task<ActionResult<ApiResponse<TwoFactorConfirmDto>>> ConfirmTwoFactorSetupAsync(
        [FromBody] VerifyTwoFactorRequest request,
        CancellationToken cancellationToken)
    {
        var result = await authService.ConfirmTwoFactorSetupAsync(
            User.GetRequiredUserId(),
            request.Code,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        var authentication = await HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        await SignInAsync(result.User, authentication.Properties?.IsPersistent ?? false);
        return Ok(ApiResponse<TwoFactorConfirmDto>.Ok(result, "auth.2fa.confirmed"));
    }

    [Authorize]
    [HttpPost("2fa/recovery-codes")]
    public async Task<ActionResult<ApiResponse<RecoveryCodesDto>>> RegenerateRecoveryCodesAsync(CancellationToken cancellationToken)
    {
        var result = await authService.RegenerateRecoveryCodesAsync(User.GetRequiredUserId(), cancellationToken);
        return Ok(ApiResponse<RecoveryCodesDto>.Ok(result, "auth.2fa.recovery_regenerated"));
    }

    [Authorize]
    [HttpPost("2fa/disable")]
    public async Task<ActionResult<ApiResponse<CurrentUserDto>>> DisableTwoFactorAsync(
        [FromBody] DisableTwoFactorRequest request,
        CancellationToken cancellationToken)
    {
        var user = await authService.DisableTwoFactorAsync(
            User.GetRequiredUserId(),
            request.Password,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        var authentication = await HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        await SignInAsync(user, authentication.Properties?.IsPersistent ?? false);
        return Ok(ApiResponse<CurrentUserDto>.Ok(user, "auth.2fa.disabled"));
    }

    /// <summary>
    /// Kullanıcının KENDİ parolasını değiştirmesi.
    /// <para>
    /// Yalnız ÇEREZ şeması kabul edilir. API anahtarıyla parola değiştirilebilseydi, sınırlı bir
    /// anahtarı ele geçiren biri parolayı değiştirip hesabın tamamını devralabilirdi — anahtarın
    /// yetkisi hesabın yetkisine yükselirdi. Anahtar sunucudan sunucuya iş içindir, hesap yönetimi
    /// için değil.
    /// </para>
    /// <para>
    /// Hız sınırı mevcut parolayı deneme yanılmaya karşı korur: çerez çalınmış olsa bile parola
    /// tahmin edilerek doğrulama aşılamasın.
    /// </para>
    /// </summary>
    [Authorize(AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme)]
    [EnableRateLimiting("password-change")]
    [HttpPost("change-password")]
    public async Task<ActionResult<ApiResponse<ChangePasswordResultDto>>> ChangePasswordAsync(
        [FromBody] ChangePasswordRequest request,
        CancellationToken cancellationToken)
    {
        var result = await authService.ChangePasswordAsync(
            User.GetRequiredUserId(),
            request,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        return Ok(ApiResponse<ChangePasswordResultDto>.Ok(result, "auth.password_changed"));
    }

    private Task SignInAsync(CurrentUserDto user, bool isPersistent, Guid? impersonatorId = null, string? impersonatorName = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.UserName),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Role, user.Role),
            new("preferred_language", user.PreferredLanguage),
            new("two_factor_enabled", user.IsTwoFactorEnabled.ToString())
        };

        if (impersonatorId.HasValue && !string.IsNullOrWhiteSpace(impersonatorName))
        {
            claims.Add(new Claim("impersonator_id", impersonatorId.Value.ToString()));
            claims.Add(new Claim("impersonator_name", impersonatorName));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        var properties = new AuthenticationProperties
        {
            IsPersistent = isPersistent,
            AllowRefresh = true,
            IssuedUtc = DateTimeOffset.UtcNow,
            ExpiresUtc = isPersistent ? DateTimeOffset.UtcNow.AddDays(14) : null
        };

        return HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, properties);
    }
}
