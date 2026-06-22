namespace AdaPanel.Core.Dtos.Firewall;

public sealed class FirewallRuleDto
{
    public string DisplayName { get; set; } = string.Empty;

    public string Direction { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;

    public string Protocol { get; set; } = string.Empty;

    public string LocalPort { get; set; } = string.Empty;

    public bool Enabled { get; set; }
}
