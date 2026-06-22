using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Git;

public sealed class ConfigureGitDeploymentRequest
{
    [Required]
    [StringLength(2048, MinimumLength = 4)]
    public string RepositoryUrl { get; set; } = string.Empty;

    [StringLength(256)]
    public string Branch { get; set; } = "main";

    /// <summary>true ise webhook gizli anahtarı yeniden üretilir.</summary>
    public bool RegenerateSecret { get; set; }
}
