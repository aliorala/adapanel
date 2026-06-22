namespace AdaPanel.Core.Dtos.Databases;

public sealed class DatabaseWorkspaceDto
{
    public string Engine { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string ComponentId { get; init; } = string.Empty;

    public string ServiceName { get; init; } = string.Empty;

    public string Host { get; init; } = string.Empty;

    public int Port { get; init; }

    public string AdminUserName { get; init; } = string.Empty;

    public string AdminPassword { get; init; } = string.Empty;

    public bool SupportsHostScopedUsers { get; init; }

    public IReadOnlyCollection<DatabaseRecordDto> Databases { get; init; } = [];

    public IReadOnlyCollection<DatabaseUserDto> Users { get; init; } = [];
}
