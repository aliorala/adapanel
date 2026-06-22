namespace AdaPanel.Core.Dtos.Proxy;

public sealed class ReverseProxySiteConfigurationDto
{
    public string SiteName { get; init; } = string.Empty;

    public string WebConfigPath { get; init; } = string.Empty;

    public bool IsArrEnabled { get; init; }

    public bool IsProxyEnabled { get; init; }

    public string DestinationUrl { get; init; } = string.Empty;

    public string MountPath { get; init; } = "/";

    public bool HasHtmlLinkRewrite { get; init; }

    public bool PreserveHostHeader { get; init; }

    public bool ReverseRewriteHostInResponseHeaders { get; init; }

    public IReadOnlyCollection<RewriteRuleDto> RewriteRules { get; init; } = [];
}
