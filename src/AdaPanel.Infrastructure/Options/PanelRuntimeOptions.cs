namespace AdaPanel.Infrastructure.Options;

public sealed class PanelRuntimeOptions
{
    public const string SectionName = "Panel";

    public string ServiceName { get; set; } = "AdaPanel";

    public string DataRoot { get; set; } = "%ProgramData%\\AdaPanel";

    public string DefaultLanguage { get; set; } = "tr";

    public int PreferredHttpsPort { get; set; } = 8888;

    public string SecureEntryPath { get; set; } = "panel";

    public bool AllowHttpFallback { get; set; }

    public string AuthCookieName { get; set; } = "AdaPanel.Auth";

    public string TotpIssuer { get; set; } = "AdaPanel";

    public int LoginLockoutMinutes { get; set; } = 15;
}
