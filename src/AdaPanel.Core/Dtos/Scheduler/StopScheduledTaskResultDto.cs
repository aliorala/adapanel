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
    /// <summary>
    /// Ağacıyla birlikte GERÇEKTEN sonlandırıldığı ÖLÇÜLEN süreç sayısı
    /// (0 = durdurulacak canlı süreç bulunamadı <b>ya da</b> hiçbiri öldürülemedi —
    /// ayrımı <see cref="AttemptedProcesses"/> yapar).
    /// </summary>
    /// <remarks>
    /// Bu alan bir zamanlar DENEME sayısıydı. Betikteki sayaç <c>taskkill</c> çağrısından sonra
    /// KOŞULSUZ artıyor, çıkış kodu <c>Out-Null</c>'a gidiyordu. <c>taskkill /F</c> erişim
    /// reddinde 1 ile döner ve süreç yaşamaya devam eder; panel yine de "N süreç ağacı
    /// sonlandırıldı" diyordu. Artık iki kanıt birlikte aranır: çıkış kodu 0 <b>ve</b> sürecin
    /// yeniden sorulduğunda BULUNAMAMASI.
    /// </remarks>
    public int KilledProcesses { get; set; }

    /// <summary>
    /// Öldürme denenen süreç sayısı. <see cref="KilledProcesses"/>'ten büyükse aradaki fark
    /// hayatta kalanlardır.
    /// </summary>
    public int AttemptedProcesses { get; set; }

    /// <summary>
    /// Öldürülemeyen süreçler ADIYLA (<c>ad(pid)</c>). "Üçü öldürülemedi" demek, yöneticiye
    /// HANGİSİ olduğunu söylemez — sunucuda arayacağı şey tam olarak budur.
    /// </summary>
    public IReadOnlyList<string> SurvivingProcesses { get; set; } = [];

    /// <summary>Durdurmadan sonra görev hâlâ "Running" görünüyor mu.</summary>
    public bool StillRunning { get; set; }

    /// <summary>
    /// Denenen her süreç gerçekten öldürüldü mü. <c>false</c> ise panel "durduruldu" DEMEMELİ.
    /// </summary>
    public bool AllKilled => SurvivingProcesses.Count == 0 && KilledProcesses >= AttemptedProcesses;
}
