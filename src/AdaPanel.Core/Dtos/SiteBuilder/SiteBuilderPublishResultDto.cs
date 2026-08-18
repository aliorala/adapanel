namespace AdaPanel.Core.Dtos.SiteBuilder;

/// <summary>Yayın sonucu: gerçekten NE yazıldığını söyler.</summary>
/// <remarks>
/// "Yayınlandı" demek yetmez; bu depoda bir geri yükleme özelliği tam da bunu yaptığı için
/// yalan söylemişti. Yazılan dosyalar tek tek sayılır ve döndürülür.
/// </remarks>
public sealed class SiteBuilderPublishResultDto
{
    public string SiteName { get; set; } = string.Empty;

    public string TargetPath { get; set; } = string.Empty;

    public List<string> WrittenFiles { get; set; } = [];

    /// <summary>Üzerine yazılan (kurucuya ait olmayan) dosyalar.</summary>
    public List<string> OverwrittenFiles { get; set; } = [];

    public DateTime PublishedAtUtc { get; set; }

    /// <summary>Sitenin ilk bağlamasından türetilen adres (varsa) — kullanıcı sonucu görebilsin.</summary>
    public string PreviewUrl { get; set; } = string.Empty;
}

/// <summary>Yüklenmiş bir görsel.</summary>
public sealed class SiteBuilderImageDto
{
    /// <summary>Panelde saklanan güvenli dosya adı. Tasarımda bu ad kullanılır.</summary>
    public string Name { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public DateTime UploadedAtUtc { get; set; }
}
