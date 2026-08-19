using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.WebDb;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

// DENETİM KAYDI: iki ayrı sınıf eylem, ikisi de iz gerektirir.
//   • DAĞITIM (phpMyAdmin/Adminer kurulumu): sunucuya, veritabanına TAM YETKİYLE bağlanabilen bir
//     web arayüzü indirir ve yayına alır. Panelin dışında, tarayıcıdan erişilebilen ikinci bir
//     yönetim kapısıdır — ne zaman ve kim tarafından açıldığı, güvenlik incelemesinin ilk sorusudur.
//   • TEK SEFERLİK AÇILIŞ (signon): kullanıcıya, o veritabanına oturum açmış bir bağlantı verir.
//     Bu andan sonra yapılan SQL işlemleri panelin denetiminin DIŞINDA gerçekleşir; panelde kalan
//     tek iz, erişimin KİME ve HANGİ veritabanı için verildiğidir. Bu kayıt olmazsa "bu tablo kim
//     tarafından silindi" sorusu için başlangıç noktası bile kalmaz.
//
// Üretilen URL bir kimlik jetonu taşır → kayda ASLA yazılmaz; yalnız motor ve veritabanı adı.
[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/webdb")]
public sealed class WebDbController(
    IWebDbClientService webDbClientService,
    IAuditService auditService) : ControllerBase
{
    [HttpGet("url")]
    public async Task<ActionResult<ApiResponse<WebDbWorkspaceDto>>> GetWorkspaceAsync(CancellationToken cancellationToken)
    {
        var workspace = await webDbClientService.GetWorkspaceAsync(cancellationToken);
        return Ok(ApiResponse<WebDbWorkspaceDto>.Ok(workspace));
    }

    [HttpPost("phpmyadmin")]
    public async Task<ActionResult<ApiResponse<WebDbDeploymentDto>>> DeployPhpMyAdminAsync(CancellationToken cancellationToken)
    {
        var deployment = await webDbClientService.DeployPhpMyAdminAsync(cancellationToken);
        await DagitimiKaydetAsync(deployment);
        return Ok(ApiResponse<WebDbDeploymentDto>.Ok(deployment, "webdb.deploy.completed"));
    }

    [HttpPost("adminer")]
    public async Task<ActionResult<ApiResponse<WebDbDeploymentDto>>> DeployAdminerAsync(CancellationToken cancellationToken)
    {
        var deployment = await webDbClientService.DeployAdminerAsync(cancellationToken);
        await DagitimiKaydetAsync(deployment);
        return Ok(ApiResponse<WebDbDeploymentDto>.Ok(deployment, "webdb.deploy.completed"));
    }

    /// <summary>phpMyAdmin'i signon ile açacak tek seferlik URL üretir (DB başına izole).</summary>
    [HttpPost("phpmyadmin/open")]
    public async Task<ActionResult<ApiResponse<WebDbSignonDto>>> OpenPhpMyAdminAsync(
        [FromBody] WebDbOpenRequest request,
        CancellationToken cancellationToken)
    {
        var signon = await webDbClientService.CreatePhpMyAdminSignonAsync(request.Engine, request.DatabaseName, cancellationToken);
        await ErisimiKaydetAsync("phpmyadmin", request);
        return Ok(ApiResponse<WebDbSignonDto>.Ok(signon));
    }

    /// <summary>Adminer'i tek tıkla açacak tek seferlik URL üretir (DB başına izole).</summary>
    [HttpPost("adminer/open")]
    public async Task<ActionResult<ApiResponse<WebDbSignonDto>>> OpenAdminerAsync(
        [FromBody] WebDbOpenRequest request,
        CancellationToken cancellationToken)
    {
        var signon = await webDbClientService.CreateAdminerSignonAsync(request.Engine, request.DatabaseName, cancellationToken);
        await ErisimiKaydetAsync("adminer", request);
        return Ok(ApiResponse<WebDbSignonDto>.Ok(signon));
    }

    private Task DagitimiKaydetAsync(WebDbDeploymentDto deployment) =>
        auditService.AuditAsync(
            HttpContext,
            "webdb.deployed",
            $"webdb:{deployment.Provider}",
            $"Web database client '{deployment.DisplayName}' deployed to site '{deployment.SiteName}' "
            + $"(port {deployment.Port}, path '{deployment.InstallDir}', engines: {deployment.SupportedEngines}).");

    private Task ErisimiKaydetAsync(string saglayici, WebDbOpenRequest request) =>
        auditService.AuditAsync(
            HttpContext,
            "webdb.session.opened",
            $"database:{request.DatabaseName ?? "(all)"}",
            $"One-time '{saglayici}' session opened for engine '{request.Engine}', "
            + $"database '{request.DatabaseName ?? "(all databases)"}'. "
            + "Statements executed in that session are NOT recorded by the panel.");
}

public sealed class WebDbOpenRequest
{
    public string Engine { get; init; } = "mariadb";

    public string? DatabaseName { get; init; }
}
