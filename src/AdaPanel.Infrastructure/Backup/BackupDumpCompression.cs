using System.IO.Compression;
using AdaPanel.Core.Common;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace AdaPanel.Infrastructure.Backup;

/// <summary>
/// Yedek dosyalarının SIKIŞTIRILMASI/açılması. Panelin SIKIŞTIRMA BİÇİMİ VE KARARI BURADADIR;
/// başka hiçbir yerde "bunu sıkıştıralım mı" sorusu sorulmaz (tek kaynak).
/// <para>
/// <b>Neden gerekli:</b> tek başına alınan veritabanı yedekleri HAM <c>.sql</c> olarak saklanıyordu.
/// SQL dökümü düz metindir ve aynı sütun adlarını, aynı <c>INSERT INTO</c> önekini, aynı tarih
/// biçimini satır satır tekrar eder. Ölçüldü (gerçek mysqldump çıktısı, 65,07 MiB, WordPress benzeri
/// şema): ZSTD-3 <b>4,31 kat</b> küçültüyor — 65,07 MiB → 15,09 MiB. Bu, diskte de, FTP/S3'e
/// yükleme süresinde de, saklama penceresi boyunca biriken toplamda da dörtte bire inmek demektir.
/// </para>
/// <para>
/// <b>Neden ZSTD:</b> Plesk paritesi. Plesk yedek dökümlerini sıkıştırarak saklar ve <b>Obsidian
/// 18.0.40'tan itibaren GZIP'i bırakıp ZSTD'ye geçmiştir</b>; gerekçesi yedek ve geri yükleme
/// HIZIDIR. Ölçüm bunu doğruluyor: aynı dökümde ZSTD-3, GZip-Optimal'den hem <b>%2,8 daha küçük</b>
/// (15,09 / 15,53 MiB) hem de <b>4,3 kat daha hızlı</b> (0,61 sn / 2,61 sn); açması da daha hızlı
/// (0,17 sn / 0,20 sn). Gecelik yedek global kilidin içinde çalıştığı için sıkıştırma süresi
/// doğrudan kilit süresidir.
/// </para>
/// <para>
/// <b>SIRA: önce SIKIŞTIR, sonra ŞİFRELE.</b> Şifrelenmiş veri rastgeleye yakındır ve SIKIŞMAZ;
/// ters sırada kazanç sıfır olurdu. Bu sıra <c>BackupService.FinalizeBackupAsync</c> içinde
/// kilitlidir ve testle korunur.
/// </para>
/// </summary>
public static class BackupDumpCompression
{
    /// <summary>
    /// Sıkıştırılmış yedek dosyasının uzantısı (ör. <c>mariadb-shop-20260817.sql.zst</c>).
    /// İçerik gerçekten ZSTD olduğu için uzantı da <c>.zst</c>'dir: <c>.gz</c> demek, kullanıcı
    /// dosyayı 7-Zip/gunzip ile açmaya kalktığında yanlış yola sokmak olurdu.
    /// </summary>
    public const string CompressedExtension = ".zst";

    /// <summary>
    /// Kullanılan ZSTD seviyesi — <b>tek yerde adlandırılmış sabit</b>.
    /// <para>
    /// <c>3</c> seçildi (zstd'nin kendi varsayılanı): ölçümde oran ve hızın BİRLİKTE en iyi olduğu
    /// nokta — 4,31x, 0,61 sn. Üst seviyeler bedelini hak etmiyor: <c>-9</c> %4,3 küçültme için
    /// süreyi 6,7 katına (4,09 sn), <c>-19</c> ise %24,7 küçültme için <b>74,75 saniyeye</b>
    /// (122 kat) çıkarıyor — gecelik yedeği global kilidin içinde gereksiz uzatırdı.
    /// </para>
    /// </summary>
    private const int CompressionLevel = 3;

    /// <summary>
    /// ZSTD çerçeve SAĞLAMA TOPLAMI (XXH64) — <b>AÇIK</b>. Zstd'nin kendi varsayılanı KAPALIDIR ve
    /// bu, bir yedek dosyasında kabul edilemez.
    /// <para>
    /// <b>Ölçüldü:</b> sağlama kapalıyken sıkıştırılmış gövdeye atılan 40 rastgele bit
    /// çevirmesinin <b>19'u hiçbir hata vermeden SESSİZCE BOZUK SQL üretti</b> — yani bit çürümüş
    /// ya da yarım inmiş bir yedek, müşterinin canlı veritabanına çöp olarak "geri yüklenirdi".
    /// Sağlama açıkken aynı denemede <b>40/40 yakalandı</b>. Bedeli 4 bayt (7.034 → 7.038).
    /// </para>
    /// <para>
    /// Bu, biçim GZip'ten ZSTD'ye çevrilirken kaybedilmemesi gereken bir güvencedir: GZip'in
    /// sonundaki CRC32 bu hataları zaten yakalıyordu. Sağlamayı açmadan ZSTD'ye geçmek, dosyayı
    /// küçültürken BÜTÜNLÜK DENETİMİNİ sessizce kaldırmak olurdu.
    /// </para>
    /// </summary>
    private const int ChecksumEnabled = 1;

