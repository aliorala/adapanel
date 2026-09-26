using System.Management;
using System.Globalization;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Dns;

/// <summary>Invoke the same read-only CIM methods used by DnsServer cmdlets, without starting a shell.</summary>
internal static class WindowsDnsInspection
{
    private sealed record Zone(string Name, string Type, bool AutoCreated);

    private static ManagementScope Connect()
    {
        var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\DNS", new ConnectionOptions { Timeout = TimeSpan.FromSeconds(5) });
        scope.Connect();
        return scope;
    }

    private static List<T> Read<T>(ManagementScope scope, string className, string? zoneName,
        Func<ManagementBaseObject, T> map, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var source = new ManagementClass(scope, new ManagementPath(className), null);
        using var input = source.GetMethodParameters("Get");
        if (zoneName is not null) input["ZoneName"] = zoneName; // Bound parameter, never WQL/script text.
        using var response = source.InvokeMethod("Get", input, new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(5) })
            ?? throw new IOException("DNS provider returned no response.");
        CheckResultCode(response["ReturnValue"]);
        var result = new List<T>();
        if (response["cmdletOutput"] is not ManagementBaseObject[] rows)
        {
            if (response["cmdletOutput"] is not null) throw new IOException("DNS provider returned an invalid inventory.");
            return result;
        }
        foreach (var row in rows)
        {
            using (row)
            {
                token.ThrowIfCancellationRequested();
                result.Add(map(row));
            }
        }
        return result;
    }

    internal static void CheckResultCode(object? code)
    {
        // PS_DnsServer* Get methods leave ReturnValue null on success. Provider failures
        // throw ManagementException; other versions may explicitly return a status code.
        if (code is not null && Convert.ToInt64(code, CultureInfo.InvariantCulture) != 0)
            throw new IOException($"DNS provider read failed: {code}.");
    }

    private static string RequiredText(ManagementBaseObject row, string field)
    {
        var value = Convert.ToString(row[field], CultureInfo.InvariantCulture);
        return !string.IsNullOrWhiteSpace(value) ? value : throw new IOException($"DNS {field} missing.");
    }

    private static List<Zone> ReadZones(ManagementScope scope, CancellationToken token) => Read(scope, "PS_DnsServerZone", null,
        row => new Zone(RequiredText(row, "ZoneName"), RequiredText(row, "ZoneType"),
            Convert.ToBoolean(row["IsAutoCreated"] ?? throw new IOException("DNS zone origin missing."), CultureInfo.InvariantCulture)), token);

    internal static Task<int> ReadPrimaryZoneCountAsync(CancellationToken token) => Task.Run(() =>
        ReadZones(Connect(), token).Count(zone => !zone.AutoCreated && string.Equals(zone.Type, "Primary", StringComparison.OrdinalIgnoreCase)), token);

    internal static Task<IReadOnlyDictionary<string, DnsZoneInspectionProbe.Result>> ReadAsync(string[] names, CancellationToken token) =>
        Task.Run<IReadOnlyDictionary<string, DnsZoneInspectionProbe.Result>>(() =>
        {
            var result = new Dictionary<string, DnsZoneInspectionProbe.Result>(StringComparer.OrdinalIgnoreCase);
            if (WindowsNetworkHealth.ReadService("DNS").Status == "NotInstalled")
            {
                foreach (var name in names) result[name] = new() { Name = name };
                return result;
            }
            var scope = Connect();
            var zones = ReadZones(scope, token).ToDictionary(zone => zone.Name.TrimEnd('.'), StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
            {
                token.ThrowIfCancellationRequested();
                if (!zones.TryGetValue(name.TrimEnd('.'), out var zone))
                {
                    result[name] = new() { Name = name, Available = true, Keys = [] };
                    continue;
                }
                var keys = Read(scope, "PS_DnsServerResourceRecord", zone.Name,
                    row => RequiredText(row, "RecordType") + "|" + RequiredText(row, "HostName"), token);
                result[name] = new() { Name = name, Available = true, ZoneType = zone.Type, Keys = keys.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
            }
            return result;
        }, token);
}
