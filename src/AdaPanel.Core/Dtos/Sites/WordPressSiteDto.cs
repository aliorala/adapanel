namespace AdaPanel.Core.Dtos.Sites;

/// <summary>panel WordPress Toolkit benzeri: bir sitedeki WordPress kurulumunun durumu.</summary>
public sealed class WordPressSiteDto
{
    public string SiteName { get; set; } = string.Empty;

    public bool IsWordPress { get; set; }

    public string Version { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    /// <summary>Bakım modu (.maintenance dosyası) açık mı.</summary>
    public bool MaintenanceMode { get; set; }

    public string AdminUrl { get; set; } = string.Empty;

    /// <summary>Kurulu eklentiler (wp-content/plugins, dosya başlıklarından).</summary>
    public IReadOnlyCollection<WordPressComponentDto> Plugins { get; set; } = [];

    /// <summary>Kurulu temalar (wp-content/themes/*/style.css başlıklarından).</summary>
    public IReadOnlyCollection<WordPressComponentDto> Themes { get; set; } = [];

    /// <summary>wordpress.org'dan öğrenilen en güncel kararlı çekirdek sürümü (boş = bilinmiyor).</summary>
    public string LatestVersion { get; set; } = string.Empty;

    /// <summary>Kurulu sürüm en güncel sürümün gerisinde mi (çekirdek güncellemesi var).</summary>
    public bool UpdateAvailable { get; set; }
}

/// <summary>WordPress eklenti/tema bileşeni (ad + sürüm + slug).</summary>
public sealed class WordPressComponentDto
{
    public string Name { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;
}

public sealed class SetWordPressMaintenanceRequest
{
    public bool Enabled { get; set; }
}
