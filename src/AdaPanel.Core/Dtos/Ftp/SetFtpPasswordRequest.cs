namespace AdaPanel.Core.Dtos.Ftp;

/// <summary>Bir FTP kullanıcısının (Windows yerel hesabı) parolasını değiştirir.</summary>
public sealed class SetFtpPasswordRequest
{
    public string UserName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;
}
