namespace AdaPanel.Core.Dtos.Security;

public sealed class SecuritySettingsDto
{
    public string SecureEntryPath { get; init; } = string.Empty;

    public string[] AllowedIpAddresses { get; init; } = [];

    public bool HasIpRestrictions { get; init; }
}

