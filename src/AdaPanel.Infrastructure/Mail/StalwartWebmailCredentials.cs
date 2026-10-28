using System.Net.Http.Json;
using System.Text.Json;
using AdaPanel.Core.Common;

namespace AdaPanel.Infrastructure.Mail;

/// <summary>Stalwart 0.16.26 account-owned AppPassword API, never the primary credential.</summary>
public sealed class StalwartWebmailCredentials(StalwartRuntime runtime, IHttpClientFactory clients)
{
    public const string ClientName = "webmail-sso-stalwart";
    internal static readonly string[] Permissions =
    [
        "authenticate", "emailSend", "imapAuthenticate", "imapCapability", "imapId", "imapAppend",
        "imapCopy", "imapMove", "imapCreate", "imapDelete", "imapEnable", "imapExpunge", "imapFetch",
        "imapIdle", "imapList", "imapLsub", "imapNamespace", "imapRename", "imapSearch", "imapSort",
        "imapSelect", "imapExamine", "imapStatus", "imapStore", "imapSubscribe", "imapThread", "imapMyRights",
        "sieveAuthenticate", "sieveListScripts", "sieveSetActive", "sieveGetScript", "sievePutScript",
        "sieveDeleteScript", "sieveRenameScript", "sieveCheckScript", "sieveHaveSpace"
    ];

    public async Task<string> ResolveAccountAsync(string address, CancellationToken token)
    {
        // Read canonical primary addresses; aliases and partial name matches never qualify.
        string? result = null;
        for (var position = 0; position < 10000; position += 100)
        {
            var query = await CallAsync("x:Account/query", new { position, limit = 100 }, token);
            var ids = query.GetProperty("ids").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
            if (ids.Length == 0) return result ?? throw Invalid();
            var get = await CallAsync("x:Account/get", new { ids, properties = new[] { "id", "@type", "emailAddress" } }, token);
            foreach (var account in get.GetProperty("list").EnumerateArray())
            {
                if (account.GetProperty("@type").GetString() == "User" &&
                    string.Equals(account.GetProperty("emailAddress").GetString(), address, StringComparison.OrdinalIgnoreCase))
                {
                    if (result is not null) throw Invalid();
                    result = account.GetProperty("id").GetString();
                    if (string.IsNullOrEmpty(result)) throw Invalid();
                }
            }
            if (ids.Length < 100) return result ?? throw Invalid();
        }
        throw Unavailable(); // Incomplete measurement must not select an arbitrary account.
    }

    public sealed record Credential(string Id, string Secret);

    public async Task<Credential> CreateAsync(string accountId, string correlation, DateTimeOffset expiry, CancellationToken token)
    {
        var credential = new Dictionary<string, object>
        {
            ["description"] = correlation,
            ["expiresAt"] = expiry.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            ["allowedIps"] = new Dictionary<string, bool> { ["127.0.0.1/32"] = true, ["::1/128"] = true },
            ["permissions"] = new Dictionary<string, object>
            {
                ["@type"] = "Replace", ["permissions"] = Permissions.ToDictionary(x => x, _ => true)
            }
        };
        var response = await CallAsync("x:AppPassword/set", new
        {
            accountId, create = new Dictionary<string, object> { ["sso"] = credential }
        }, token);
        if (!response.TryGetProperty("created", out var created) || !created.TryGetProperty("sso", out var item)) throw Unavailable();
        var id = item.GetProperty("id").GetString();
        var secret = item.GetProperty("secret").GetString();
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret)) throw Unavailable();
        return new(id, secret);
    }

    public async Task<bool> RevokeOwnedAsync(string accountId, string? credentialId, string correlation, CancellationToken token)
    {
        // Re-read the credential's correlation before destruction. An unknown outcome of create
        // is recovered by listing this account's credentials, never by deleting other credentials.
        var get = await CallAsync("x:AppPassword/get", new { accountId, properties = new[] { "id", "description" } }, token);
        var ids = get.GetProperty("list").EnumerateArray()
            .Where(x => x.GetProperty("description").GetString() == correlation)
            .Select(x => x.GetProperty("id").GetString()!)
            .Where(x => credentialId is null || x == credentialId).ToArray();
        if (ids.Length == 0) return false;
        var set = await CallAsync("x:AppPassword/set", new { accountId, destroy = ids }, token);
        var destroyed = set.TryGetProperty("destroyed", out var values)
            ? values.EnumerateArray().Select(x => x.GetString()).ToHashSet() : [];
        if (ids.Any(x => !destroyed.Contains(x))) throw Unavailable();
        return true;
    }

    private async Task<JsonElement> CallAsync(string method, object arguments, CancellationToken token)
    {
        var password = await runtime.GetAdminPasswordAsync(token);
        if (string.IsNullOrWhiteSpace(password)) throw Unavailable();
        using var client = clients.CreateClient(ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost:8080/jmap/");
        request.Headers.Authorization = StalwartRuntime.BasicHeader(password);
        request.Content = JsonContent.Create(new
        {
            @using = new[] { "urn:ietf:params:jmap:core", "urn:stalwart:jmap" },
            methodCalls = new object[][] { [method, arguments, "sso"] }
        });
        using var response = await client.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) throw Unavailable();
        using var stream = await response.Content.ReadAsStreamAsync(token);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        if (!document.RootElement.TryGetProperty("methodResponses", out var methods) || methods.GetArrayLength() != 1) throw Unavailable();
        var call = methods[0];
        if (call[0].GetString() != method || call[2].GetString() != "sso") throw Unavailable();
        return call[1].Clone();
    }
    private static AppException Invalid() => new("mail.webmail_sso_invalid", 403, 403);
    private static AppException Unavailable() => new("mail.webmail_sso_unavailable", 503, 503);
}
