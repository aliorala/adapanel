namespace AdaPanel.Core.Dtos.Migration;

/// <summary>Sensitive request/protected-job state only. Never log or publish this object.</summary>
public sealed class LiveMigrationConnection
{
    public string Provider { get; init; } = string.Empty;
    public string Platform { get; init; } = "linux";
    public string Host { get; init; } = string.Empty;
    public int SshPort { get; init; } = 22;
    public string SshUsername { get; init; } = "root";
    public string Password { get; init; } = string.Empty;
    public string PrivateKey { get; init; } = string.Empty;
    public string PrivateKeyPassphrase { get; init; } = string.Empty;
    public string AcceptedSshFingerprint { get; init; } = string.Empty;
}
