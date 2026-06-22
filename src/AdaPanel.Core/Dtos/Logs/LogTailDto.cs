namespace AdaPanel.Core.Dtos.Logs;

public sealed class LogTailDto
{
    public string Name { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public string[] Lines { get; set; } = [];

    public DateTime? LastModifiedUtc { get; set; }
}
