namespace AdaPanel.Core.Dtos.Setup;

public sealed class SetupStatusDto
{
    public bool IsInitialized { get; init; }

    public bool RequiresSetupWizard { get; init; }

    public string SecureEntryPath { get; init; } = string.Empty;

    public string DefaultLanguage { get; init; } = "tr";

    public int PreferredHttpsPort { get; init; } = 8888;

    public string ApplicationVersion { get; init; } = string.Empty;
}
