namespace AdaPanel.Core.Dtos.Databases;

public sealed class DatabaseRecordDto
{
    public string Engine { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Owner { get; init; } = string.Empty;

    public string Encoding { get; init; } = string.Empty;

    public string Collation { get; init; } = string.Empty;
}
