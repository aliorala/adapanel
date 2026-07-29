using System.Text.Json;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Security;

/// <summary>
/// Erişim kurtarma sırasında (panel açılışında, DI kurulmadan önce) brute-force IP engellerini
/// temizler. Normal akışta engelleri <see cref="BruteForceProtectionService"/> yönetir; bu yardımcı
/// yalnızca "yönetici kendi IP'sini bloklattı, panele hiç ulaşamıyor" durumu için vardır ve durum
/// dosyasını doğrudan okur/yazar.
/// </summary>
public static class BruteForceRecovery
{
    public static async Task ClearBansAsync(string stateFilePath, ILogger logger)
    {
        if (!File.Exists(stateFilePath))
        {
            return;
        }

        var json = await File.ReadAllTextAsync(stateFilePath);
        using var document = JsonDocument.Parse(json);

        var ruleNames = new List<string>();
        if (document.RootElement.TryGetProperty("bans", out var bans) && bans.ValueKind == JsonValueKind.Array)
        {
            foreach (var ban in bans.EnumerateArray())
            {
                if (ban.TryGetProperty("firewallRuleName", out var rule) && rule.GetString() is { Length: > 0 } name)
                {
                    ruleNames.Add(name);
                }
            }
        }

        foreach (var ruleName in ruleNames)
        {
            // Kural adları panel tarafından "AdaPanel BruteForce Block <ip>" olarak üretilir; yine de
            // çift tırnak kaçışı yapılır (durum dosyası elle düzenlenmiş olabilir).
            var script = $"""
                Remove-NetFirewallRule -DisplayName "{ruleName.Replace("\"", "`\"")}" -ErrorAction SilentlyContinue | Out-Null
                """;
            await ProcessRunner.RunPowerShellAsync(script);
        }

        // Engel ve deneme listelerini boşalt; ayarlar (eşik, süre, whitelist) korunur.
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        node["bans"] = new System.Text.Json.Nodes.JsonArray();
        node["attempts"] = new System.Text.Json.Nodes.JsonArray();
        await File.WriteAllTextAsync(stateFilePath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        logger.LogWarning("Erişim kurtarma: {Count} brute-force IP engeli ve tüm deneme sayaçları temizlendi.", ruleNames.Count);
    }
}
