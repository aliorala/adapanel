using AdaPanel.Core.Dtos.Security;

namespace AdaPanel.Core.Interfaces;

/// <summary>
/// Panel girişine yönelik brute-force saldırılarını izler ve eşik aşıldığında
/// saldırgan IP'yi Windows Firewall üzerinden süreli olarak engeller.
/// </summary>
public interface IBruteForceProtectionService
{
    Task<BruteForceStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<BruteForceStatusDto> UpdateSettingsAsync(UpdateBruteForceSettingsRequest request, CancellationToken cancellationToken = default);

    /// <summary>Başarısız bir giriş kaydeder; eşik aşıldıysa IP'yi engeller. Login akışını bozmaz.</summary>
    Task RegisterFailedAttemptAsync(string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Başarılı giriş sonrası IP için sayaçları temizler.</summary>
    Task RegisterSuccessAsync(string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Bir IP'nin engelini elle kaldırır.</summary>
    Task<BruteForceStatusDto> UnbanAsync(string ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Süresi dolmuş engelleri firewall'dan kaldırır (zamanlayıcı tarafından çağrılır).</summary>
    Task CleanupExpiredBansAsync(CancellationToken cancellationToken = default);
}
