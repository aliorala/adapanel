using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Store;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/store")]
public sealed class StoreController(IAppStoreService appStoreService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<AppTemplateDto>>>> GetTemplatesAsync(CancellationToken cancellationToken)
    {
        var templates = await appStoreService.GetTemplatesAsync(cancellationToken);
        return Ok(ApiResponse<IReadOnlyCollection<AppTemplateDto>>.Ok(templates));
    }

    [HttpPost("install")]
    public async Task<ActionResult<ApiResponse<AppTemplateDto>>> InstallAsync([FromBody] InstallAppTemplateRequest request, CancellationToken cancellationToken)
    {
        var template = await appStoreService.InstallAsync(request, cancellationToken);
        return Ok(ApiResponse<AppTemplateDto>.Ok(template, "store.installed"));
    }
}
