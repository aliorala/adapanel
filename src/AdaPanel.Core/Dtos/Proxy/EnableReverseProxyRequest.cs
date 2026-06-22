using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Proxy;

public sealed class EnableReverseProxyRequest
{
    [Required]
    [StringLength(2048, MinimumLength = 8)]
    public string DestinationUrl { get; init; } = string.Empty;

    [StringLength(256)]
    public string MountPath { get; init; } = "/";

    public bool EnableHtmlLinkRewrite { get; init; } = true;
}
