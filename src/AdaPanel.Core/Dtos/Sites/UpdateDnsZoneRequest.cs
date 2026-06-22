namespace AdaPanel.Core.Dtos.Sites;

public sealed class UpdateDnsZoneRequest
{
    public string ZoneName { get; set; } = string.Empty;

    public string[] NameServers { get; set; } = [];
}
