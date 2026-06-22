namespace AdaPanel.Core.Dtos.Waf;

public sealed class SiteSecurityConfigurationDto
{
    public string SiteName { get; set; } = string.Empty;

    public string ApplicationPoolName { get; set; } = string.Empty;

    public string PhysicalPath { get; set; } = string.Empty;

    public bool IsIsolated { get; set; }

    public bool FolderAclApplied { get; set; }

    public bool BasicRulesEnabled { get; set; }

    public int RequestsPerMinute { get; set; }

    public string Principal { get; set; } = string.Empty;
}
