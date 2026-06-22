using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Interfaces;

namespace AdaPanel.Infrastructure.Services;

/// <inheritdoc />
public sealed class OperationProgressService : IOperationProgressService
{
    private readonly object gate = new();
    private readonly List<OperationProgressStepDto> steps = [];
    private string title = string.Empty;
    private bool running;
    private bool completed;
    private bool failed;
    private string error = string.Empty;
    private DateTime? startedAtUtc;

    public void Begin(string title)
    {
        lock (gate)
        {
            this.title = title ?? string.Empty;
            steps.Clear();
            running = true;
            completed = false;
            failed = false;
            error = string.Empty;
            startedAtUtc = DateTime.UtcNow;
        }
    }

    public void StartStep(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        lock (gate)
        {
            steps.Add(new OperationProgressStepDto { Name = name, Status = "running" });
        }
    }

    public void CompleteStep(string name, bool success, string detail = "")
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        lock (gate)
        {
            var status = success ? "done" : "failed";
            // Aynı isimli en son "running" adımı güncelle; yoksa yeni bir tamamlanmış adım ekle.
            for (var index = steps.Count - 1; index >= 0; index--)
            {
                if (string.Equals(steps[index].Name, name, StringComparison.OrdinalIgnoreCase) &&
                    steps[index].Status == "running")
                {
                    steps[index] = new OperationProgressStepDto { Name = name, Status = status, Detail = detail };
                    return;
                }
            }

            steps.Add(new OperationProgressStepDto { Name = name, Status = status, Detail = detail });
        }
    }

    public void Complete()
    {
        lock (gate)
        {
            running = false;
            completed = true;
        }
    }

    public void Fail(string error)
    {
        lock (gate)
        {
            running = false;
            failed = true;
            this.error = error ?? string.Empty;
        }
    }

    public OperationProgressDto Snapshot()
    {
        lock (gate)
        {
            return new OperationProgressDto
            {
                Title = title,
                Running = running,
                Completed = completed,
                Failed = failed,
                Error = error,
                Steps = steps.ToArray(),
                StartedAtUtc = startedAtUtc
            };
        }
    }
}
