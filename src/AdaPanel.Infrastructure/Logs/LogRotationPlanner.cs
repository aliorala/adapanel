using AdaPanel.Core.Dtos.Logs;

namespace AdaPanel.Infrastructure.Logs;

/// <summary>Bir günlük klasöründeki tek dosyanın karar için gereken tüm bilgisi.</summary>
public sealed record LogFileSnapshot(string Name, long SizeBytes, DateTime LastWriteUtc);

/// <summary>Bir klasör için döndürme kararı.</summary>
/// <param name="Rotate">Sıkıştırılacak (döndürülecek) dosyalar.</param>
/// <param name="ActiveFileName">
/// Yazılmakta olduğu varsayılan, bu turda KESİNLİKLE dokunulmayacak dosya. Ekranda gösterilir ki
/// "neden bu dosya duruyor" sorusunun cevabı görünür olsun.
/// </param>
public sealed record LogRotationPlan(IReadOnlyList<LogFileSnapshot> Rotate, string? ActiveFileName);

/// <summary>
/// Log döndürme KARARLARI. Tamamen saf: dosya sistemine, saate ya da IIS'e dokunmaz.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden ayrı sınıf:</b> asıl servis IIS'i, diski ve sıkıştırıcıyı birlikte kullanır; kararı
/// orada bırakmak "yalnızca gerçek bir sunucuda test edilebilir" bir mantık üretirdi. Bu depoda
/// aynı ayrıştırma daha önce de yapıldı (<c>AutomationService.GroupDatabasesBySite</c>,
/// <c>SelectBackupEngines</c>) ve karşılığını verdi.
/// </para>
/// <para>
/// <b>EN ÖNEMLİ KURAL — aktif dosyaya dokunulmaz:</b> IIS/Serilog o an yazdığı dosyayı açık tutar.
/// Bu makinede ÖLÇÜLDÜ (canlı panel servisi, Serilog <c>shared: true</c>): aktif dosyada
/// <c>File.Move</c> ve <c>File.Delete</c> <see cref="IOException"/> ile reddediliyor. Ölçümün
/// tehlikeli yanı ise şu: yazan taraf dosyayı <c>FILE_SHARE_DELETE</c> ile açmışsa aynı çağrılar
/// BAŞARILI olur ve <b>canlı günlük sessizce yok edilir</b>. Bu yüzden "dene, patlarsa atla"
/// yeterli bir koruma değildir — en yeni dosya baştan aday bile sayılmaz.
/// </para>
/// </remarks>
public static class LogRotationPlanner
{
    private const long BytesPerMegabyte = 1024L * 1024L;

