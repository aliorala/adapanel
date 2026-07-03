using AdaPanel.Api.Extensions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Scheduler;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator,Reseller,Customer")]
[Route("api/v1/cron")]
public sealed class CronController(
    ISchedulerService schedulerService,
    ICustomerScopeService customerScopeService) : ControllerBase
{
    private bool IsAdmin => User.IsInRole("Administrator");

    // Müşteri/bayi görevleri kendi ad alanında yaşar (Plesk aboneliğe bağlar): "u<id>--<ad>".
    // Böylece herkes yalnızca kendi görevlerini görür/siler/çalıştırır; admin hepsini görür.
    private string OwnerPrefix => $"u{User.GetRequiredUserId():N}--";

    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<ScheduledTaskDto>>>> GetTasksAsync(CancellationToken cancellationToken)
    {
        var tasks = await schedulerService.GetTasksAsync(cancellationToken);
        if (!IsAdmin)
        {
            var prefix = OwnerPrefix;
            tasks = tasks
                .Where(task => task.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(task =>
                {
                    task.Name = task.Name[prefix.Length..];
                    return task;
                })
                .ToList();
        }

        return Ok(ApiResponse<IReadOnlyCollection<ScheduledTaskDto>>.Ok(tasks));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<ScheduledTaskDto>>> CreateTaskAsync([FromBody] CreateScheduledTaskRequest request, CancellationToken cancellationToken)
    {
        if (!IsAdmin)
        {
            // Müşteri/bayi yalnızca URL görevi kurabilir (komut/PHP = sunucuda kod çalıştırmak, admin yetkisi).
            request.TaskType = "url";
            request.Command = string.Empty;
            request.Arguments = string.Empty;
            request.WorkingDirectory = string.Empty;
            request.PhpVersion = string.Empty;
            EnsureCustomerUrlAllowed(request.Url);

            // Plan kotası (MaxScheduledTasks; 0 = sınırsız): aynı adla güncelleme kotayı doldurmaz.
            var scope = await customerScopeService.GetScopeAsync(User.GetRequiredUserId(), cancellationToken);
            if (scope.MaxScheduledTasks > 0)
            {
                var prefix = OwnerPrefix;
                var requestedName = prefix + (request.Name ?? string.Empty).Trim();
                var ownedCount = (await schedulerService.GetTasksAsync(cancellationToken))
                    .Count(task => task.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                                   !string.Equals(task.Name, requestedName, StringComparison.OrdinalIgnoreCase));
                if (ownedCount >= scope.MaxScheduledTasks)
                {
                    throw new AppException("cron.quota_exceeded", 409, 409);
                }
            }

            request.Name = OwnerPrefix + (request.Name ?? string.Empty).Trim();
        }

        var task = await schedulerService.CreateTaskAsync(request, cancellationToken);
        if (!IsAdmin && task.Name.StartsWith(OwnerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            task.Name = task.Name[OwnerPrefix.Length..];
        }

        return Ok(ApiResponse<ScheduledTaskDto>.Ok(task, "cron.saved"));
    }

    [HttpPost("{name}/run")]
    public async Task<ActionResult<ApiResponse<object>>> RunTaskAsync(string name, CancellationToken cancellationToken)
    {
        await schedulerService.RunTaskAsync(ScopeName(name), cancellationToken);
        return Ok(ApiResponse<object>.Ok(new { name }, "cron.run_started"));
    }

    [HttpPost("{name}/enable")]
    public async Task<ActionResult<ApiResponse<object>>> EnableTaskAsync(string name, CancellationToken cancellationToken)
    {
        await schedulerService.SetTaskEnabledAsync(ScopeName(name), true, cancellationToken);
        return Ok(ApiResponse<object>.Ok(new { name }, "cron.enabled"));
    }

    [HttpPost("{name}/disable")]
    public async Task<ActionResult<ApiResponse<object>>> DisableTaskAsync(string name, CancellationToken cancellationToken)
    {
        await schedulerService.SetTaskEnabledAsync(ScopeName(name), false, cancellationToken);
        return Ok(ApiResponse<object>.Ok(new { name }, "cron.disabled"));
    }

    [HttpDelete("{name}")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteTaskAsync(string name, CancellationToken cancellationToken)
    {
        await schedulerService.DeleteTaskAsync(ScopeName(name), cancellationToken);
        return Ok(ApiResponse<object>.Ok(new { name }, "cron.deleted"));
    }

    private string ScopeName(string name) => IsAdmin ? name : OwnerPrefix + name;

    // Müşteri URL'si sunucunun kendisini (loopback/link-local) hedefleyemez; dış dünya serbest.
    private static void EnsureCustomerUrlAllowed(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri))
        {
            throw new AppException("cron.invalid_url", 400, 400);
        }

        var host = uri.Host;
        var isBlocked = uri.IsLoopback ||
            host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.StartsWith("169.254.", StringComparison.Ordinal);
        if (isBlocked)
        {
            throw new AppException("cron.url_forbidden", 403, 403);
        }
    }
}
