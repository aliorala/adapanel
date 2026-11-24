using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;

namespace AdaPanel.Infrastructure.Ssl;

// win-acme v2.2.9.1701: DomainObjects/Renewal.cs and the corresponding plugin options.
internal static class SslRenewalVerification
{
    internal const string IisSource = "54deb3ee-b5df-4381-8485-fe386054055b";
    internal const string ManualSource = "e239db3b-b42f-48aa-b64f-46d4f3e9941b";
    internal const string SelfHosting = "c7d5e050-9363-4ba1-b3a8-931b31c618b7";
    internal const string DnsScript = "8f1da72e-f727-49f0-8546-ef69e5ecec32";
    internal const string IisInstallation = "ea6a5be3-f8de-4d27-a6bd-750b619b2ee2";

    internal static AppException Invalid() => new("ssl.renewal_verification_failed", 502, 502);
    internal static string? String(JsonElement value, string name) => value.TryGetProperty(name, out var item)
        && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    private static bool Plugin(JsonElement value, string expected) => string.Equals(String(value, "Plugin"), expected, StringComparison.OrdinalIgnoreCase);
    private static string[] Strings(JsonElement value, string name) => value.TryGetProperty(name, out var items) && items.ValueKind == JsonValueKind.Array
        ? items.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray() : [];
    private static bool Exact(string[] actual, string[] expected) => actual.Length == expected.Length && actual.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(expected);
    private static bool SiteIds(JsonElement value, long id) => value.TryGetProperty("IncludeSiteIds", out var ids)
        && ids.ValueKind == JsonValueKind.Array && ids.GetArrayLength() == 1 && ids[0].ValueKind == JsonValueKind.Number && ids[0].TryGetInt64(out var actual) && actual == id;
    private static bool Empty(JsonElement value, string name) => !value.TryGetProperty(name, out var item) || item.ValueKind == JsonValueKind.Null
        || item.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(item.GetString())
        || item.ValueKind == JsonValueKind.Array && item.GetArrayLength() == 0;

