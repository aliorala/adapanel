using System.IO.Compression;
using AdaPanel.Infrastructure.Backup;
using ZstdSharp;

namespace AdaPanel.Infrastructure.Seo;

/// <summary>Analiz edilecek TEK bir günlük dosyası.</summary>
/// <param name="Path">Tam yol.</param>
/// <param name="Compressed">Arşiv mi (<c>.log.zst</c>) — rapor bunu ayrıca sayar.</param>
/// <param name="LastWriteUtc">Sıralama ölçütü.</param>
public readonly record struct SeoLogFile(string Path, bool Compressed, DateTime LastWriteUtc);

/// <summary>
/// Bir sitenin IIS günlük dosyalarını bulur ve <b>sıkıştırılmış olanları da</b> okunabilir hâle
/// getirir.
/// </summary>
/// <remarks>
/// <para>
/// <b>NEDEN ARŞİVLER DE OKUNUYOR — ÖLÇÜLEN GEREKÇE:</b> panelin log döndürme özelliği kapanmış IIS
/// günlüklerini ZSTD ile arşivliyor (<c>u_ex260817.log</c> → <c>u_ex260817.log.zst</c>) ve varsayılan
/// site yaş eşiği 30 GÜN. Yalnız <c>*.log</c> okuyan bir analiz, döndürme açıkken pencerenin eski
/// günlerini SESSİZCE kaybederdi: "Googlebot son iki haftadır uğramıyor" gibi bir sonuç, aslında
/// dosyanın sıkıştırılmış olmasından çıkardı. Sıkıştırma biçimi burada YENİDEN KARARLAŞTIRILMAZ;
/// biçim tespiti panelin tek sıkıştırma kaynağından okunur
/// (<see cref="BackupDumpCompression.DetectFormat"/>), yani <c>.gz</c> ile elle sıkıştırılmış bir
/// günlük de kendiliğinden okunur.
/// </para>
/// <para>
/// <b>Uzantıya değil İÇERİĞE bakılır:</b> karar dosyanın ilk dört baytındaki çerçeve imzasından
/// verilir. Adı <c>.log</c> olan ama arşiv olan (ya da tersi) bir dosya yanlış okunmaz.
/// </para>
/// <para>
/// <b>Akış hâlinde:</b> arşiv geçici bir dosyaya AÇILMAZ, doğrudan akıtılarak okunur — 38 MB'lık
/// bir günlüğü diske ikinci kez yazmak hem yavaş hem gereksizdir.
/// </para>
/// </remarks>
public static class SeoLogFileSet
{
    /// <summary>Akış tamponu (kalıp: <c>BackupDumpCompression</c>).</summary>
    private const int BufferSize = 1 << 20;

    /// <summary>
    /// Pencereye giren günlük dosyalarını yeniden eskiye sıralayarak döndürür.
    /// </summary>
    /// <remarks>
    /// Süzgeç <b>son yazma zamanına</b> bakar, dosya ADINDAKİ tarihe değil: IIS dosya adını
    /// <c>u_exYYMMDD.log</c> yazar ama yönetici biçimi değiştirebilir (saatlik döndürmede
    /// <c>u_exYYMMDDHH.log</c>), ayrıca arşiv adı <c>.log.zst</c> ile biter. Adı ayrıştırmak, biçim
    /// değişince sessizce boş küme döndürürdü. Satır düzeyindeki asıl tarih süzgeci zaten
    /// analizde, günlüğün KENDİ <c>date</c> alanından uygulanır.
    /// </remarks>
    public static IReadOnlyList<SeoLogFile> Discover(string directory, DateTime cutoffUtc)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var klasor = new DirectoryInfo(directory);

        return klasor
            .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
            .Where(file => IsLogCandidate(file.Name))
            .Where(file => file.LastWriteTimeUtc.Date >= cutoffUtc.Date)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(SeoLogLimits.MaxFiles)
            .Select(file => new SeoLogFile(
                file.FullName,
                file.Name.EndsWith(BackupDumpCompression.CompressedExtension, StringComparison.OrdinalIgnoreCase),
                file.LastWriteTimeUtc))
            .ToArray();
    }

    /// <summary>
    /// Ada göre ÖN ELEME. Klasörde başka dosyalar da bulunabilir; hepsini açıp imzasına bakmak,
    /// yüzlerce dosyalık bir klasörde gereksiz disk erişimi olurdu.
    /// </summary>
    private static bool IsLogCandidate(string name) =>
        name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".log" + BackupDumpCompression.CompressedExtension, StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".log.gz", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Dosyayı metin okuyucusu olarak açar; arşivse akış üzerinde AÇARAK.
    /// </summary>
    /// <remarks>
    /// <c>FileShare.ReadWrite | FileShare.Delete</c>: IIS aktif günlüğü açık tutar ve panel ona
    /// DOKUNMAZ, yalnız okur. Paylaşım bayrakları olmadan aktif dosya hiç okunamazdı — yani
    /// BUGÜNÜN verisi hep eksik olurdu.
    /// </remarks>
    public static TextReader OpenText(string path)
    {
        var format = BackupDumpCompression.DetectFormat(path);
        var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, useAsync: true);

        try
        {
            // Biçim listesi burada TÜRETİLMEZ, yalnız TÜKETİLİR: hangi biçimlerin var olduğu
            // BackupDumpCompression.DumpFormat'ta tanımlıdır. Yeni bir biçim eklenirse derleyici
            // bu switch'i değil, oradaki numaralandırmayı işaret eder.
            // Okuyucu kapanınca açıcı, açıcı kapanınca DOSYA kapanır (üçünün de leaveOpen'ı
            // varsayılan olarak false). Zincirin bir halkası açık bırakılsaydı analiz biten her
            // dosya için bir tutamak sızdırırdı ve 60 dosyalık bir turda bu görünür olurdu.
            return format switch
            {
                BackupDumpCompression.DumpFormat.Zstandard =>
                    new StreamReader(new DecompressionStream(stream)),
                BackupDumpCompression.DumpFormat.GZip =>
                    new StreamReader(new GZipStream(stream, CompressionMode.Decompress)),
                _ => new StreamReader(stream)
            };
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }
}
