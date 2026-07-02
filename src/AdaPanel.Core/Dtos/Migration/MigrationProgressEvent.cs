namespace AdaPanel.Core.Dtos.Migration;

/// <summary>
/// Canlı göç akışında tek bir ilerleme olayı (SSE ile istemciye akıtılır). Tek tip; alanlar faza göre dolar:
/// "plan" → çalışacak adımların listesi; "start" → adım başladı; "result" → adım bitti (başarı + mesaj);
/// "done" → genel sonuç. Böylece istemci "hangi aşamada ne yapılıyor"u canlı gösterebilir.
/// </summary>
public sealed class MigrationProgressEvent
{
    /// <summary>Olay türü: "plan" | "start" | "result" | "done".</summary>
    public string Phase { get; set; } = string.Empty;

    /// <summary>İlgili adım kimliği (site/files/userini/database). "plan"/"done" fazlarında boş olabilir.</summary>
    public string Step { get; set; } = string.Empty;

    /// <summary>"result" fazında adımın başarısı; diğer fazlarda null.</summary>
    public bool? Success { get; set; }

    /// <summary>i18n anahtarı ya da ham mesaj ("result" ve "done" fazları için).</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Başarısız "result" fazında gerçek teknik neden (ham mesaj); diğer durumlarda null.</summary>
    public string? Detail { get; set; }

    /// <summary>"plan" fazında: sırayla çalışacak adım kimlikleri.</summary>
    public List<string> Steps { get; set; } = [];

    /// <summary>"done" fazında: göçün genel başarısı.</summary>
    public bool? OverallSuccess { get; set; }
}
