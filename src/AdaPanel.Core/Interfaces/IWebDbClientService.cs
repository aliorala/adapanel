using AdaPanel.Core.Dtos.WebDb;

namespace AdaPanel.Core.Interfaces;

public interface IWebDbClientService
{
    Task<WebDbWorkspaceDto> GetWorkspaceAsync(CancellationToken cancellationToken = default);

    Task<WebDbDeploymentDto> DeployPhpMyAdminAsync(CancellationToken cancellationToken = default);

    Task<WebDbDeploymentDto> DeployAdminerAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// phpMyAdmin'i signon ile açacak tek seferlik URL üretir. databaseName verilirse o
    /// veritabanının kullanıcısı olarak girer ve yalnız o DB'yi gösterir; boşsa root (tüm DB).
    /// </summary>
    Task<WebDbSignonDto> CreatePhpMyAdminSignonAsync(string engine, string? databaseName, CancellationToken cancellationToken = default);
}
