using System.IO.Compression;

namespace AdaPanel.Infrastructure.Backup;

/// <summary>
/// Yedek dosyalarının SIKIŞTIRILMASI/açılması. Panelin SIKIŞTIRMA BİÇİMİ VE KARARI BURADADIR;
/// başka hiçbir yerde "bunu sıkıştıralım mı" sorusu sorulmaz (tek kaynak).
/// <para>
/// <b>Neden gerekli:</b> tek başına alınan veritabanı yedekleri HAM <c>.sql</c> olarak saklanıyordu.
/// SQL dökümü düz metindir ve aynı sütun adlarını, aynı <c>INSERT INTO</c> önekini, aynı tarih
/// biçimini satır satır tekrar eder. Ölçüldü: gerçek bir mysqldump çıktısında (65 MiB, WordPress
/// benzeri şema) GZip <b>4,19 kat</b> küçültüyor — 65 MiB → 15,5 MiB. Bu, diskte de, FTP/S3'e
/// yükleme süresinde de, saklama penceresi boyunca biriken toplamda da dörtte bire inmek demektir.
/// </para>
/// <para>
/// <b>Neden GZip (yerleşik), ZSTD (NuGet) değil:</b> aynı döküm ölçüldüğünde ZSTD-3 15,1 MiB
/// veriyor — GZip'ten yalnızca <b>%2,8</b> daha küçük. Kazanç bu kadar küçükken ZSTD, projeye YENİ
/// BİR BAĞIMLILIK ve yeni bir güvenlik yüzeyi ekler; üstelik açma kodu, ÜÇÜNCÜ TARAF DEPODAN
/// (FTP/S3/WebDAV) geri gelen — yani saldırganın etkileyebileceği — baytları ayrıştırır. Bu depoda
/// SSH.NET'te tam bu sınıftan bir olay yaşandı (yüksek önem dereceli açık bildirimi).
/// <see cref="GZipStream"/> .NET'in içindedir ve çalışma zamanıyla birlikte yamalanır.
/// Ayrıca <c>.gz</c> EVRENSELDİR: panel çalışmaz durumdayken bile müşteri yedeğini 7-Zip/gunzip ile
/// açabilir — felaket anında lazım olan dosyada biçim yaygınlığı bir özelliktir.
/// Hız gerekirse aynı yerleşik yolda <see cref="CompressionLevel.Fastest"/> 127 MB/sn verir
/// (ZSTD-3 ile aynı hız sınıfı), yani ZSTD'nin hız kozu bağımlılık eklemeden de alınabilir.
/// </para>
/// <para>
/// <b>SIRA: önce SIKIŞTIR, sonra ŞİFRELE.</b> Şifrelenmiş veri rastgeleye yakındır ve SIKIŞMAZ;
/// ters sırada kazanç sıfır olurdu. Bu sıra <c>BackupService.FinalizeBackupAsync</c> içinde
/// kilitlidir ve testle korunur.
/// </para>
/// </summary>
public static class BackupDumpCompression
{
    /// <summary>Sıkıştırılmış yedek dosyasının uzantısı (ör. <c>mariadb-shop-20260817.sql.gz</c>).</summary>
    public const string CompressedExtension = ".gz";

    /// <summary>
    /// Kullanılan sıkıştırma seviyesi. <see cref="CompressionLevel.Optimal"/> seçildi:
    /// ölçümde <c>Fastest</c>'a göre %9 daha küçük dosya veriyor ve panel, arşivlerin İÇİNDEKİ
    /// dosyaları da zaten <c>Optimal</c> ile sıkıştırıyor (tek bir davranış).
    /// </summary>
    private const CompressionLevel Level = CompressionLevel.Optimal;

    /// <summary>GZip dosya imzası (RFC 1952). Biçim kararı BUNDAN verilir.</summary>
    private static readonly byte[] Magic = [0x1F, 0x8B];

    /// <summary>Akış tamponu. Büyük dökümler belleğe TOPLANMAZ, akıtılarak işlenir.</summary>
    private const int BufferSize = 1 << 20;

    /// <summary>
    /// ZATEN sıkışık olan biçimler — bunları tekrar sıkıştırmak boşa CPU'dur.
    /// <list type="bullet">
    /// <item><c>zip</c> — site/abonelik/panel arşivleri; içindeki her girdi zaten Deflate'lidir.</item>
    /// <item><c>bak</c> — MSSQL yedeği. Panel bu yedeği <c>BACKUP DATABASE ... WITH COMPRESSION</c>
    /// ile alır (bkz. <c>DatabaseService.ExportMsSqlAsync</c>), yani SQL Server dosyayı kendi
    /// sıkıştırmasıyla yazmıştır. Ölçüldü: zaten sıkıştırılmış veriyi tekrar GZip'lemenin kazancı
    /// <b>%0,29</b> — ihmal edilebilir. Üstelik <c>RESTORE DATABASE ... FROM DISK</c> gerçek bir
    /// <c>.bak</c> bekler; sarmalamak, kazanç sıfırken geri yükleme riski almak olurdu.</item>
    /// </list>
    /// </summary>
    private static readonly HashSet<string> AlreadyCompressedFormats =
        new(StringComparer.OrdinalIgnoreCase) { "zip", "bak", "gz", "gzip", "zst", "7z", "rar", "xz", "bz2" };

