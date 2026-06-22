namespace AdaPanel.Core.Dtos.Files;

public sealed class WriteFileRequest
{
    public string Path { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;
}
