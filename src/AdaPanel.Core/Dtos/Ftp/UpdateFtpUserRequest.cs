namespace AdaPanel.Core.Dtos.Ftp;

/// <summary>FTP hesabı güncelleme: ev dizini (site kökü içinde) ve/veya salt-okunur bayrağı. Boş alan = değişmez.</summary>
public sealed class UpdateFtpUserRequest
{
    public string? HomePath { get; set; }

    public bool? ReadOnly { get; set; }
}
