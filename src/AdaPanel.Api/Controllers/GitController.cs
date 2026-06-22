using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Git;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/git")]
public sealed class GitController(IGitDeploymentService gitDeploymentService) : ControllerBase
{
    [HttpGet("{siteName}")]
    public async Task<ActionResult<ApiResponse<GitDeploymentDto>>> GetAsync(string siteName, CancellationToken cancellationToken)
    {
        var deployment = await gitDeploymentService.GetAsync(siteName, cancellationToken);
        return Ok(ApiResponse<GitDeploymentDto>.Ok(deployment));
    }

    [HttpPut("{siteName}")]
    public async Task<ActionResult<ApiResponse<GitDeploymentDto>>> ConfigureAsync(
        string siteName,
        [FromBody] ConfigureGitDeploymentRequest request,
        CancellationToken cancellationToken)
    {
        var deployment = await gitDeploymentService.ConfigureAsync(siteName, request, cancellationToken);
        return Ok(ApiResponse<GitDeploymentDto>.Ok(deployment, "git.configured"));
    }

    [HttpPost("{siteName}/deploy")]
    public async Task<ActionResult<ApiResponse<GitDeployResultDto>>> DeployAsync(string siteName, CancellationToken cancellationToken)
    {
        var result = await gitDeploymentService.DeployAsync(siteName, cancellationToken);
        return Ok(ApiResponse<GitDeployResultDto>.Ok(result, result.Success ? "git.deployed" : "git.deploy_failed"));
    }

    /// <summary>Git sağlayıcısı (GitHub/GitLab) push webhook'u için anonim tetikleyici. Gizli token ile doğrulanır.</summary>
    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    [HttpPost("{siteName}/webhook")]
    public async Task<ActionResult<ApiResponse<GitDeployResultDto>>> WebhookAsync(
        string siteName,
        [FromQuery] string token,
        CancellationToken cancellationToken)
    {
        var result = await gitDeploymentService.DeployViaWebhookAsync(siteName, token ?? string.Empty, cancellationToken);
        return Ok(ApiResponse<GitDeployResultDto>.Ok(result, result.Success ? "git.deployed" : "git.deploy_failed"));
    }
}
