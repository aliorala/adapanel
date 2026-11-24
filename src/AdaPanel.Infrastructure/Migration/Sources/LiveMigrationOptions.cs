namespace AdaPanel.Infrastructure.Migration.Sources;

public sealed class LiveMigrationOptions
{
    public long MaxArtifactBytes { get; set; } = 32L * 1024 * 1024 * 1024;
    public long MaxTotalArtifactBytes { get; set; } = 100L * 1024 * 1024 * 1024;
    public int MaxSourceUnits { get; set; } = 1000;
    public int MaxCommandOutputBytes { get; set; } = 4 * 1024 * 1024;
    public TimeSpan BackupTimeout { get; set; } = TimeSpan.FromHours(6);
}
