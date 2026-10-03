using System.Security.Cryptography;
using System.Text.Json;

namespace AdaPanel.Infrastructure.Support;

/// <summary>Identifies the saved configuration a user actually reviewed.</summary>
internal static class SettingsRevision
{
    private static readonly byte[] LegacyKey = RandomNumberGenerator.GetBytes(32);

    internal static string New() => Guid.NewGuid().ToString("N");

    // Legacy files have no revision. Reading them must not rewrite configuration, so use
    // a process-stable keyed digest of the caller's explicit stored configuration snapshot.
    // A private key also protects old files that still contain a plaintext SMTP password:
    // the returned token must not become an offline password guessing oracle. Restarting
    // invalidates legacy tokens and requires a refresh; saved GUID revisions persist.
    // Callers exclude delivery status and never decrypt a credential for this snapshot.
    internal static string GetOrCompute(string? storedRevision, object legacySnapshot) =>
        !string.IsNullOrWhiteSpace(storedRevision)
            ? storedRevision
            : "legacy:" + Convert.ToHexString(HMACSHA256.HashData(LegacyKey, JsonSerializer.SerializeToUtf8Bytes(legacySnapshot)));
}
