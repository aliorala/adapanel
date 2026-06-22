namespace AdaPanel.Core.Dtos.Security;

public sealed class BruteForceStatusDto
{
    public BruteForceSettingsDto Settings { get; set; } = new();

    public IReadOnlyCollection<BannedIpDto> BannedIps { get; set; } = [];
}
