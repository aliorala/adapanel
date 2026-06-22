namespace AdaPanel.Core.Dtos.Sites;

public sealed class SiteDefaultsResultDto
{
    public string SiteName { get; set; } = string.Empty;

    public string[] ReadyFeatures { get; set; } = [];

    public string[] FailedFeatures { get; set; } = [];

    public bool IsComplete => FailedFeatures.Length == 0;
}
