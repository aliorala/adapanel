namespace AdaPanel.Core.Interfaces;

public interface ITotpService
{
    string GenerateSecret();

    string BuildProvisioningUri(string issuer, string accountName, string secret);

    bool VerifyCode(string secret, string code);
}

