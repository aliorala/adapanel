namespace AdaPanel.Core.Dtos.Sites;

public sealed class UpdateDnsZoneRequest
{
    public string ZoneName { get; set; } = string.Empty;

    public string[] NameServers { get; set; } = [];

    /// <summary>İzin verilen AXFR (zone transfer) hedef IP'leri (ikincil DNS sunucuları). Boş = transfer kapalı.</summary>
    public string[] ZoneTransferIps { get; set; } = [];
}