    /// <summary>
    /// Klasördeki ham günlüklerden hangilerinin döndürüleceğini seçer.
    /// </summary>
    /// <param name="logFiles">Klasördeki ham <c>*.log</c> dosyaları (arşivler DEĞİL).</param>
    /// <param name="maxAgeDays">Yaş eşiği (gün); 0 = yaşa göre döndürme kapalı.</param>
    /// <param name="maxFileSizeMB">Boyut eşiği (MB); 0 = boyuta göre döndürme kapalı.</param>
    /// <param name="nowUtc">Şimdi (UTC) — dışarıdan verilir ki test saate bağlı olmasın.</param>
    public static LogRotationPlan PlanRotation(
        IReadOnlyCollection<LogFileSnapshot> logFiles,
        int maxAgeDays,
        int maxFileSizeMB,
        DateTime nowUtc)
    {
        var aktif = EnYenisi(logFiles);
        if (logFiles.Count == 0)
        {
            return new LogRotationPlan([], null);
        }

        // İki eşik de kapalıysa HİÇBİR ŞEY döndürülmez. "Hepsini döndür" gibi bir varsayılana
        // düşmek, ayarı sıfırlayan yöneticinin bütün günlüklerini sıkıştırmak olurdu.
        if (maxAgeDays <= 0 && maxFileSizeMB <= 0)
        {
            return new LogRotationPlan([], aktif?.Name);
        }

        var boyutEsigi = maxFileSizeMB > 0 ? maxFileSizeMB * BytesPerMegabyte : long.MaxValue;

        var secilenler = logFiles
            .Where(dosya => aktif is null || !NameEquals(dosya.Name, aktif.Name))
            // Boş dosya sıkıştırılmaz: ZSTD boş girdide bile geçerli bir çerçeve (9 bayt) üretir,
            // yani dosyayı KÜÇÜLTMEK yerine büyütürdük.
            .Where(dosya => dosya.SizeBytes > 0)
            .Where(dosya =>
                (maxAgeDays > 0 && (nowUtc - dosya.LastWriteUtc).TotalDays >= maxAgeDays)
                || dosya.SizeBytes >= boyutEsigi)
            .OrderBy(dosya => dosya.LastWriteUtc)
            .ThenBy(dosya => dosya.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new LogRotationPlan(secilenler, aktif?.Name);
    }

    /// <summary>
    /// Saklama kuralını uygular: hangi ARŞİVLER silinecek?
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Yalnızca arşivler silinir, ham günlükler ASLA.</b> Toplam boyut sınırı ham günlükleri de
    /// SAYAR (sınır diskin gerçeğini anlatmalı) ama onları silmez: bir ham günlük ya döndürülmeye
    /// aday olur ya da aktif dosyadır; ikisinde de silmek yanlış cevaptır. Sınır yalnız arşiv
    /// silerek karşılanamıyorsa panel silmeyi ZORLAMAZ — durumu olduğu gibi bildirir.
    /// </para>
    /// <para>Silme sırası her zaman EN ESKİDEN yenidir.</para>
    /// </remarks>
    public static IReadOnlyList<LogFileSnapshot> PlanRetention(
        IReadOnlyCollection<LogFileSnapshot> archives,
        IReadOnlyCollection<LogFileSnapshot> logFiles,
        int keepCount,
        int maxTotalSizeMB)
    {
        if (archives.Count == 0)
        {
            return [];
        }

        // Eskiden yeniye: silme her zaman en eskiden başlar.
        var eskidenYeniye = archives
            .OrderBy(dosya => dosya.LastWriteUtc)
            .ThenBy(dosya => dosya.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var silinecek = new List<LogFileSnapshot>();
        var silinecekAdlar = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1) Adet sınırı — 0 "sınırsız" demektir (panelin diğer saklama ayarlarıyla aynı anlam).
        if (keepCount > 0 && eskidenYeniye.Length > keepCount)
        {
            foreach (var dosya in eskidenYeniye.Take(eskidenYeniye.Length - keepCount))
            {
                if (silinecekAdlar.Add(dosya.Name))
                {
                    silinecek.Add(dosya);
                }
            }
        }

        // 2) Toplam boyut sınırı.
        if (maxTotalSizeMB > 0)
        {
            var sinir = maxTotalSizeMB * BytesPerMegabyte;
            var toplam = logFiles.Sum(dosya => dosya.SizeBytes)
                         + archives.Sum(dosya => dosya.SizeBytes)
                         - silinecek.Sum(dosya => dosya.SizeBytes);

            foreach (var dosya in eskidenYeniye)
            {
                if (toplam <= sinir)
                {
                    break;
                }

                if (!silinecekAdlar.Add(dosya.Name))
                {
                    continue;
                }

                silinecek.Add(dosya);
                toplam -= dosya.SizeBytes;
            }
        }

        return silinecek
            .OrderBy(dosya => dosya.LastWriteUtc)
            .ThenBy(dosya => dosya.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Yazılmakta olduğu varsayılan dosya: en son yazılan. Eşitlikte ad sırası belirler ki karar
    /// aynı girdide her zaman aynı çıksın (IIS aynı saniyede iki dosya kapatabilir).
    /// </summary>
    public static LogFileSnapshot? EnYenisi(IReadOnlyCollection<LogFileSnapshot> logFiles) =>
        logFiles
            .OrderByDescending(dosya => dosya.LastWriteUtc)
            .ThenByDescending(dosya => dosya.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    /// <summary>Politikadaki gün eşiğini hedefe göre seçer (panel ve site farklı okuyuculara sahiptir).</summary>
    public static int AgeThresholdFor(LogRotationSettingsDto settings, bool isPanelTarget) =>
        isPanelTarget ? settings.PanelLogMaxAgeDays : settings.SiteLogMaxAgeDays;

    /// <summary>
    /// Saklanacak arşiv adedini hedefe göre seçer. Panel ve site AYRI: panel günlüğünün 14 günlük
    /// saklama sözü Serilog tarafından zaten veriliyordu, site günlüğünde ise böyle bir söz yok.
    /// </summary>
    public static int KeepCountFor(LogRotationSettingsDto settings, bool isPanelTarget) =>
        isPanelTarget ? settings.PanelKeepCount : settings.SiteKeepCount;

    private static bool NameEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
