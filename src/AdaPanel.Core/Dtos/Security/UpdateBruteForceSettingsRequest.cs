using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Security;

public sealed class UpdateBruteForceSettingsRequest
{
    public bool Enabled { get; set; } = true;

    [Range(1, 1000)]
    public int MaxFailedAttempts { get; set; } = 10;

    [Range(1, 1440)]
    public int WindowMinutes { get; set; } = 15;

    [Range(0, 525600)]
    public int BanMinutes { get; set; } = 60;

    public IReadOnlyCollection<string> WhitelistedIps { get; set; } = [];
}
