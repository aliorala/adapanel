namespace AdaPanel.Core.Dtos.Proxy;

public sealed class RewriteRuleDto
{
    public string Name { get; init; } = string.Empty;

    public string MatchPattern { get; init; } = string.Empty;

    public string RewriteUrl { get; init; } = string.Empty;

    public bool StopProcessing { get; init; }

    public bool AppendQueryString { get; init; }

    public bool IgnoreCase { get; init; }
}
