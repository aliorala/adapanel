namespace AdaPanel.Core.Dtos.Scheduler;

/// <summary>
/// TEK bir görevin anlık durumu. "Şimdi çalıştır"dan sonra sayfanın canlı bilgi vermesi için.
/// <para>
/// Neden ayrı ve küçük: canlı bilgi için saniyede bir tüm görev listesini okumak, her seferinde
/// bütün görevleri gezen bir PowerShell turu başlatır. Tek göreve bakan bu uç, o turu tek satıra
/// indirir.
/// </para>
/// </summary>
public sealed class ScheduledTaskStatusDto
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Ready / Running / Disabled. Boşsa görev bulunamadı.</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>Görev şu anda çalışıyor mu (durum metninden değil, Task Scheduler'ın kendisinden).</summary>
    public bool IsRunning { get; set; }

    /// <summary>Son çalışmanın çıkış kodu. Çalışırken Task Scheduler'ın özel kodları döner.</summary>
    public long? LastTaskResult { get; set; }

    public DateTime? LastRunTimeUtc { get; set; }
}
