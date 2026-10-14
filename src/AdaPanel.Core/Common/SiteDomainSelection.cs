using AdaPanel.Core.Dtos.Sites;
using System.Net;

namespace AdaPanel.Core.Common;

public static class SiteDomainSelection
{
    public static bool IsRealDomain(string value) => value.Contains('.') &&
        Uri.CheckHostName(value) == UriHostNameType.Dns && !IPAddress.TryParse(value, out _) &&
        !value.EndsWith(".nip.io", StringComparison.OrdinalIgnoreCase) &&
        !value.EndsWith(".sslip.io", StringComparison.OrdinalIgnoreCase);

    public static string WebDomain(SiteDto site)
    {
        var hosts = site.Bindings.Select(binding => (binding.HostName ?? "").Trim().TrimEnd('.').ToLowerInvariant())
            .Where(IsRealDomain).Distinct().ToArray();
        var name = (site.Name ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        if (IsRealDomain(name) && (hosts.Length == 0 || hosts.Contains(name) || hosts.Contains("www." + name))) return name;
        return hosts.OrderBy(host => IsServiceHost(host) ? 1 : 0)
            .ThenBy(host => host.StartsWith("www.") ? 1 : 0).ThenBy(host => host, StringComparer.Ordinal).FirstOrDefault() ?? "";
    }

    private static bool IsServiceHost(string host) => new[] { "mail.", "webmail.", "ns1.", "ns2.", "ftp.", "autodiscover.", "autoconfig." }
        .Any(prefix => host.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
