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

    /// <summary>
    /// Yöneticinin BİLEREK kabul ettiği uzak sunucu parmak izi: FTPS'te sertifika
    /// (<c>SHA256:1A:2B:…</c>), SFTP'de host key (<c>SHA256:base64</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Boşsa doğrulama uygulanır.</b> FTPS'te geçerli sertifika sorunsuz geçer; geçersizse
    /// reddedilir. SFTP'de ise onay HER ZAMAN gerekir — SSH'ta sertifika otoritesi yoktur ve
    /// <c>ssh</c> da ilk bağlantıda sorar.
    /// </para>
    /// <para>
    /// <b>Neden tek alan:</b> bir göç işinde dosya kaynağı tektir ve protokolü isteğin kendisi
    /// söyler. İki ayrı alan, yöneticinin hangisini dolduracağını protokole göre bilmesini
    /// gerektirirdi; yanlış alanı doldurmak sessizce "onay verilmemiş" sayılırdı.
    /// </para>
    /// </remarks>
    public string AcceptHostFingerprint { get; set; } = string.Empty;
}
