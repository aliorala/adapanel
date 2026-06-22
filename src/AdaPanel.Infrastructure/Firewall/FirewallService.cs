using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Firewall;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Firewall;

public sealed class FirewallService : IFirewallService
{
    public async Task<IReadOnlyCollection<FirewallRuleDto>> GetRulesAsync(CancellationToken cancellationToken = default)
    {
        var script = """
            $rules = Get-NetFirewallRule -DisplayName 'AdaPanel *' -ErrorAction SilentlyContinue | ForEach-Object {
              $port = Get-NetFirewallPortFilter -AssociatedNetFirewallRule $_ -ErrorAction SilentlyContinue | Select-Object -First 1;
              [pscustomobject]@{
                DisplayName = $_.DisplayName
                Direction = $_.Direction.ToString()
                Action = $_.Action.ToString()
                Protocol = if ($port) { $port.Protocol } else { '' }
                LocalPort = if ($port) { [string]$port.LocalPort } else { '' }
                Enabled = [bool]$_.Enabled
              }
            }
            $rules | ConvertTo-Json -Depth 4
            """;

        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
        if (!result.Success)
        {
            throw new AppException("firewall.read_failed", 500, 500);
        }

        return DeserializeArray<FirewallRuleDto>(result.StandardOutput);
    }

    public async Task<IReadOnlyCollection<PortStatusDto>> GetPortStatusAsync(int panelPort, CancellationToken cancellationToken = default)
    {
        var catalog = BuildCatalog(panelPort);

        // 1) Dinleme: tek TCP listener tablosu taraması (servisleri rahatsız etmez, her portu ayrı ayrı yoklamaktan iyidir).
        IPEndPoint[] listeners;
        try
        {
            listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        }
        catch
        {
            listeners = [];
        }

        // 2) Firewall kapsamı: etkin inbound Allow (TCP/Any) kurallarının kapsadığı portlar.
        var (coversAllTcp, ranges) = await ReadAllowCoverageAsync(cancellationToken);

        var result = new List<PortStatusDto>(catalog.Count);
        foreach (var (port, rangeEnd, key, category, required) in catalog)
        {
            var isRange = rangeEnd > port;
            var matches = isRange
                ? listeners.Where(endpoint => endpoint.Port >= port && endpoint.Port <= rangeEnd).ToArray()
                : listeners.Where(endpoint => endpoint.Port == port).ToArray();
            var listening = matches.Length > 0;
            var scope = listening
                ? (matches.Any(endpoint => !IPAddress.IsLoopback(endpoint.Address)) ? "all" : "loopback")
                : string.Empty;

            result.Add(new PortStatusDto
            {
                Port = port,
                RangeEnd = isRange ? rangeEnd : 0,
                Protocol = "TCP",
                ServiceKey = key,
                Category = category,
                Required = required,
                Listening = listening,
                ListenScope = scope,
                FirewallAllowed = IsPortCovered(port, coversAllTcp, ranges)
            });
        }

        return result;
    }

    // Panelin bildiği hizmet portları. Panel portu HttpContext'ten gelir (gerçek). Port'a göre tekilleştirilir
    // (panel 443'teyse https satırıyla çakışmasın; panel kazanır).
    private static List<(int Port, int RangeEnd, string Key, string Category, bool Required)> BuildCatalog(int panelPort)
    {
        // RangeEnd = 0 -> tek port. FTP pasif (50000-51000) FtpService'teki sabitlerle eşleşir.
        var entries = new List<(int Port, int RangeEnd, string Key, string Category, bool Required)>
        {
            (panelPort > 0 ? panelPort : 8890, 0, "panel", "panel", true),
            (80, 0, "http", "web", true),
            (443, 0, "https", "web", true),
            (21, 0, "ftp", "ftp", false),
            (50000, 51000, "ftpPassive", "ftp", false),
            (25, 0, "smtp", "mail", true),
            (587, 0, "submission", "mail", true),
            (465, 0, "smtps", "mail", false),
            (143, 0, "imap", "mail", true),
            (993, 0, "imaps", "mail", false),
            (995, 0, "pop3s", "mail", false),
            (8896, 0, "phpmyadmin", "tools", false),
            (8897, 0, "adminer", "tools", false),
            (8899, 0, "webmail", "mail", false)
        };

        var seen = new HashSet<int>();
        var unique = new List<(int Port, int RangeEnd, string Key, string Category, bool Required)>();
        foreach (var entry in entries)
        {
            if (seen.Add(entry.Port))
            {
                unique.Add(entry);
            }
        }

        return unique;
    }

