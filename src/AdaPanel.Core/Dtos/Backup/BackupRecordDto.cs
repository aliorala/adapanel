namespace AdaPanel.Core.Dtos.Backup;

public sealed class BackupRecordDto
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public string Format { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public long Size { get; set; }

    public string Status { get; set; } = string.Empty;

    public string TargetType { get; set; } = "local";

    public string RemoteUri { get; set; } = string.Empty;
}
