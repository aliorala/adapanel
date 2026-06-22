namespace AdaPanel.Core.Dtos.Files;

public sealed class SetFilePermissionsRequest
{
    public string Path { get; set; } = string.Empty;

    public string Principal { get; set; } = string.Empty;

    public string Rights { get; set; } = string.Empty;
}
