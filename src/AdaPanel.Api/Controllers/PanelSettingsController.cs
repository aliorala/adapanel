using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/panel-settings")]
public sealed class PanelSettingsController(IPanelPreferencesService preferencesService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<PanelPreferencesDto>>> GetAsync(CancellationToken cancellationToken)
        => Ok(ApiResponse<PanelPreferencesDto>.Ok(await preferencesService.GetAsync(cancellationToken)));

    [HttpPut]
    public async Task<ActionResult<ApiResponse<PanelPreferencesDto>>> UpdateAsync(
        [FromBody] UpdatePanelPreferencesRequest request,
        CancellationToken cancellationToken)
        => Ok(ApiResponse<PanelPreferencesDto>.Ok(await preferencesService.UpdateAsync(request, cancellationToken), "settings.saved"));
}
