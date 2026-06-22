namespace AdaPanel.Core.Dtos.Setup;

public sealed class SetupResultDto
{
    public bool IsInitialized { get; init; }

    public string SecureEntryPath { get; init; } = string.Empty;

    public string AdminUserName { get; init; } = string.Empty;

    public string PreferredLanguage { get; init; } = "tr";

    public int PreferredHttpsPort { get; init; }

    public bool RecommendedComponentsRequested { get; init; }

    public int RecommendedComponentsInstalled { get; init; }

    /// <summary>true ise önerilen yazılım kurulumu arka planda sürüyor; frontend ilerlemeyi yoklamalı.</summary>
    public bool InstallationInProgress { get; init; }

    public string[] Warnings { get; init; } = [];
}
