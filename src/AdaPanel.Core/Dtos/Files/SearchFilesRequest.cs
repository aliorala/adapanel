namespace AdaPanel.Core.Dtos.Files;

public sealed class SearchFilesRequest
{
    public string Path { get; set; } = string.Empty;

    public string Query { get; set; } = string.Empty;
}
