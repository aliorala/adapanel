namespace AdaPanel.Core.Dtos.Settings;

public sealed class PanelPreferencesDto
{
    public string PanelName { get; set; } = "AdaPanel";

    public string DefaultLanguage { get; set; } = "tr";

    public string ThemeMode { get; set; } = "light";

    public string UiDensity { get; set; } = "compact";

    public string DefaultSiteRoot { get; set; } = "C:\\inetpub\\wwwroot";

    public string DefaultBackupRoot { get; set; } = "%ProgramData%\\AdaPanel\\backups";

    public string ServerAddress { get; set; } = string.Empty;

    public int PreferredHttpsPort { get; set; } = 8888;

    public bool AllowHttpFallback { get; set; } = true;

    public int LoginLockoutMinutes { get; set; } = 15;

    public string DefaultRuntimeProfile { get; set; } = "auto";

    public bool InstallRuntimeIfMissing { get; set; } = true;

    public bool RestartRequired { get; set; }
}
