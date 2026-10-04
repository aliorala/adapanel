namespace AdaPanel.Core.Dtos.Files;

public sealed class FileSearchResultDto
{
    public string Path { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public FileEntryDto[] Items { get; set; } = [];
    public int ScannedFiles { get; set; }
    public bool Truncated { get; set; }
    public string TruncatedReasonKey { get; set; } = string.Empty;
}
