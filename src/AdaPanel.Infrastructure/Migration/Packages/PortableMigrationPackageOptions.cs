namespace AdaPanel.Infrastructure.Migration.Packages;

public sealed class PortableMigrationPackageOptions
{
    public List<string> PublicWebRoots { get; set; } = [];
    public long MaxUploadBytes { get; set; } = 10L * 1024 * 1024 * 1024;
    public long MaxPayloadBytes { get; set; } = 32L * 1024 * 1024 * 1024;
    public long MaxTotalUncompressedBytes { get; set; } = 100L * 1024 * 1024 * 1024;
    public int MaxCompressionRatio { get; set; } = 1000;
    public int MaxItems { get; set; } = 100_000;
    public int MaxManifestBytes { get; set; } = 4 * 1024 * 1024;
}
