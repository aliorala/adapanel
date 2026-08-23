using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Ftp;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

// Reseller (Bayi) yalnız kendi müşterilerinin sitelerine FTP kullanıcısı yönetebilir. FTP sunucusu
// KURULUMU (ensure) server-level → admin-only. Liste, etkin sitelere göre filtrelenir; parola/silme
// işleminde kullanıcının ait olduğu site çözülüp kapsam doğrulanır.
//
// DENETİM KAYDI: buradaki uçlar GERÇEK BİR WINDOWS YEREL HESABI açar/siler/parolasını değiştirir ve
// bir klasör ağacına yazma izni verir. Yani panel dışında da sonucu olan işlemlerdir; iz bırakmazsa
// sunucuda "bu hesabı kim açtı" sorusunun cevabı Windows tarafında da yoktur. Parola DEĞERİ asla
// yazılmaz — yalnızca "değiştirildi" olgusu.
[ApiController]
[Authorize(Roles = "Administrator,Reseller")]
[Route("api/v1/ftp")]
public sealed class FtpController(
    IFtpService ftpService,
    ICustomerScopeService customerScopes,
    IIisSiteService iisSiteService,
    IAuditService auditService) : ControllerBase
{
    [HttpPost("ensure")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<FtpServerStatusDto>>> EnsureServerAsync(CancellationToken cancellationToken)
    {
        var status = await ftpService.EnsureServerAsync(cancellationToken);
        await auditService.AuditAsync(HttpContext, "ftp.server.ensured", "ftp-server", "FTP server installation/configuration ensured.");
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

        // FTPS zorunluluğunun KAPATILMASI, bütün müşterilerin parolasını düz metne indirir —
        // sunucu genelinde güvenlik seviyesini düşüren bir karardır ve kim aldığı bilinmelidir.
        await auditService.AuditAsync(
            HttpContext,
            "ftp.tls.updated",
            "ftp-server",
            $"FTPS requirement set to {requireSsl}.");

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

        // GÜVENLİK (çapraz-kiracı): admin DIŞINDAKİ kullanıcılar ana dizini kendi sitelerinin
        // klasörünün dışına gösteremez.
        //
        // Bu alan boş bırakılabilir ve o zaman sitenin kökü kullanılır (olağan durum). Ama
        // doldurulduğunda hiç doğrulanmıyordu: verilen yola FTP hesabı için DEĞİŞTİRME izni
        // uygulanıyor (icacls ... /T). Yani "C:\" ya da başka bir müşterinin klasörü yazan bir
        // kiracı, o ağacın tamamına yazma hakkı olan bir FTP hesabı elde ederdi.
        if (!User.IsInRole("Administrator") && !string.IsNullOrWhiteSpace(request.HomePath))
        {
            var site = await iisSiteService.GetSiteAsync(request.SiteName, cancellationToken);
            var kok = string.IsNullOrWhiteSpace(site.ApplicationRootPath)
                ? site.PhysicalPath
                : site.ApplicationRootPath;

            // Kökün kendisi geçerlidir: en olağan seçim sitenin ta kendisidir.
            if (!PathContainment.IsRootOrInside(kok, request.HomePath))
            {
                return StatusCode(403, ApiResponse<FtpUserDto>.Fail("ftp.home_outside_site", 403));
            }
        }

        var user = await ftpService.CreateFtpUserAsync(request, cancellationToken);

        // PAROLA YAZILMAZ: request.Password kayda hiç girmez. Yazılan şey hesabın kimliği,
        // hangi siteye bağlandığı ve hangi ana dizine yazma hakkı aldığıdır — bir olaydan sonra
        // "bu hesap neye erişebiliyordu" sorusunu cevaplayan alanlar bunlar.
        await auditService.AuditAsync(
            HttpContext,
            "ftp.user.created",
            user.UserName,
            $"FTP account created for site '{user.SiteName}' with home '{user.HomePath}'.",
            await SiteSahibiAsync(user.SiteName, cancellationToken));

        return Ok(ApiResponse<FtpUserDto>.Ok(user, "ftp.user_created"));
    }

    [HttpPost("users/password")]
    public async Task<ActionResult<ApiResponse<object>>> SetPasswordAsync([FromBody] SetFtpPasswordRequest request, CancellationToken cancellationToken)
    {
        await EnsureFtpUserScopeAsync(request.UserName, cancellationToken);
        await ftpService.SetFtpPasswordAsync(request, cancellationToken);

        // Yalnız OLGU yazılır ("değiştirildi"), değer değil.
        await auditService.AuditAsync(
            HttpContext,
            "ftp.user.password_changed",
            request.UserName,
            "FTP account password was changed.",
            await FtpKullanicisininSahibiAsync(request.UserName, cancellationToken));

        return Ok(ApiResponse<object>.Ok(new { request.UserName }, "ftp.password_changed"));
    }

    [HttpDelete("users/{userName}")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteUserAsync(string userName, CancellationToken cancellationToken)
    {
        await EnsureFtpUserScopeAsync(userName, cancellationToken);

        // SAHİP SİLMEDEN ÖNCE ÇÖZÜLÜR: hesap kaldırıldıktan sonra hangi siteye ait olduğu artık
        // hiçbir yerden okunamaz; kaydı sonradan kiracıya bağlamak İMKÂNSIZ olurdu.
        var sahip = await FtpKullanicisininSahibiAsync(userName, cancellationToken);

        await ftpService.DeleteFtpUserAsync(userName, cancellationToken);
        await auditService.AuditAsync(HttpContext, "ftp.user.deleted", userName, "FTP account was removed.", sahip);
        return Ok(ApiResponse<object>.Ok(new { userName }, "ftp.user_deleted"));
    }

    /// <summary>
    /// Sitenin ABONELİK SAHİBİ — kaydın kiracıya görünmesini sağlayan alan.
    /// </summary>
    /// <remarks>
    /// Kapsam denetimi ÇAĞIRANIN siteye erişebildiğini doğrular; buradaki soru başka: kaynak KİME
    /// ait. İkisi ayrıldığı an (yönetici/bayi kiracının sitesine dokunur) tam da kaydın en çok
    /// gerektiği andır. Bu alan olmadan kiracı, kendi sitesinde yapılan işlemi denetim ekranında
    /// HİÇ göremezdi. Site sahipsizse (yöneticinin kendi sitesi) boş kalır; tahmin edilmez.
    /// </remarks>
    private Task<Guid?> SiteSahibiAsync(string? siteName, CancellationToken cancellationToken)
        => AuditSubject.SiteOwnerAsync(customerScopes, siteName, cancellationToken);

    /// <summary>
    /// FTP hesabının bağlı olduğu sitenin abonelik sahibi.
    /// </summary>
    /// <remarks>
    /// <b>Neden <see cref="EnsureFtpUserScopeAsync"/> ile aynı çözüm ama ayrı metot:</b> o metot
    /// yalnız KAPSAMLI rollerde çalışır (yöneticide erken döner) çünkü işi yetkilendirmedir.
    /// Buradaki soru yetkiyle ilgili değil: kaydın konusu kim. En çok yönetici işlemlerinde
    /// gerekir — tam da ötekinin çalışmadığı yerde.
    /// </remarks>
    private async Task<Guid?> FtpKullanicisininSahibiAsync(string userName, CancellationToken cancellationToken)
    {
        try
        {
            var users = await ftpService.GetFtpUsersAsync(cancellationToken);
            var siteName = users.FirstOrDefault(item =>
                string.Equals(item.UserName, userName, StringComparison.OrdinalIgnoreCase))?.SiteName;
            return await SiteSahibiAsync(siteName, cancellationToken);
        }
        catch
        {
            return null;
        }
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
