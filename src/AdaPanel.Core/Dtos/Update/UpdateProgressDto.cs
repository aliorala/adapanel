namespace AdaPanel.Core.Dtos.Update;

/// <summary>Güncelleme paketi indirmesinin ANLIK durumu. Yüzde tahmin değil, yazılan bayttır.</summary>
public sealed class UpdateProgressDto
{
    /// <summary>Şu an bir indirme sürüyor mu?</summary>
    public bool IsDownloading { get; set; }

    public long ReceivedBytes { get; set; }

    /// <summary>Sunucu Content-Length vermediyse 0; bu durumda yüzde hesaplanamaz, yalnız boyut gösterilir.</summary>
    public long TotalBytes { get; set; }

    /// <summary>0-100. <see cref="TotalBytes"/> bilinmiyorsa -1 (yüzde YOK demektir, uydurma sayı üretilmez).</summary>
    public int Percent { get; set; }

    public DateTime? StartedAtUtc { get; set; }

    /// <summary>İndirme hatayla bittiyse sebebi; boşsa hata yok.</summary>
    public string Error { get; set; } = string.Empty;
}
