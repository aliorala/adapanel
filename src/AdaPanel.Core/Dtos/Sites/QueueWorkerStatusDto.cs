namespace AdaPanel.Core.Dtos.Sites;

/// <summary>Laravel queue worker (queue:work) durumu — Plesk Kuyruk sekmesi gibi.</summary>
public sealed class QueueWorkerStatusDto
{
    public bool Running { get; set; }

    public string TaskName { get; set; } = string.Empty;
}
