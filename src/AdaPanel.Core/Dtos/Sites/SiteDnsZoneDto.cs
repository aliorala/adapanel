namespace AdaPanel.Core.Dtos.Sites;

public sealed class SiteDnsZoneDto
{
    public string SiteName { get; set; } = string.Empty;

    public string ZoneName { get; set; } = string.Empty;

    public string[] NameServers { get; set; } = [];

    /// <summary>İzin verilen AXFR (zone transfer) hedef IP'leri (ikincil DNS sunucuları). Boş = transfer kapalı.</summary>
    public string[] ZoneTransferIps { get; set; } = [];

    public SiteDnsRecordDto[] Records { get; set; } = [];

    public bool IsWindowsDnsAvailable { get; set; }

    public bool IsWindowsZonePresent { get; set; }

    public string Source { get; set; } = "panel-managed";

    public string StatusMessage { get; set; } = string.Empty;
}
