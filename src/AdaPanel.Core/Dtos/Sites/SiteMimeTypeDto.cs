namespace AdaPanel.Core.Dtos.Sites;

/// <summary>
/// Site bazlı uzantı → MIME türü eşlemesi (IIS <c>staticContent/mimeMap</c>).
/// </summary>
/// <remarks>
/// IIS, <c>staticContent</c> içinde TANIMLI OLMAYAN uzantıları hiç sunmaz (404.3 döner); yani
/// bu eşleme olmadan sitenin kendi yazı tipi/veri dosyaları indirilemez. Plesk'te ayrı bir
/// "MIME types" ekranı olmasının sebebi budur.
/// </remarks>
public sealed class SiteMimeTypeDto
{
    /// <summary>Nokta ile başlayan uzantı: <c>.woff2</c>.</summary>
    public string Extension { get; set; } = string.Empty;

    /// <summary>MIME türü: <c>font/woff2</c>.</summary>
    public string MimeType { get; set; } = string.Empty;
}
