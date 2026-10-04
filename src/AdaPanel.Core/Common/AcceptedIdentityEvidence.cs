using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AdaPanel.Core.Entities;

namespace AdaPanel.Core.Common;

/// <summary>
/// Opaque evidence of the identity accepted by an authentication operation. It lives
/// only inside one server process/request; it is neither an API field nor a cookie.
/// </summary>
public static class AcceptedIdentityEvidence
{
    private static readonly byte[] ProcessKey = RandomNumberGenerator.GetBytes(32);

    public static string Create(PanelUser user)
    {
        // Login counters/timestamps and pending enrollment are intentionally excluded:
        // another normal login or an unconfirmed setup does not revoke accepted identity.
        var snapshot = JsonSerializer.SerializeToUtf8Bytes(new
        {
            user.Id, user.UserName, user.Email, user.PasswordHash, user.Role, user.IsActive,
            user.IsTwoFactorEnabled,
            ActiveFactor = user.IsTwoFactorEnabled ? user.TwoFactorSecretProtected : null
        });
        return Convert.ToHexString(HMACSHA256.HashData(ProcessKey, snapshot));
    }

    public static bool Matches(PanelUser user, string? evidence) =>
        !string.IsNullOrWhiteSpace(evidence) && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(Create(user)), Encoding.UTF8.GetBytes(evidence));
}
