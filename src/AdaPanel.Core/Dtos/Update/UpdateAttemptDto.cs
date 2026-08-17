namespace AdaPanel.Core.Dtos.Update;

/// <summary>
/// Kurulum çalıştırıcısının bıraktığı son durum. Panel kurulum sırasında kapalı olduğu için
/// ilerlemeyi kendisi bildiremez; çalıştırıcı her adımda diske yazar, panel geri döndüğünde okur.
/// </summary>
public sealed class UpdateAttemptDto
{
    /// <summary>
    /// starting | stopping-service | installing | installer-finished | succeeded | failed
    /// </summary>
    public string Phase { get; set; } = string.Empty;

    /// <summary>Kurulum yardımcısının çıkış kodu. 5 = kurulum iptal edildi (dosyalar kullanımdaydı).</summary>
    public int? InstallerExitCode { get; set; }

    public string Error { get; set; } = string.Empty;

    /// <summary>
    /// Hükmü PANEL verdiyse gösterilecek çeviri anahtarı (<see cref="Error"/> serbest metni yerine).
    /// Boşsa metin çalıştırıcıdan gelmiştir ve olduğu gibi gösterilir.
    /// </summary>
    public string ErrorKey { get; set; } = string.Empty;

    /// <summary>
    /// Hüküm çalıştırıcının değil PANELİN kendi ölçümünden çıktı (çalışan sürüm = hedef sürüm).
    /// <para>
    /// Gerekli: kurulum panelin servisini durdurur ve çalıştırıcı çoğu zaman hükmü yazamadan ölür;
    /// durum dosyası "kuruluyor"da donar. O dosyaya bakıp "tamamlanamadı" demek, BAŞARILI bir
    /// güncellemeyi başarısız ilan etmekti — canlıda iki makinede birden yaşandı.
    /// </para>
    /// </summary>
    public bool IsDecidedByPanel { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }

    /// <summary>Deneme sonuçlandı mı? true ise arayüz beklemeyi BIRAKMALI ve sonucu göstermeli.</summary>
    public bool IsFinished =>
        string.Equals(Phase, "succeeded", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Phase, "failed", StringComparison.OrdinalIgnoreCase);

    public bool IsFailed => string.Equals(Phase, "failed", StringComparison.OrdinalIgnoreCase);
}
