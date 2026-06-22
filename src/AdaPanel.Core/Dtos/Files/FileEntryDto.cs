namespace AdaPanel.Core.Dtos.Files;

public sealed class FileEntryDto
{
    public string Name { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public bool IsDirectory { get; set; }

    public long Size { get; set; }

    public DateTime? LastModifiedUtc { get; set; }

    public string Extension { get; set; } = string.Empty;
}
