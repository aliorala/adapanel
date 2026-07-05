using AdaPanel.Core.Dtos.Antivirus;

namespace AdaPanel.Core.Interfaces;

/// <summary>
/// ClamAV tabanlı dosya antivirüsü (Plesk antivirüs paritesi). Motor, yazılım kataloğundaki
/// "clamav" bileşenidir; tanımlar freshclam ile indirilir, tarama clamscan ile isteğe bağlı yapılır
/// (daemon yok — panel sunucusunda kalıcı bellek maliyeti bilinçli olarak alınmaz).
/// </summary>
public interface IAntivirusService
{
    Task<AntivirusStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Virüs tanım veritabanını freshclam ile indirir/günceller (ilk sefer ~250 MB).</summary>
    Task<AntivirusStatusDto> UpdateDefinitionsAsync(CancellationToken cancellationToken = default);

    /// <summary>Verilen siteyi veya dizini tarar; sonuç kalıcı kaydedilir (son tarama raporu).</summary>
    Task<AntivirusScanResultDto> ScanAsync(AntivirusScanRequest request, CancellationToken cancellationToken = default);
}