    /// <summary>ZSTD çerçeve imzası (RFC 8878, <c>0xFD2FB528</c> küçük-endian). Biçim kararı BUNDAN verilir.</summary>
    private static readonly byte[] ZstandardMagic = [0x28, 0xB5, 0x2F, 0xFD];

    /// <summary>GZip dosya imzası (RFC 1952). Yalnızca OKUMA yolunda tanınır; artık üretilmez.</summary>
    private static readonly byte[] GZipMagic = [0x1F, 0x8B];

    /// <summary>Akış tamponu. Büyük dökümler belleğe TOPLANMAZ, akıtılarak işlenir.</summary>
    private const int BufferSize = 1 << 20;

    /// <summary>
    /// ZATEN sıkışık olan biçimler — bunları tekrar sıkıştırmak boşa CPU'dur.
    /// <list type="bullet">
    /// <item><c>zip</c> — site/abonelik/panel arşivleri; içindeki her girdi zaten Deflate'lidir.</item>
    /// <item><c>bak</c> — MSSQL yedeği. Panel bu yedeği <c>BACKUP DATABASE ... WITH COMPRESSION</c>
    /// ile alır (bkz. <c>DatabaseService.ExportMsSqlAsync</c>), yani SQL Server dosyayı kendi
    /// sıkıştırmasıyla yazmıştır. Ölçüldü: zaten sıkıştırılmış veriyi tekrar sıkıştırmanın kazancı
    /// <b>%0,29</b> — ihmal edilebilir. Üstelik <c>RESTORE DATABASE ... FROM DISK</c> gerçek bir
    /// <c>.bak</c> bekler; sarmalamak, kazanç sıfırken geri yükleme riski almak olurdu.</item>
    /// </list>
    /// </summary>
    private static readonly HashSet<string> AlreadyCompressedFormats =
        new(StringComparer.OrdinalIgnoreCase) { "zip", "bak", "gz", "gzip", "zst", "zstd", "7z", "rar", "xz", "bz2" };

    /// <summary>Dosyanın taşıdığı sıkıştırma biçimi.</summary>
    public enum DumpFormat
    {
        /// <summary>Sıkıştırılmamış (düz <c>.sql</c> vb.).</summary>
        None,

        /// <summary>ZSTD — panelin BUGÜN ürettiği biçim.</summary>
        Zstandard,

        /// <summary>GZip — yalnızca okunur (aşağıdaki gerekçeye bakınız).</summary>
        GZip
    }

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
    /// Dosyanın sıkıştırma biçimini DOSYANIN BAŞLIĞINDAN okur — uzantıdan ya da panel kaydındaki
    /// etiketten değil (<see cref="BackupArchiveCipher.IsEncryptedFile"/> ile aynı disiplin).
    /// <para>
    /// <b>Üç biçim de tanınır ve bu bilinçlidir:</b>
    /// <list type="number">
    /// <item><b>Düz</b> — sıkıştırma özelliğinden ÖNCE alınmış bütün <c>.sql</c> yedekler. Bunlar
    /// sahada VARDIR ve çalışmaya devam etmek ZORUNDADIR.</item>
    /// <item><b>ZSTD</b> — panelin bugün ürettiği biçim.</item>
    /// <item><b>GZip</b> — panel bunu artık ÜRETMEZ (bu özellik önce GZip ile yazıldı, yayınlanmadan
    /// ZSTD'ye çevrildi). Yine de tanınıyor: maliyeti .NET'te zaten bulunan <c>GZipStream</c>'e bir
    /// dallanma, karşılığı ise ara sürümle üretilmiş ya da yönetici tarafından elle <c>gzip</c>'lenmiş
    /// bir dökümün SESSİZCE geri yüklenemez olmaması. Bir yedeği okuyamamanın bedeli, birkaç satır
    /// kodun bedelinden kat kat yüksektir.</item>
    /// </list>
    /// </para>
    /// </summary>
    public static DumpFormat DetectFormat(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> head = stackalloc byte[4];
            var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);

