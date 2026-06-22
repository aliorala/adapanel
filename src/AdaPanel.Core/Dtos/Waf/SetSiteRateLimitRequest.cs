namespace AdaPanel.Core.Dtos.Waf;

public sealed class SetSiteRateLimitRequest
{
    public int RequestsPerMinute { get; set; } = 120;

    public int Burst { get; set; } = 30;
}
