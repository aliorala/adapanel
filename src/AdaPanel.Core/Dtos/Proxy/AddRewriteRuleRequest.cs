using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Proxy;

public sealed class AddRewriteRuleRequest
{
    [Required]
    [StringLength(128, MinimumLength = 2)]
    public string Name { get; init; } = string.Empty;

    [Required]
    [StringLength(1024, MinimumLength = 1)]
    public string MatchPattern { get; init; } = string.Empty;

    [Required]
    [StringLength(2048, MinimumLength = 1)]
    public string RewriteUrl { get; init; } = string.Empty;

    public bool StopProcessing { get; init; } = true;

    public bool AppendQueryString { get; init; } = true;

    public bool IgnoreCase { get; init; } = true;
}
