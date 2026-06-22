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
}
