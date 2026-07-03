using AdaPanel.Core.Dtos.Scheduler;

namespace AdaPanel.Core.Interfaces;

public interface ISchedulerService
{
    Task<IReadOnlyCollection<ScheduledTaskDto>> GetTasksAsync(CancellationToken cancellationToken = default);

    Task<ScheduledTaskDto> CreateTaskAsync(CreateScheduledTaskRequest request, CancellationToken cancellationToken = default);

    Task DeleteTaskAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Görevi beklemeden hemen tetikler (Plesk "Run Now").</summary>
    Task RunTaskAsync(string name, CancellationToken cancellationToken = default);
}
