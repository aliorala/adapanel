namespace AdaPanel.Core.Dtos.Files;

public sealed class ArchiveFileRequest
{
    public string Path { get; set; } = string.Empty;

    public string DestinationPath { get; set; } = string.Empty;

    public bool Overwrite { get; set; }
}
