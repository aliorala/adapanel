using System.Collections;
using System.Runtime.InteropServices;

namespace AdaPanel.Infrastructure.Firewall;

/// <summary>Read-only Windows Firewall COM API; avoids the NetSecurity module/CIM startup.</summary>
internal static class WindowsFirewallInspection
{
    internal static Task<(bool CoversAllTcp, List<(int Lo, int Hi)> Ranges)> ReadAllowCoverageAsync(CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!)!;
        object? rules = null;
        try
        {
            rules = policy.Rules;
            var ports = new List<(string?, string?)>();
            foreach (dynamic rule in (IEnumerable)rules)
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (!(bool)rule.Enabled || (int)rule.Direction != 1 || (int)rule.Action != 1) continue;
                    var protocol = (int)rule.Protocol;
                    if (protocol == 256) ports.Add(("Any", "Any"));
                    else if (protocol == 6) ports.Add(("TCP", NormalizePorts((string?)rule.LocalPorts)));
                }
                finally { Release(rule); }
            }
            return FirewallService.ParseAllowCoverage(ports);
        }
        finally { Release(rules); Release(policy); }
    }, token);

    internal static string? NormalizePorts(string? ports) => ports?.Trim() == "*" ? "Any" : ports;

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }
}
