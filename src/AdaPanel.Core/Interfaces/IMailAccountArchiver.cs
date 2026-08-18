using AdaPanel.Core.Dtos.Mail;

namespace AdaPanel.Core.Interfaces;

/// <summary>
/// TEK BİR ALAN ADININ posta kutularını (iletiler dahil) arşivler ve geri yükler.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sunucu geneli posta yedeğinden (<c>IBackupService.BackupMailAsync</c>) FARKI:</b> o, Stalwart
/// veri deposunun TAMAMINI kopyalar ve bunu yapabilmek için servisi DURDURUR; geri yüklemesi bütün
/// alan adlarını o günkü hâline döndürür — yani felaket kurtarmadır (sunucu komple gitti).
/// Buradaki akış ise hesap başına çalışır, servis ÇALIŞIRKEN yapılır ve geri yükleme YALNIZ hedef
/// hesaba yazar; diğer müşterilerin postasına DOKUNMAZ — yani "tek siteyi geri yükle"dir.
/// İkisi birbirinin yerine geçmez, ikisi de gereklidir.
/// </para>
/// <para>
/// <b>Nasıl çalışır:</b> Stalwart'ın resmî aracı <c>vandelay</c> ile JMAP üzerinden. Panel müşteri
/// parolalarını BİLMEZ; bağlantı YÖNETİCİ kimliğiyle kurulur ve hedef hesap <c>--account-name</c>
/// ile seçilir (Stalwart'ın <c>impersonate</c> izni). Hesap parolası ne okunur ne değiştirilir.
/// </para>
/// </remarks>
public interface IMailAccountArchiver
{
    /// <summary>
    /// Posta ARŞİVLENEBİLİR Mİ? Yedek almadan ÖNCE ölçülür; hazır değilse çağıran arşive posta
    /// koymaz ve "posta dahil" DEMEZ.
    /// </summary>
    Task<MailArchivePreflightDto> CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Alan adının bütün hesaplarını <paramref name="outputDirectory"/> altına hesap başına birer
    /// SQLite arşivi olarak yazar. Sunucu ÇALIŞIRKEN yapılır.
    /// </summary>
    Task<MailArchiveResultDto> ArchiveDomainAsync(
        string domain,
        string outputDirectory,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// <paramref name="archiveDirectory"/> altındaki hesap arşivlerini sunucuya geri yazar.
    /// YALNIZ arşivdeki hesaplara yazar; başka hiçbir alan adına dokunmaz.
    /// </summary>
    /// <returns>Geri yüklenemeyen hesaplar ve nedenleri (boş = hepsi geri yüklendi).</returns>
    Task<IReadOnlyList<string>> RestoreDomainAsync(
        string domain,
        string archiveDirectory,
        CancellationToken cancellationToken = default);
}
