using AdaPanel.Core.Enums;

namespace AdaPanel.Core.Entities;

public sealed class InstalledComponent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string ComponentId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public ComponentType Type { get; set; }

    public string InstallDir { get; set; } = string.Empty;

    public string ServiceName { get; set; } = string.Empty;

    public ComponentInstallStatus Status { get; set; } = ComponentInstallStatus.Pending;

    public string ProtectedConfigurationJson { get; set; } = string.Empty;

    public string LastError { get; set; } = string.Empty;

    public DateTime InstalledAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

