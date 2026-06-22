namespace AdaPanel.Core.Dtos.Git;

public sealed class GitDeployResultDto
{
    public bool Success { get; set; }

    public string Commit { get; set; } = string.Empty;

    public string Output { get; set; } = string.Empty;

    public DateTime DeployedAtUtc { get; set; }

    public GitDeploymentDto Deployment { get; set; } = new();
}
