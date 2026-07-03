namespace AdaPanel.Core.Dtos.Scheduler;

public sealed class CreateScheduledTaskRequest
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Plesk'teki görev tipleri: "command" (komut), "php" (PHP betiği), "url" (adresi GET ile çağır).</summary>
    public string TaskType { get; set; } = "command";

    /// <summary>command: çalıştırılacak program. php: betik dosyasının tam yolu.</summary>
    public string Command { get; set; } = string.Empty;

    public string Arguments { get; set; } = string.Empty;

    public string WorkingDirectory { get; set; } = string.Empty;

    /// <summary>url tipi: zamanında GET ile çağrılacak http(s) adresi (ör. uygulama cron uç noktası).</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>php tipi: istenen PHP sürümü (ör. "8.3"); boşsa kurulu en yeni sürüm kullanılır.</summary>
    public string PhpVersion { get; set; } = string.Empty;

    public string ScheduleType { get; set; } = "hourly";

    public int Interval { get; set; } = 1;

    public string TimeOfDay { get; set; } = "00:00";
}
