namespace AdaPanel.Core.Dtos.Sites;

public sealed class UpsertDnsRecordRequest
{
    public string? Id { get; set; }

    public string Type { get; set; } = "A";

    public string Host { get; set; } = "@";

    public string Value { get; set; } = string.Empty;

    public int Ttl { get; set; } = 3600;

    public int? Priority { get; set; }
}
