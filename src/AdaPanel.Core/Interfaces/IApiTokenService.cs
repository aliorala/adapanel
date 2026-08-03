using AdaPanel.Core.Dtos.Ai;
using AdaPanel.Core.Entities;

namespace AdaPanel.Core.Interfaces;

/// <summary>
/// Kişisel erişim anahtarlarını yönetir ve doğrular (MCP bağlantıları başta olmak üzere).
/// Her kullanıcı KENDİ anahtarını oluşturur; anahtarın yetkisi sahibinin rolünden türer.
/// </summary>
public interface IApiTokenService
{
    Task<IReadOnlyList<ApiTokenDto>> GetForUserAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Yeni anahtar üretir. Düz metin değer YALNIZCA bu çağrının yanıtında döner.</summary>
    Task<CreatedApiTokenDto> CreateAsync(Guid userId, CreateApiTokenRequest request, CancellationToken cancellationToken = default);

    /// <summary>Anahtarı iptal eder. Kayıt silinmez; denetim izi korunur.</summary>
    Task RevokeAsync(Guid userId, Guid tokenId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Kullanıcının TÜM etkin anahtarlarını iptal eder ve iptal edilen sayıyı döner.
    /// <para>
    /// Parola KURTARMA yolunda çağrılır: parolasını unutup sıfırlayan ya da parolası yönetici
    /// tarafından değiştirilen bir hesapta "ele geçirilmiş olabilir" varsayımı geçerlidir. Anahtar
    /// ayrı bir kimlik bilgisidir ve parola değişiminden etkilenmez; bu yüzden açıkça düşürülmezse
    /// saldırgan parola sıfırlansa bile MCP üzerinden erişimini sürdürür.
    /// </para>
    /// </summary>
    Task<int> RevokeAllForUserAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sunulan düz metin anahtarı doğrular. Geçerliyse anahtar kaydını döner, değilse null.
    /// Son kullanım anını günceller.
    /// </summary>
    Task<PanelApiToken?> ValidateAsync(string presentedToken, CancellationToken cancellationToken = default);
}