    internal static JsonElement? Select(IReadOnlyList<JsonElement> records, SiteDto site, string[] hosts, string? dnsScript = null)
    {
        var matches = new List<JsonElement>();
        foreach (var record in records)
        {
            if (!record.TryGetProperty("TargetPluginOptions", out var target)) throw Invalid();
            var names = Strings(target, "IncludeHosts").Concat(Strings(target, "AlternativeNames")).Append(String(target, "CommonName") ?? "").Where(x => !string.IsNullOrEmpty(x)).ToArray();
            var ownsSite = target.TryGetProperty("IncludeSiteIds", out var sites) && sites.ValueKind == JsonValueKind.Array
                && sites.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt64(out var id) && id == site.Id);
            var webInstallation = record.TryGetProperty("InstallationPluginOptions", out var configuredInstallations)
                && configuredInstallations.ValueKind == JsonValueKind.Array && configuredInstallations.EnumerateArray().Any(x =>
                    Plugin(x, IisInstallation) && x.TryGetProperty("SiteId", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var value) && value == site.Id);
            if (Plugin(target, IisSource) && Exact(Strings(target, "IncludeTypes"), ["ftp"])) continue;
            if (Plugin(target, IisSource) ? !ownsSite : !webInstallation) continue;
            if (names.Length > 0 && !names.Any(name => hosts.Contains(name, StringComparer.OrdinalIgnoreCase))) continue;
            var sourceOk = dnsScript is null
                ? Plugin(target, IisSource) && SiteIds(target, site.Id) && Exact(Strings(target, "IncludeHosts"), hosts)
                    && Empty(target, "IncludePattern") && Empty(target, "IncludeRegex") && Empty(target, "ExcludeHosts")
                    && (Empty(target, "IncludeTypes") || Exact(Strings(target, "IncludeTypes"), ["http"]))
                : Plugin(target, ManualSource) && Exact(Strings(target, "AlternativeNames"), hosts);
            if (!sourceOk || !record.TryGetProperty("ValidationPluginOptions", out var validation)
                || !record.TryGetProperty("InstallationPluginOptions", out var installs) || installs.ValueKind != JsonValueKind.Array
                || installs.GetArrayLength() != 1 || !Plugin(installs[0], IisInstallation)
                || (installs[0].TryGetProperty("SiteId", out var installationSite) && installationSite.ValueKind != JsonValueKind.Null
                    ? installationSite.ValueKind != JsonValueKind.Number || !installationSite.TryGetInt64(out var actualSite) || actualSite != site.Id : dnsScript is not null)
                || (dnsScript is null ? !Plugin(validation, SelfHosting) || !Empty(validation, "Port") || !Empty(validation, "Https")
                    : !Plugin(validation, DnsScript) || !string.Equals(String(validation, "Script"), dnsScript, StringComparison.OrdinalIgnoreCase)
                      || !Empty(validation, "CreateScript") || !Empty(validation, "DeleteScript")
                      || !Empty(validation, "CreateScriptArguments") || !Empty(validation, "DeleteScriptArguments")))
                throw new AppException("ssl.renewal_managed_externally", 409, 409,
                    $"{site.Name}: {string.Join(", ", hosts)} already belongs to a different renewal plan. Renew it with its existing win-acme configuration; review source, validation and installation plugins before requesting a new panel-managed certificate.");
            var id = String(record, "Id");
            if (string.IsNullOrWhiteSpace(id) || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) throw Invalid();
            matches.Add(record);
        }
        if (matches.Count > 1) throw Invalid();
        return matches.Count == 0 ? null : matches[0];
    }

    internal static void VerifyIssued(JsonElement? before, JsonElement after, X509Certificate2 certificate, string[] hosts)
    {
        VerifyCertificate(certificate, hosts);
        if (!string.Equals(LastThumbprint(after), certificate.Thumbprint, StringComparison.OrdinalIgnoreCase)) throw Invalid();
    }

    internal static string LastThumbprint(JsonElement record)
    {
        if (!record.TryGetProperty("History", out var history) || history.ValueKind != JsonValueKind.Array || history.GetArrayLength() == 0) throw Invalid();
        var last = history[history.GetArrayLength() - 1];
        if (!last.TryGetProperty("Success", out var success) || success.ValueKind != JsonValueKind.True || !Empty(last, "ErrorMessages")) throw Invalid();
        if (!last.TryGetProperty("OrderResults", out var orders) || orders.ValueKind != JsonValueKind.Array
            || orders.GetArrayLength() != 1 || String(orders[0], "Thumbprint") is not string thumb || string.IsNullOrWhiteSpace(thumb)
            || !orders[0].TryGetProperty("Success", out var orderSuccess) || orderSuccess.ValueKind != JsonValueKind.True
            || !Empty(orders[0], "ErrorMessages")) throw Invalid();
        return thumb;
    }

    internal static void VerifyCertificate(X509Certificate2 certificate, string[] hosts)
    {
        if (!certificate.HasPrivateKey || certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow
            || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow || hosts.Any(host => !SslCertificateSelection.MatchesHost(certificate, host))) throw Invalid();
        var usages = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().ToArray();
        if (usages.Length > 0 && !usages.Any(x => x.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>().Any(oid => oid.Value is "1.3.6.1.5.5.7.3.1" or "2.5.29.37.0"))) throw Invalid();
    }

    internal static async Task<IReadOnlyList<JsonElement>> ReadAsync(string executable, bool staging, CancellationToken token)
    {
        var clientName = "win-acme";
        string? root = null;
        var settingsPath = Path.Combine(Path.GetDirectoryName(executable)!, "settings.json");
        if (!File.Exists(settingsPath)) settingsPath = Path.Combine(Path.GetDirectoryName(executable)!, "settings_default.json");
        if (File.Exists(settingsPath))
        {
            using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath, token));
            if (settings.RootElement.TryGetProperty("Client", out var client))
            {
                clientName = String(client, "ClientName") ?? clientName;
                root = String(client, "ConfigurationPath");
            }
        }
        root = string.IsNullOrEmpty(root) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), clientName)
            : Directory.Exists(Path.Combine(root, clientName)) ? Path.Combine(root, clientName) : root;
        root = Path.Combine(root, staging ? "acme-staging-v02.api.letsencrypt.org" : "acme-v02.api.letsencrypt.org");
        if (!Directory.Exists(root)) return [];
        var result = new List<JsonElement>();
        foreach (var path in Directory.EnumerateFiles(root, "*.renewal.json"))
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, token));
            result.Add(document.RootElement.Clone());
        }
        return result;
    }
}
