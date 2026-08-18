namespace AdaPanel.Core.Dtos.Logs;

/// <summary>
/// Tek bir günlük klasörünün (panel ya da bir sitenin IIS klasörü) döndürme durumu.
/// </summary>
/// <remarks>
/// Ekranda "hangi sitenin logu ne zaman döndürüldü, ne kadar yer kazanıldı" sorusunun cevabı
/// budur. <see cref="SkippedLockedFiles"/> ayrı bir alan olarak taşınıyor: kilitli dosya bir HATA
/// değildir (IIS o dosyaya yazıyordur) ama SESSİZ de kalmamalı — aksi hâlde "0 dosya döndürüldü"
/// satırı, politikanın hiç çalışmadığı bir arızayla aynı görünürdü. Bu depoda tam olarak bu
/// karışıklık yaşandı: "0 veritabanı yedeklendi" satırı aylarca bilgi sanıldı, oysa arızaydı.
/// </remarks>
public sealed class LogRotationTargetDto
{
    /// <summary>Kararlı anahtar: <c>panel</c> ya da <c>site:&lt;siteAdı&gt;</c>.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Ekranda görünen ad (panel için "AdaPanel", site için site adı).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Site hedefiyse site adı; panel hedefinde boş.</summary>
    public string SiteName { get; set; } = string.Empty;

    /// <summary>Yönetilen klasör (IIS ayarından çözülür, sabit kodlu değildir).</summary>
    public string Directory { get; set; } = string.Empty;

    /// <summary>Klasör hiç yoksa / erişilemiyorsa false; ekran bunu "henüz log yok" diye gösterir.</summary>
    public bool DirectoryExists { get; set; }

    public DateTime? LastRotationUtc { get; set; }

    /// <summary>Son turda sıkıştırılan (döndürülen) dosya sayısı.</summary>
    public int RotatedFiles { get; set; }

    /// <summary>Son turda saklama kuralıyla silinen arşiv sayısı.</summary>
    public int DeletedArchives { get; set; }

    /// <summary>Son turda kazanılan yer (bayt): sıkıştırma kazancı + silinen arşivler.</summary>
    public long FreedBytes { get; set; }

    /// <summary>Başka bir süreç (IIS / Serilog) tuttuğu için ATLANAN dosya sayısı.</summary>
    public int SkippedLockedFiles { get; set; }

    /// <summary>Güvenlik kapısına takılıp atlanan dosya sayısı (bağlantı noktası, kapsam dışı yol...).</summary>
    public int SkippedUnsafeFiles { get; set; }

    /// <summary>Klasörde şu an duran ham günlük dosyalarının toplamı (bayt).</summary>
    public long CurrentLogBytes { get; set; }

    /// <summary>Klasörde şu an duran arşivlerin toplamı (bayt).</summary>
    public long CurrentArchiveBytes { get; set; }

    /// <summary><c>ok</c> | <c>skipped</c> | <c>error</c>.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Hata varsa ham mesaj; yoksa boş.</summary>
    public string Error { get; set; } = string.Empty;
}
