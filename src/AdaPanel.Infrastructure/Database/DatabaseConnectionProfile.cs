using AdaPanel.Core.Enums;

namespace AdaPanel.Infrastructure.Database;

internal sealed record DatabaseConnectionProfile(
    DbEngine Engine,
    string EngineKey,
    string DisplayName,
    string ComponentId,
    string ServiceName,
    string Host,
    int Port,
    string AdminUserName,
    string AdminPassword,
    bool SupportsHostScopedUsers,
    string InstallDir);
