namespace AdaPanel.Core.Dtos.Databases;

public sealed class DatabaseDumpResultDto
{
    public string Engine { get; set; } = string.Empty;

    public string DatabaseName { get; set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;

    public long Size { get; set; }
}
