namespace AdaPanel.Core.Dtos.Sites;

public sealed class DnsInspectionDto
{
    public SiteDnsZoneDto Zone { get; init; } = new();
    public IReadOnlyCollection<string>? PublishedRecordKeys { get; init; }
    public IReadOnlyList<string> MissingDefaults { get; init; } = [];
    public string Error { get; init; } = string.Empty;
}
