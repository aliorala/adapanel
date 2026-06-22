namespace AdaPanel.Core.Dtos.Ftp;

public sealed class FtpServerStatusDto
{
    public string ServerAddress { get; set; } = string.Empty;

    public int ControlPort { get; set; } = 21;

    public int PassivePortStart { get; set; } = 50000;

    public int PassivePortEnd { get; set; } = 51000;

    public bool IsConfigured { get; set; }

    public string StatusMessage { get; set; } = "ftp.server_ready";
}