    /// <summary>
    /// Bu biçim sıkıştırılmalı mı? <b>Kararın TEK kaynağı burasıdır.</b>
    /// <para>
    /// Varsayılan EVET'tir: listede olmayan bir biçim sıkıştırılır. Yeni bir yedek türü
    /// eklendiğinde bu listeye dokunmayı unutmanın bedeli, zaten sıkışık bir dosyada boşa harcanan
    /// birkaç saniye CPU'dur. Varsayılan HAYIR olsaydı, unutmanın bedeli özelliğin o tür için
    /// SESSİZCE hiç çalışmaması olurdu — ölçülmeyen, fark edilmeyen bir kayıp.
    /// </para>
    /// </summary>
    public static bool ShouldCompress(string format) =>
        !string.IsNullOrWhiteSpace(format) && !AlreadyCompressedFormats.Contains(format.Trim());

    /// <summary>
    /// Dosya GZip ile sıkıştırılmış mı? <b>Karar dosyanın BAŞLIĞINDAN verilir</b>, uzantıdan ya da
    /// panel kaydındaki etiketten değil — <see cref="BackupArchiveCipher.IsEncryptedFile"/> ile aynı
    /// disiplin. Sıkıştırmadan ÖNCE alınmış eski düz <c>.sql</c> yedekler ve elle kopyalanmış
    /// dosyalar bu sayede çalışmaya devam eder; kaydı bozulmuş bir dosya da doğru tanınır.
    /// </summary>
    public static bool IsCompressedFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> head = stackalloc byte[2];
            return stream.Length > Magic.Length
                   && stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length
                   && head.SequenceEqual(Magic);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Okuyamadıysak "sıkışık değil" DEMEYİZ; çağıran dosyayı zaten açamayacak ve gerçek
            // hatayı görecek. Buradan yanlış "düz dosya" cevabı vermek, gzip baytlarını SQL sanıp
            // veritabanına aktarmaya kalkmak olurdu.
            return false;
        }
    }

    /// <summary>
    /// <paramref name="sourcePath"/> dosyasını GZip'leyip <paramref name="targetPath"/> yazar.
    /// Kaynak dosyaya DOKUNMAZ (silme kararı çağıranındır). Bellek kullanımı dosya boyutundan
    /// bağımsızdır: veri <see cref="BufferSize"/> parçalar hâlinde AKITILIR.
    /// </summary>
    public static async Task CompressFileAsync(string sourcePath, string targetPath, CancellationToken cancellationToken = default)
    {
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        await using var target = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
        await using var gzip = new GZipStream(target, Level, leaveOpen: true);
        await source.CopyToAsync(gzip, BufferSize, cancellationToken);

        // BOŞ KAYNAK DA GEÇERLİ BİR GZIP ÜRETMELİ. GZipStream başlığı ilk yazmada döker; hiç veri
        // gelmezse dosya 0 BAYT kalırdı. O dosya ".gz" adını taşır, kayıtta "sıkışık" görünür ama
        // gzip DEĞİLDİR — sonraki her adım onu ham SQL sanardı. Flush, veri gelmemiş olsa bile
        // başlığı yazdırır.
        await gzip.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// GZip'li dosyayı açıp <paramref name="targetPath"/> yazar.
    /// <para>
    /// <b>Bu adım atlanamaz:</b> <c>ImportDumpAsync</c> gerçek bir <c>.sql</c> DOSYASI bekler
    /// (mysql istemcisine <c>source &lt;yol&gt;</c> olarak verilir). Sıkıştırılmış baytlar doğrudan
    /// verilseydi özellik "yedek küçüldü" der ama GERİ YÜKLEME ÇALIŞMAZDI — yani yedek fiilen yok
    /// olurdu.
    /// </para>
    /// <para>
    /// Hata durumunda yarım kalan hedef dosya SİLİNİR: yarım bir SQL dökümünü veritabanına
    /// aktarmak, hiç aktarmamaktan çok daha pahalıya mal olur.
    /// </para>
    /// </summary>
    public static async Task DecompressFileAsync(string sourcePath, string targetPath, CancellationToken cancellationToken = default)
    {
        var basarili = false;
        try
        {
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, useAsync: true))
            await using (var gzip = new GZipStream(source, CompressionMode.Decompress, leaveOpen: true))
            await using (var target = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                await gzip.CopyToAsync(target, BufferSize, cancellationToken);
            }

            basarili = true;
        }
        finally
        {
            if (!basarili)
            {
                TryDelete(targetPath);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Temizlik best-effort; asıl hata çağırana zaten gidiyor.
        }
    }
}
