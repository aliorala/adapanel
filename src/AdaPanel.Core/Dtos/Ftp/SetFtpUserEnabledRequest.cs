namespace AdaPanel.Core.Dtos.Ftp;

/// <summary>FTP hesabını aç/kapat isteği.</summary>
public sealed class SetFtpUserEnabledRequest
{
    public bool Enabled { get; set; }
}
