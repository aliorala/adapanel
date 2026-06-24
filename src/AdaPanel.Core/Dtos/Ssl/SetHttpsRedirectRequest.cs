namespace AdaPanel.Core.Dtos.Ssl;

/// <summary>Bir site için HTTP→HTTPS kalıcı (301) yönlendirmesini açar/kapatır (panel "Force HTTPS").</summary>
public sealed class SetHttpsRedirectRequest
{
    public string SiteName { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
}
