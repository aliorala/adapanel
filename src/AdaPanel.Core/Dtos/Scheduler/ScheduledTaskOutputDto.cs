namespace AdaPanel.Core.Dtos.Scheduler;

/// <summary>
/// Bir görevin SON çalışmasında ürettiği çıktı.
/// <para>
/// Windows Görev Zamanlayıcı komut çıktısını saklamaz; yalnızca çıkış kodunu tutar. Panel bu yüzden
/// görevi bir kabuğa sarıp çıktısını kendi dosyasına yönlendirir ve buradan okur.
/// </para>
/// </summary>
public sealed class ScheduledTaskOutputDto
{
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Okunacak bir çıktı var mı. Görev hiç çalışmadıysa ya da çıktı yakalamadan önce
    /// kaydedildiyse false olur — bu ikisi "çıktı boş" ile aynı şey DEĞİLDİR.
    /// </summary>
    public bool Available { get; set; }

    /// <summary>Dosya büyük olduğu için yalnızca son kısmı gönderildiyse true.</summary>
    public bool Truncated { get; set; }

    public DateTime? WrittenAtUtc { get; set; }

    public string Content { get; set; } = string.Empty;
}
