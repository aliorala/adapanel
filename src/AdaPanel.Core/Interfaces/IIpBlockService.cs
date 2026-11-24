using AdaPanel.Core.Dtos.Firewall;

namespace AdaPanel.Core.Interfaces;

/// <summary>
/// Panelin IP/CIDR engel listesi (sureli engel dahil). Kurallar Windows Firewall'da PANELE AIT onekle
/// (<c>AdaPanel IpBlock </c>) yazilir; panel kendi yonetim IP'sini ve aktif yonetici oturum IP'lerini
/// ENGELLEYEMEZ (kilitlenme korumasi).
/// </summary>
public interface IIpBlockService
{
    Task<IpBlockListDto> GetAsync(CancellationToken cancellationToken = default);

    /// <param name="protectedAddresses">
    /// Engellenmemesi gereken adresler (istegi yapan yonetici IP'si, son oturum acan yonetici IP'leri).
    /// Panelin kendi arayuz adresleri servis tarafindan eklenir.
    /// </param>
    Task<IpBlockEntryDto> AddAsync(AddIpBlockRequest request, string actor,
        IReadOnlyCollection<string> protectedAddresses, CancellationToken cancellationToken = default);

    Task<IpBlockEntryDto> RemoveAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Suresi dolan engelleri kaldirir (kural + kayit). Kaldirilan girdileri doner.</summary>
    Task<IReadOnlyCollection<IpBlockEntryDto>> SweepExpiredAsync(CancellationToken cancellationToken = default);
}
