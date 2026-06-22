using AdaPanel.Core.Dtos.Mail;

namespace AdaPanel.Core.Interfaces;

/// <summary>Ters DNS (PTR) dogrulama. Yazmaz, yalnizca kontrol edip yonlendirir.</summary>
public interface IRdnsService
{
    /// <summary>Verilen hostname (bossa panel sunucu adresi) icin PTR kaydini dogrular.</summary>
    Task<RdnsCheckDto> CheckAsync(string? hostName, CancellationToken cancellationToken = default);
}
