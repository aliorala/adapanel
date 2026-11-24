using AdaPanel.Core.Dtos.Git;

namespace AdaPanel.Core.Interfaces;

/// <summary>
/// Site uygulama köküne Git deposundan dağıtım (clone/pull) ve webhook ile
/// otomatik dağıtım yönetimi.
/// </summary>
public interface IGitDeploymentService
{
    Task<GitDeploymentDto> GetAsync(string siteName, CancellationToken cancellationToken = default);

    Task<GitDeploymentDto> ConfigureAsync(string siteName, ConfigureGitDeploymentRequest request, CancellationToken cancellationToken = default);

    Task<GitDeployResultDto> DeployAsync(string siteName, CancellationToken cancellationToken = default);

    /// <summary>Webhook ile dağıtım; <paramref name="token"/> kayıtlı gizli anahtarla doğrulanır.</summary>
    Task<GitDeployResultDto> DeployViaWebhookAsync(string siteName, string token, CancellationToken cancellationToken = default);

    /// <summary>
    /// Yapılandırmayı, panelin tuttuğu git dizinini, dağıtım geçmişini ve anahtar/jetonları kaldırır.
    /// Site dosyalarına DOKUNMAZ. Siteyi okuyamasa da çalışır (site silme akışı kullanır).
    /// </summary>
    Task<GitRemovalResultDto> RemoveAsync(string siteName, CancellationToken cancellationToken = default);

    /// <summary>Yeni SSH dağıtım anahtarı üretir (öncekini değiştirir). Dönüşte yalnız GENEL anahtar vardır.</summary>
    Task<GitDeploymentDto> GenerateDeployKeyAsync(string siteName, CancellationToken cancellationToken = default);

    Task<GitDeploymentDto> RemoveDeployKeyAsync(string siteName, CancellationToken cancellationToken = default);

    Task<GitDeploymentDto> SetHttpsTokenAsync(string siteName, SetGitHttpsTokenRequest request, CancellationToken cancellationToken = default);

    Task<GitDeploymentDto> RemoveHttpsTokenAsync(string siteName, CancellationToken cancellationToken = default);

    Task<GitDeploymentDto> SetPostDeployAsync(string siteName, SetGitPostDeployRequest request, CancellationToken cancellationToken = default);

    /// <summary>Seçilen dağıtım kaydındaki ÖNCEKİ commit'e geri döner (derleme sonrası komutlar yeniden çalışır).</summary>
    Task<GitDeployResultDto> RollbackAsync(string siteName, string deploymentId, CancellationToken cancellationToken = default);
}
