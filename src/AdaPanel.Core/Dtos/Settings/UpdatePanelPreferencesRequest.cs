using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Settings;

public sealed class UpdatePanelPreferencesRequest
{
    [Required]
    [StringLength(64, MinimumLength = 2)]
    public string PanelName { get; init; } = "AdaPanel";

    [Required]
    [RegularExpression("^(tr|en)$")]
    public string DefaultLanguage { get; init; } = "tr";

    [Required]
    [RegularExpression("^(dark|light|system)$")]
    public string ThemeMode { get; init; } = "light";

    [Required]
    [RegularExpression("^(compact|comfortable)$")]
    public string UiDensity { get; init; } = "compact";

    [Required]
    [StringLength(1024, MinimumLength = 3)]
    public string DefaultSiteRoot { get; init; } = "C:\\inetpub\\wwwroot";

    [Required]
    [StringLength(1024, MinimumLength = 3)]
    public string DefaultBackupRoot { get; init; } = "%ProgramData%\\AdaPanel\\backups";

    [StringLength(255)]
    public string ServerAddress { get; init; } = string.Empty;

    [Range(1, 65535)]
    public int PreferredHttpsPort { get; init; } = 8888;

    public bool AllowHttpFallback { get; init; } = true;

    [Range(1, 1440)]
    public int LoginLockoutMinutes { get; init; } = 15;

    [Required]
    [RegularExpression("^(auto|static|php|aspnet|classic-asp|mixed|node|python)$")]
    public string DefaultRuntimeProfile { get; init; } = "auto";

    public bool InstallRuntimeIfMissing { get; init; } = true;
}
