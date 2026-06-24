using AdaPanel.Api.Security;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Ftp;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

// Reseller (Bayi) yalnız kendi müşterilerinin sitelerine FTP kullanıcısı yönetebilir. FTP sunucusu
// KURULUMU (ensure) server-level → admin-only. Liste, etkin sitelere göre filtrelenir; parola/silme
// işleminde kullanıcının ait olduğu site çözülüp kapsam doğrulanır.
[ApiController]
[Authorize(Roles = "Administrator,Reseller")]
[Route("api/v1/ftp")]
public sealed class FtpController(IFtpService ftpService, ICustomerScopeService customerScopes) : ControllerBase
{
    [HttpPost("ensure")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<FtpServerStatusDto>>> EnsureServerAsync(CancellationToken cancellationToken)
    {
        var status = await ftpService.EnsureServerAsync(cancellationToken);
        return Ok(ApiResponse<FtpServerStatusDto>.Ok(status, "ftp.server_ready"));
    }

    // M5 (panel "yalnız güvenli FTP"): FTPS zorunluluğu server-level → admin-only.
    [HttpGet("tls")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<FtpTlsStatusDto>>> GetTlsAsync(CancellationToken cancellationToken)
    {
        var requireSsl = await ftpService.GetRequireSslAsync(cancellationToken);
        return Ok(ApiResponse<FtpTlsStatusDto>.Ok(new FtpTlsStatusDto { RequireSsl = requireSsl }));
    }

    [HttpPut("tls")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<FtpTlsStatusDto>>> SetTlsAsync([FromBody] FtpTlsRequest request, CancellationToken cancellationToken)
    {
        var requireSsl = await ftpService.SetRequireSslAsync(request.RequireSsl, cancellationToken);
        return Ok(ApiResponse<FtpTlsStatusDto>.Ok(
            new FtpTlsStatusDto { RequireSsl = requireSsl },
            requireSsl ? "ftp.tls_required" : "ftp.tls_optional"));
    }

    [HttpGet("users")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<FtpUserDto>>>> GetUsersAsync(CancellationToken cancellationToken)
    {
        var users = await ftpService.GetFtpUsersAsync(cancellationToken);

        // Bayi/müşteri yalnız etkin sitelerinin FTP kullanıcılarını görür.
        var allowed = await CustomerAccess.GetEffectiveSitesAsync(customerScopes, User, cancellationToken);
        if (allowed is not null)
        {
            users = users
                .Where(item => allowed.Contains(item.SiteName, StringComparer.OrdinalIgnoreCase))
                .ToArray();
        }

        return Ok(ApiResponse<IReadOnlyCollection<FtpUserDto>>.Ok(users));
    }

    [HttpPost("users")]
    public async Task<ActionResult<ApiResponse<FtpUserDto>>> CreateUserAsync([FromBody] CreateFtpUserRequest request, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, request.SiteName, cancellationToken);
        var user = await ftpService.CreateFtpUserAsync(request, cancellationToken);
        return Ok(ApiResponse<FtpUserDto>.Ok(user, "ftp.user_created"));
    }

    [HttpPost("users/password")]
    public async Task<ActionResult<ApiResponse<object>>> SetPasswordAsync([FromBody] SetFtpPasswordRequest request, CancellationToken cancellationToken)
    {
        await EnsureFtpUserScopeAsync(request.UserName, cancellationToken);
        await ftpService.SetFtpPasswordAsync(request, cancellationToken);
        return Ok(ApiResponse<object>.Ok(new { request.UserName }, "ftp.password_changed"));
    }

    [HttpDelete("users/{userName}")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteUserAsync(string userName, CancellationToken cancellationToken)
    {
        await EnsureFtpUserScopeAsync(userName, cancellationToken);
        await ftpService.DeleteFtpUserAsync(userName, cancellationToken);
        return Ok(ApiResponse<object>.Ok(new { userName }, "ftp.user_deleted"));
    }

    // Bayi/müşteri için: FTP kullanıcısının ait olduğu siteyi çözüp kapsamı doğrula. Kullanıcı
    // bulunamazsa (ya da başka sitenin kullanıcısıysa) EnsureSiteAsync 403 atar. Admin'de no-op.
    private async Task EnsureFtpUserScopeAsync(string userName, CancellationToken cancellationToken)
    {
        if (!CustomerAccess.IsScoped(User))
        {
            return;
        }

        var users = await ftpService.GetFtpUsersAsync(cancellationToken);
        var siteName = users
            .FirstOrDefault(item => string.Equals(item.UserName, userName, StringComparison.OrdinalIgnoreCase))?.SiteName
            ?? string.Empty;
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
    }
}

/// <summary>FTPS zorunluluğu toggle isteği (panel "yalnız güvenli FTP").</summary>
public sealed class FtpTlsRequest
{
    public bool RequireSsl { get; set; }
}

/// <summary>FTPS zorunluluğu durumu.</summary>
public sealed class FtpTlsStatusDto
{
    public bool RequireSsl { get; set; }
}
