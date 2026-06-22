namespace AdaPanel.Core.Dtos.Sites;

public sealed class SiteDnsRecordDto
{
    public string Id { get; set; } = string.Empty;

    public string Type { get; set; } = "A";

    public string Host { get; set; } = "@";

    public string Value { get; set; } = string.Empty;

    public int Ttl { get; set; } = 3600;

    public int? Priority { get; set; }

    public bool IsManaged { get; set; } = true;
}
