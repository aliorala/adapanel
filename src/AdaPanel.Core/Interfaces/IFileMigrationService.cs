using AdaPanel.Core.Dtos.Sites;

namespace AdaPanel.Core.Interfaces;

public interface IFileMigrationService
{
    /// <summary>
    /// Uzak bir FTP/FTPS/SFTP sunucusundan site dosyalarını sitenin kök dizinine özyinelemeli indirir
    /// (Plesk göçü gibi; veritabanı taşımanın dosya eşi). Mevcut dosyaların üzerine yazar.
    /// </summary>
    Task<RemoteFileMigrationResult> PullAsync(RemoteFileMigrationRequest request, CancellationToken cancellationToken = default);
}
