using System.Net;
using System.Net.Sockets;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Backup;

/// <summary>Owner-managed, exact FTP endpoint exceptions. Never contains credentials or paths.</summary>
public sealed class BackupNetworkTrustService(JsonFileStore store, IServerOwnerService owners)
{
    private const string FileName = "config/backup-network-trust.json";

    public static string NormalizeTarget(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "ftp"
            || string.IsNullOrWhiteSpace(uri.Host) || uri.Host.Contains('*') || uri.Port <= 0 || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new AppException("backup.network_trust_invalid", 400, 400);
        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (IPAddress.TryParse(uri.DnsSafeHost, out var literal))
        {
            if (literal.IsIPv4MappedToIPv6) literal = literal.MapToIPv4();
            host = literal.ToString();
        }
        if (host.Contains(':')) host = $"[{host.Trim('[', ']')}]";
        return $"ftp://{host}:{uri.Port}/";
    }

    public async Task RequireOwnerAsync(Guid actor, bool impersonating, CancellationToken token)
    {
        if (impersonating || !await owners.IsServerOwnerAsync(actor, token))
            throw new AppException("backup.network_trust_owner_only", 403, 403);
    }

    public async Task<BackupNetworkTrustDto> GetAsync(string target, Guid actor, bool impersonating, CancellationToken token)
    {
        await RequireOwnerAsync(actor, impersonating, token);
        var key = NormalizeTarget(target);
        var allowed = await IsTrustedAsync(new Uri(key), token);
        await RequireOwnerAsync(actor, impersonating, token);
        return new(key, allowed);
    }

    public async Task<BackupNetworkTrustDto> SetAsync(string target, bool allowed, Guid actor, bool impersonating, CancellationToken token)
    {
        await RequireOwnerAsync(actor, impersonating, token);
        var key = NormalizeTarget(target);
        await store.UpdateAsync(FileName, new List<string>(), entries =>
        {
            entries.RemoveAll(entry => string.Equals(entry, key, StringComparison.Ordinal));
            if (allowed) entries.Add(key);
            return entries;
        }, ct => RequireOwnerAsync(actor, impersonating, ct), token);
        return new(key, allowed);
    }

    // Read every time: grant/revocation applies to background jobs without service restart.
    public async Task<bool> IsTrustedAsync(Uri target, CancellationToken token)
    {
        if (target.Scheme != "ftp") return false;
        var sanitized = new UriBuilder(target) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri;
        var key = NormalizeTarget(sanitized.AbsoluteUri);
        var entries = await store.ReadAsync(FileName, new List<string>(), token);
        return entries.Contains(key, StringComparer.Ordinal);
    }

    public static async Task<string?> FindBlockedTrustedHostAsync(Uri target,
        OutboundAddressGuard.OutboundHostResolver resolver, CancellationToken token)
    {
        var host = target.DnsSafeHost.TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase)
            || host.Equals("metadata.goog", StringComparison.OrdinalIgnoreCase)) return host;
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(target.DnsSafeHost, out var literal)
                ? [literal] : await resolver(target.DnsSafeHost, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return target.DnsSafeHost; }
        if (addresses.Length == 0) return target.DnsSafeHost;
        return addresses.FirstOrDefault(ip => OutboundAddressGuard.IsBlockedAddress(ip) && !IsNasAddress(ip))?.ToString();
    }

    private static bool IsNasAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
            return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168);
        // AWS IPv6 instance metadata is inside ULA; never authorize it as a NAS.
        return ip.AddressFamily == AddressFamily.InterNetworkV6 && (bytes[0] & 0xfe) == 0xfc
            && !ip.Equals(IPAddress.Parse("fd00:ec2::254")) && ip.ScopeId == 0;
    }
}
