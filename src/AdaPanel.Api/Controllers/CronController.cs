using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Scheduler;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/cron")]
public sealed class CronController(ISchedulerService schedulerService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<ScheduledTaskDto>>>> GetTasksAsync(CancellationToken cancellationToken)
    {
        var tasks = await schedulerService.GetTasksAsync(cancellationToken);
        return Ok(ApiResponse<IReadOnlyCollection<ScheduledTaskDto>>.Ok(tasks));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<ScheduledTaskDto>>> CreateTaskAsync([FromBody] CreateScheduledTaskRequest request, CancellationToken cancellationToken)
    {
        var task = await schedulerService.CreateTaskAsync(request, cancellationToken);
        return Ok(ApiResponse<ScheduledTaskDto>.Ok(task, "cron.saved"));
    }

    [HttpDelete("{name}")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteTaskAsync(string name, CancellationToken cancellationToken)
    {
        await schedulerService.DeleteTaskAsync(name, cancellationToken);
        return Ok(ApiResponse<object>.Ok(new { name }, "cron.deleted"));
    }
}