            if (read >= ZstandardMagic.Length && head[..ZstandardMagic.Length].SequenceEqual(ZstandardMagic))
            {
                return DumpFormat.Zstandard;
            }

            return read >= GZipMagic.Length && head[..GZipMagic.Length].SequenceEqual(GZipMagic)
                ? DumpFormat.GZip
                : DumpFormat.None;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Okuyamadıysak "sıkışık değil" DEMEYİZ; çağıran dosyayı zaten açamayacak ve gerçek
            // hatayı görecek. Buradan yanlış "düz dosya" cevabı vermek, sıkıştırılmış baytları SQL
            // sanıp veritabanına aktarmaya kalkmak olurdu.
            return DumpFormat.None;
        }
    }

    /// <summary>Dosya sıkıştırılmış mı? Karar <see cref="DetectFormat"/>'tan gelir.</summary>
    public static bool IsCompressedFile(string path) => DetectFormat(path) != DumpFormat.None;

    /// <summary>
    /// <paramref name="sourcePath"/> dosyasını ZSTD ile sıkıştırıp <paramref name="targetPath"/>
    /// yazar. Kaynak dosyaya DOKUNMAZ (silme kararı çağıranındır). Bellek kullanımı dosya boyutundan
    /// bağımsızdır: veri <see cref="BufferSize"/> parçalar hâlinde AKITILIR.
    /// </summary>
    public static async Task CompressFileAsync(string sourcePath, string targetPath, CancellationToken cancellationToken = default)
    {
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        await using var target = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);

        // İç blok ZORUNLU: ZSTD çerçevesinin kapanış damgası ancak sıkıştırıcı kapanırken yazılır.
        // Hedef dosya ondan ÖNCE kapansaydı, çerçevesi eksik — yani açılamayan — bir dosya kalırdı.
        // (Boş kaynakta bile geçerli bir 9 baytlık çerçeve üretilir; ölçüldü.)
        using (var compressor = new CompressionStream(target, CompressionLevel, leaveOpen: true))
        {
            compressor.SetParameter(ZSTD_cParameter.ZSTD_c_checksumFlag, ChecksumEnabled);
            await source.CopyToAsync(compressor, BufferSize, cancellationToken);
        }
    }

    /// <summary>
    /// Sıkıştırılmış dosyayı açıp <paramref name="targetPath"/> yazar. Biçim dosyanın başlığından
    /// bulunur; düz dosya verilirse hata yükselir (çağıran zaten <see cref="IsCompressedFile"/> ile
    /// sorar).
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
    /// <exception cref="AppException">
    /// <c>backup.decrypt_corrupt</c> — arşiv bozuk/kesilmiş, açılamadı.
    /// </exception>
    public static async Task DecompressFileAsync(string sourcePath, string targetPath, CancellationToken cancellationToken = default)
    {
        var format = DetectFormat(sourcePath);
        var basarili = false;
        try
        {
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, useAsync: true))
            await using (var target = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                switch (format)
                {
                    case DumpFormat.Zstandard:
                        using (var decompressor = new DecompressionStream(source, leaveOpen: true))
                        {
                            await decompressor.CopyToAsync(target, BufferSize, cancellationToken);
                        }

                        break;

                    // Panel artık GZip ÜRETMEZ; yalnızca okur. Gerekçe DetectFormat'ta.
                    case DumpFormat.GZip:
                        await using (var decompressor = new GZipStream(source, CompressionMode.Decompress, leaveOpen: true))
                        {
                            await decompressor.CopyToAsync(target, BufferSize, cancellationToken);
                        }

                        break;

                    default:
                        throw new AppException("backup.decrypt_corrupt", 400, 400, "Dosya taninan bir sikistirma bicimi tasimiyor.");
                }
            }

            basarili = true;
        }
        catch (Exception exception) when (exception is ZstdException or InvalidDataException)
        {
            // BOZUK ARŞİV NET SÖYLENİR. Sarmalanmasaydı kullanıcı "Dictionary mismatch" gibi hiçbir
            // şey anlatmayan bir iç hata görürdü (middleware bunu common.unexpected_error yapardı).
            //
            // Anahtar bilerek MEVCUT bir anahtardır: kullanıcıya giden metin ("Yedek dosyası bozuk
            // ya da değiştirilmiş... Hiçbir şey geri yüklenmedi.") bu durumu birebir anlatıyor ve
            // bu görevde i18n dosyalarına dokunulmuyor. Ayrı bir backup.archive_corrupt anahtarı
            // eklenecekse çeviriyle BİRLİKTE eklenmelidir (bkz. scratchpad/i18n-sikistirma.md).
            throw new AppException("backup.decrypt_corrupt", 400, 400, exception.Message);
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
