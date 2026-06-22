namespace AdaPanel.Core.Dtos.Auth;

public sealed class TwoFactorSetupDto
{
    public bool IsTwoFactorEnabled { get; init; }

    public bool HasPendingSetup { get; init; }

    public string ManualEntryKey { get; init; } = string.Empty;

    public string ProvisioningUri { get; init; } = string.Empty;
}

