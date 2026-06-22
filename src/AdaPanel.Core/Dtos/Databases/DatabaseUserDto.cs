namespace AdaPanel.Core.Dtos.Databases;

public sealed class DatabaseUserDto
{
    public string Engine { get; init; } = string.Empty;

    public string UserName { get; init; } = string.Empty;

    public string Host { get; init; } = string.Empty;

    public bool CanLogin { get; init; }
}
