namespace AdaPanel.Core.Dtos.Ftp;

public sealed class FtpUserDto
{
    public string UserName { get; set; } = string.Empty;

    public string HomePath { get; set; } = string.Empty;

    public string SiteName { get; set; } = string.Empty;

    public bool ExistsOnSystem { get; set; }

    public bool IsEnabled { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public string ServerAddress { get; set; } = string.Empty;

    public int Port { get; set; } = 21;
}