    // Etkin inbound Allow (TCP veya Any protokol) kurallarını okuyup port kapsamını çıkarır.
    // "Any" port -> tüm TCP kapsanır; "5000-5100" -> aralık; "587" -> tek port. AdaPanel kuralları DAHİL
    // tüm kurallara bakar (IIS'in 80/443 kuralları da sayılır). Hata olursa kapsam boş döner (engelli görünür).
    private static async Task<(bool CoversAllTcp, List<(int Lo, int Hi)> Ranges)> ReadAllowCoverageAsync(CancellationToken cancellationToken)
    {
        var script = """
            Get-NetFirewallRule -Direction Inbound -Action Allow -Enabled True -ErrorAction SilentlyContinue | ForEach-Object {
              $pf = Get-NetFirewallPortFilter -AssociatedNetFirewallRule $_ -ErrorAction SilentlyContinue | Select-Object -First 1;
              if ($pf) {
                [pscustomobject]@{
                  Protocol = [string]$pf.Protocol
                  LocalPort = ($pf.LocalPort -join ',')
                }
              }
            } | ConvertTo-Json -Depth 3
            """;

        try
        {
            var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
            if (!result.Success)
            {
                return (false, new List<(int Lo, int Hi)>());
            }

            var rows = DeserializeArray<FirewallPortFilterRow>(result.StandardOutput);
            return ParseAllowCoverage(rows.Select(row => (row.Protocol, row.LocalPort)));
        }
        catch
        {
            return (false, new List<(int Lo, int Hi)>());
        }
    }

    // Saf parse (test edilebilir): PS'ten gelen (protokol, localPort) satırlarından TCP port kapsamını çıkarır.
    internal static (bool CoversAllTcp, List<(int Lo, int Hi)> Ranges) ParseAllowCoverage(IEnumerable<(string? Protocol, string? LocalPort)> rows)
    {
        var ranges = new List<(int Lo, int Hi)>();
        var coversAll = false;
        foreach (var row in rows)
        {
            var protocol = (row.Protocol ?? string.Empty).Trim();
            var isTcp = protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase) ||
                        protocol.Equals("Any", StringComparison.OrdinalIgnoreCase) ||
                        protocol == "6";
            if (!isTcp)
            {
                continue;
            }

            foreach (var token in (row.LocalPort ?? string.Empty).Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (token.Equals("Any", StringComparison.OrdinalIgnoreCase))
                {
                    coversAll = true;
                }
                else if (token.Contains('-'))
                {
                    var parts = token.Split('-', 2);
                    if (int.TryParse(parts[0], out var lo) && int.TryParse(parts[1], out var hi) && lo <= hi)
                    {
                        ranges.Add((lo, hi));
                    }
                }
                else if (int.TryParse(token, out var single))
                {
                    ranges.Add((single, single));
                }
                // RPC / RPCEPMap / IPHTTPS gibi sayısal olmayan jetonlar yok sayılır.
            }
        }

        return (coversAll, ranges);
    }

    // Bir portun firewall kapsamında olup olmadığı (coversAll ise ya da bir aralığa düşüyorsa).
    internal static bool IsPortCovered(int port, bool coversAllTcp, IReadOnlyList<(int Lo, int Hi)> ranges)
    {
        if (coversAllTcp)
        {
            return true;
        }

        foreach (var range in ranges)
        {
            if (port >= range.Lo && port <= range.Hi)
            {
                return true;
            }
        }

        return false;
    }

    private sealed class FirewallPortFilterRow
    {
        public string? Protocol { get; set; }

        public string? LocalPort { get; set; }
    }

    public Task<FirewallRuleDto> AllowPortAsync(UpdateFirewallRuleRequest request, CancellationToken cancellationToken = default)
    {
        return UpsertRuleAsync(request, "Allow", cancellationToken);
    }

    public Task<FirewallRuleDto> BlockPortAsync(UpdateFirewallRuleRequest request, CancellationToken cancellationToken = default)
    {
        return UpsertRuleAsync(request, "Block", cancellationToken);
    }

    private static async Task<FirewallRuleDto> UpsertRuleAsync(
        UpdateFirewallRuleRequest request,
        string action,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);

        var ruleName = string.IsNullOrWhiteSpace(request.RuleName)
            ? $"AdaPanel {action} {request.Protocol.ToUpperInvariant()} {request.Port}"
            : request.RuleName.Trim();

        var script = $"""
            Remove-NetFirewallRule -DisplayName "{EscapePowerShellString(ruleName)}" -ErrorAction SilentlyContinue | Out-Null
            New-NetFirewallRule -DisplayName "{EscapePowerShellString(ruleName)}" -Direction Inbound -Action {action} -Protocol {request.Protocol.ToUpperInvariant()} -LocalPort {request.Port} -Profile Any | Out-Null
            """;

        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
        if (!result.Success)
        {
            throw new AppException("firewall.write_failed", 500, 500);
        }

        return new FirewallRuleDto
        {
            DisplayName = ruleName,
            Direction = "Inbound",
            Action = action,
            Protocol = request.Protocol.ToUpperInvariant(),
            LocalPort = request.Port.ToString(),
            Enabled = true
        };
    }

    private static void ValidateRequest(UpdateFirewallRuleRequest request)
    {
        if (request.Port < 1 || request.Port > 65535)
        {
            throw new AppException("firewall.invalid_port", 400, 400);
        }

        if (!string.Equals(request.Protocol, "TCP", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(request.Protocol, "UDP", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException("firewall.invalid_protocol", 400, 400);
        }
    }

    private static T[] DeserializeArray<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind switch
        {
            JsonValueKind.Array => document.RootElement.Deserialize<T[]>() ?? [],
            JsonValueKind.Object => [document.RootElement.Deserialize<T>()!],
            _ => []
        };
    }

    private static string EscapePowerShellString(string value) => value.Replace("\"", "`\"");
}
