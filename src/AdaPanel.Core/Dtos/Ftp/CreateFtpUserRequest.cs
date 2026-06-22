namespace AdaPanel.Core.Dtos.Ftp;

public sealed class CreateFtpUserRequest
{
    public string UserName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string SiteName { get; set; } = string.Empty;

    public string HomePath { get; set; } = string.Empty;
}
