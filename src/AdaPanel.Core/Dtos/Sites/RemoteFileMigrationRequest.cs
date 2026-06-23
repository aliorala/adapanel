namespace AdaPanel.Core.Dtos.Sites;

/// <summary>Uzak bir sunucudan (FTP/FTPS/SFTP) site dosyalarını çekme isteği (site göçü).</summary>
public sealed class RemoteFileMigrationRequest
{
    public string SiteName { get; set; } = string.Empty;

    /// <summary>ftp | ftps | sftp</summary>
    public string Protocol { get; set; } = "ftp";

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; }

    public string UserName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    /// <summary>Uzak kök yol (örn. /public_html). Boşsa "/".</summary>
    public string RemotePath { get; set; } = "/";
}
