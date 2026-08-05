using AdaPanel.Core.Dtos.Sites;

namespace AdaPanel.Core.Interfaces;

/// <summary>
/// Site içinde bir dizini HTTP Basic Auth ile korur (panel "Password-Protected Directories").
/// IIS URL Rewrite kullanan kendi-içeren bir web.config yazar; Windows hesabı gerektirmez.
/// </summary>
public interface IDirectoryProtectionService
{
    Task<IReadOnlyCollection<ProtectedDirectoryDto>> GetProtectedDirectoriesAsync(string siteName, CancellationToken cancellationToken = default);

    Task<ProtectedDirectoryDto> ProtectAsync(string siteName, ProtectDirectoryRequest request, CancellationToken cancellationToken = default);

    Task UnprotectAsync(string siteName, string relativePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Site silindiğinde onun korumalı dizin kayıtlarını kaldırır.
    /// <para>
    /// Kayıt kalırsa aynı adla açılan yeni bir sitede, hiç kurulmamış parola korumaları
    /// listelenir.
    /// </para>
    /// </summary>
    Task RemoveSiteAsync(string siteName, CancellationToken cancellationToken = default);
}
