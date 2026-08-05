namespace AdaPanel.Core.Dtos.Scheduler;

/// <summary>
/// "Süren çalışmayı durdur" eyleminin ÖLÇÜLMÜŞ sonucu.
/// <para>
/// Durdurma isteğinin hatasız dönmesi, işin gerçekten durduğu anlamına gelmez: Zamanlayıcı yalnızca
/// kendi başlattığı süreci tanır, o sürecin doğurduğu zincir hayatta kalabilir. Panel bu yüzden
/// niyeti değil sonucu bildirir.
/// </para>
/// </summary>
public sealed class StopScheduledTaskResultDto
{
    /// <summary>Ağacıyla birlikte sonlandırılan süreç sayısı (0 = durdurulacak canlı süreç bulunamadı).</summary>
    public int KilledProcesses { get; set; }

    /// <summary>Durdurmadan sonra görev hâlâ "Running" görünüyor mu.</summary>
    public bool StillRunning { get; set; }
}
