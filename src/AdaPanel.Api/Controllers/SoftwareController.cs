using AdaPanel.Api.Extensions;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Components;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/software")]
public sealed class SoftwareController(
    IComponentBootstrapper componentBootstrapper,
    IOperationProgressService operationProgress,
    IPhpService phpService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<SoftwareCatalogDto>>> GetCatalogAsync(CancellationToken cancellationToken)
    {
        var catalog = await componentBootstrapper.GetCatalogAsync(cancellationToken);
        return Ok(ApiResponse<SoftwareCatalogDto>.Ok(catalog));
    }

    [HttpGet("operations/progress")]
    public ActionResult<ApiResponse<OperationProgressDto>> GetOperationProgress()
        => Ok(ApiResponse<OperationProgressDto>.Ok(operationProgress.Snapshot()));

    [HttpPost("updates/check")]
    public async Task<ActionResult<ApiResponse<SoftwareCatalogDto>>> CheckForUpdatesAsync()
    {
        // Upstream cozumlemesi network'e gider; istek iptaline baglamiyoruz ki yarida kalmasin.
        var catalog = await componentBootstrapper.CheckForUpdatesAsync(CancellationToken.None);
        return Ok(ApiResponse<SoftwareCatalogDto>.Ok(catalog, "software.updates_checked"));
    }

    // NOT: Kurulum/kaldirma islemleri istek iptal token'ina BAGLANMAZ: indirme uzun surer
    // (orn. PostgreSQL ~365MB) ve kullanici sekmeyi kapatirsa/baglanti koparsa kurulumun
    // yarida kesilmesi tutarsiz durum birakir. Islem sunucuda tamamlanir.
    [HttpPost("install")]
    public async Task<ActionResult<ApiResponse<ComponentInstallResultDto>>> InstallAsync(
        [FromBody] ComponentInstallRequest request)
    {
        var actorUserId = User.GetRequiredUserId();
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        var result = request.ComponentId.StartsWith("php-", StringComparison.OrdinalIgnoreCase)
            ? await phpService.InstallVersionAsync(
                request.ComponentId,
                actorUserId,
                ipAddress,
                CancellationToken.None)
            : await componentBootstrapper.InstallAsync(
                request.ComponentId,
                actorUserId,
                ipAddress,
                CancellationToken.None);

        return Ok(ApiResponse<ComponentInstallResultDto>.Ok(result, result.Message));
    }

    [HttpPost("install-defaults")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<ComponentInstallResultDto>>>> InstallDefaultsAsync()
    {
        // Canlı ilerleme: her bileşen InstallComponentInternalAsync içinde adım olarak raporlanır.
        operationProgress.Begin("software.installing_defaults");
        try
        {
            var results = await componentBootstrapper.InstallDefaultsAsync(
                User.GetRequiredUserId(),
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                CancellationToken.None);

            foreach (var phpResult in results.Where(result =>
                         result.ComponentId.StartsWith("php-", StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(result.Status, "Installed", StringComparison.OrdinalIgnoreCase)))
            {
                await phpService.RegisterFastCgiAsync(phpResult.ComponentId, CancellationToken.None);
            }

            operationProgress.Complete();
            return Ok(ApiResponse<IReadOnlyCollection<ComponentInstallResultDto>>.Ok(results, "software.defaults.completed"));
        }
        catch (Exception exception)
        {
            operationProgress.Fail(exception.Message);
            throw;
        }
    }

    [HttpDelete("{componentId}")]
    public async Task<ActionResult<ApiResponse<object>>> RemoveAsync(string componentId)
    {
        await componentBootstrapper.RemoveAsync(
            componentId,
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            CancellationToken.None);

        return Ok(ApiResponse<object>.Ok(new { componentId }, "software.remove.completed"));
    }
}
