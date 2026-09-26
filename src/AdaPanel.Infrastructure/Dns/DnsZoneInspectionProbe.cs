using System.Text.Json;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Dns;

internal static class DnsZoneInspectionProbe
{
    internal sealed class Result
    {
        public string Name { get; init; } = "";
        public bool Available { get; init; }
        public string ZoneType { get; init; } = "";
        public string[]? Keys { get; init; }
        public string Error { get; init; } = "";
    }

    internal static async Task<IReadOnlyDictionary<string, Result>> ReadAsync(
        IEnumerable<string> zoneNames, CancellationToken token,
        Func<string, CancellationToken, Task<CommandResult>>? run = null, ILogger? logger = null,
        Func<string[], CancellationToken, Task<IReadOnlyDictionary<string, Result>>>? native = null)
    {
        var names = zoneNames.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (names.Length == 0) return new Dictionary<string, Result>();
        if (run is null || native is not null)
        {
            try
            {
                var direct = await (native ?? WindowsDnsInspection.ReadAsync)(names, token);
                if (names.Any(name => !direct.ContainsKey(name))) throw new IOException("Native DNS inspection returned an incomplete result.");
                return direct;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
            {
                logger?.LogWarning(exception, "Native DNS inspection failed; using PowerShell fallback.");
            }
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var result = await (run ?? ProcessRunner.RunPowerShellStrictAsync)(BuildScript(names), deadline.Token);
        if (!result.Success) throw new IOException("DNS zone inspection failed.");
        var rows = JsonSerializer.Deserialize<Result[]>(result.StandardOutput.Trim(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new IOException("DNS zone inspection returned no result.");
        var map = rows.ToDictionary(row => row.Name, StringComparer.OrdinalIgnoreCase);
        if (names.Any(name => !map.ContainsKey(name))) throw new IOException("DNS zone inspection returned an incomplete result.");
        return map;
    }

    internal static string BuildScript(IEnumerable<string> names)
    {
        var quoted = string.Join(",", names.Select(name => "'" + name.Replace("'", "''") + "'"));
        return $$"""
            $available = [bool](Get-Command Get-DnsServerZone -ErrorAction SilentlyContinue)
            $zones = @{}
            if ($available) {
                Get-DnsServerZone -ErrorAction Stop | ForEach-Object { $zones[$_.ZoneName] = $_ }
            }
            $rows = @(foreach ($name in @({{quoted}})) {
                $row = [ordered]@{ name=$name; available=$available; zoneType=''; keys=$null; error='' }
                if ($available) {
                    try {
                        $zone = $zones[$name]
                        if ($null -ne $zone) {
                            $row.zoneType = [string]$zone.ZoneType
                            $row.keys = @(Get-DnsServerResourceRecord -ZoneName $name -ErrorAction Stop | ForEach-Object { $_.RecordType + '|' + $_.HostName })
                        } else { $row.keys = @() }
                    } catch { $row.error = $_.Exception.Message }
                }
                [pscustomobject]$row
            })
            ConvertTo-Json -InputObject $rows -Depth 4 -Compress
            """;
    }
}
