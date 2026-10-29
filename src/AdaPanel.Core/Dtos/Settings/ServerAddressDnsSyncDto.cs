namespace AdaPanel.Core.Dtos.Settings;

public sealed class ServerAddressDnsSyncDto
{
    public AdaPanel.Core.Dtos.Ftp.FtpServerAddressSyncDto? Ftp { get; set; }
    public string OldAddress { get; set; } = string.Empty;
    public string NewAddress { get; set; } = string.Empty;
    public bool Pending { get; set; }
    public bool Skipped { get; set; }
    public string Error { get; set; } = string.Empty;
    public List<ServerAddressDnsZoneResultDto> Zones { get; set; } = [];
}

public sealed class ServerAddressDnsZoneResultDto
{
    public string SiteName { get; set; } = string.Empty;
    public string ZoneName { get; set; } = string.Empty;
    public int ChangedRecords { get; set; }
    public bool Succeeded { get; set; }
    public bool Skipped { get; set; }
    public string Error { get; set; } = string.Empty;
}

public sealed class RetryServerAddressDnsSyncRequest
{
    public string OldAddress { get; set; } = string.Empty;
}
