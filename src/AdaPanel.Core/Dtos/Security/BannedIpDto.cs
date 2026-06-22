namespace AdaPanel.Core.Dtos.Security;

public sealed class BannedIpDto
{
    public string IpAddress { get; set; } = string.Empty;

    public DateTime BannedAtUtc { get; set; }

    public DateTime? ExpiresAtUtc { get; set; }

    public int FailedAttempts { get; set; }

    public string FirewallRuleName { get; set; } = string.Empty;
}
