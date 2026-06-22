using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/service-plans")]
public sealed class ServicePlansController(IServicePlanService servicePlanService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<ServicePlanDto>>>> GetAsync(CancellationToken cancellationToken)
    {
        var plans = await servicePlanService.GetPlansAsync(cancellationToken);
        return Ok(ApiResponse<IReadOnlyCollection<ServicePlanDto>>.Ok(plans));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<ServicePlanDto>>> CreateAsync([FromBody] ServicePlanRequest request, CancellationToken cancellationToken)
    {
        var plan = await servicePlanService.CreatePlanAsync(request, cancellationToken);
        return Ok(ApiResponse<ServicePlanDto>.Ok(plan, "plans.created"));
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<ApiResponse<ServicePlanDto>>> UpdateAsync(string id, [FromBody] ServicePlanRequest request, CancellationToken cancellationToken)
    {
        var plan = await servicePlanService.UpdatePlanAsync(id, request, cancellationToken);
        return Ok(ApiResponse<ServicePlanDto>.Ok(plan, "plans.updated"));
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        await servicePlanService.DeletePlanAsync(id, cancellationToken);
        return Ok(ApiResponse<object>.Ok(new { }, "plans.deleted"));
    }
}
