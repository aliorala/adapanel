using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.DataProtection;

namespace AdaPanel.Infrastructure.Services;

public sealed class SecretProtector(IDataProtectionProvider dataProtectionProvider) : ISecretProtector
{
    private readonly IDataProtector protector = dataProtectionProvider.CreateProtector("AdaPanel.Infrastructure.SecretProtector.v1");

    public string Protect(string plaintext)
    {
        return protector.Protect(plaintext);
    }

    public string Unprotect(string protectedValue)
    {
        return protector.Unprotect(protectedValue);
    }
}
