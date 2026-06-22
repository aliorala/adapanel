namespace AdaPanel.Core.Dtos.Firewall;

public sealed class UpdateFirewallRuleRequest
{
    public int Port { get; set; }

    public string Protocol { get; set; } = "TCP";

    public string RuleName { get; set; } = string.Empty;
}
