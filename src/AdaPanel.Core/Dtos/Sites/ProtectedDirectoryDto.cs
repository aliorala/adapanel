namespace AdaPanel.Core.Dtos.Sites;

/// <summary>Plesk "Password-Protected Directories" benzeri korumalı dizin kaydı.</summary>
public sealed class ProtectedDirectoryDto
{
    public string SiteName { get; set; } = string.Empty;

    /// <summary>Site köküne göre yol (örn. "yonetim" veya "alt/klasor").</summary>
    public string RelativePath { get; set; } = string.Empty;

    public string UserName { get; set; } = string.Empty;

    public string Realm { get; set; } = string.Empty;
}

public sealed class ProtectDirectoryRequest
{
    public string RelativePath { get; set; } = string.Empty;

    public string UserName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string Realm { get; set; } = string.Empty;
}
