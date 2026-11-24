using System.Text.Json;
using System.Text.Json.Nodes;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Mail;

/// <summary>Backward-compatible mail certificate inventory. The legacy top-level record is
/// the most recently saved host; primaryHostName preserves the original server association.</summary>
internal static class MailCertificateRegistry
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    internal sealed record Snapshot(JsonObject Document, IReadOnlyList<JsonObject> Records, string PrimaryHostName);

    internal static string Field(JsonObject record, string name)
    {
        var values = record.Where(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (values.Length == 0 || values[0].Value is null) return "";
        if (values.Length != 1 || values[0].Value is not JsonValue value || !value.TryGetValue<string>(out var text))
            throw new InvalidDataException("Mail certificate inventory has invalid metadata.");
        return text ?? "";
    }

    internal static async Task<Snapshot> ReadAsync(JsonFileStore store, CancellationToken token)
    {
        var root = await store.ReadAsync<JsonObject?>(MailCertificateStoreReference.StatePath, null, token) ?? new JsonObject();
        return Parse(root);
    }

    internal static Snapshot Parse(JsonObject root)
    {
        var records = new List<JsonObject>();
        var arrays = root.Where(p => p.Key.Equals("certificates", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (arrays.Length > 1 || (arrays.Length == 1 && arrays[0].Value is not JsonArray))
            throw new InvalidDataException("Mail certificate inventory is invalid.");
        if (arrays.Length == 1)
        {
            var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in (JsonArray)arrays[0].Value!)
            {
                if (entry is not JsonObject record || string.IsNullOrWhiteSpace(Field(record, "hostName"))
                    || !hosts.Add(Field(record, "hostName")))
                    throw new InvalidDataException("Mail certificate inventory has invalid or duplicate hostnames.");
                records.Add((JsonObject)record.DeepClone());
            }
        }
        var legacyHost = Field(root, "hostName");
        if (!string.IsNullOrWhiteSpace(legacyHost))
        {
            var legacy = (JsonObject)root.DeepClone();
            foreach (var key in legacy.Select(p => p.Key).Where(k => k.Equals("certificates", StringComparison.OrdinalIgnoreCase)
                || k.Equals("primaryHostName", StringComparison.OrdinalIgnoreCase)).ToArray()) legacy.Remove(key);
            // Top-level fields remain authoritative for legacy writers and installations.
            var index = records.FindIndex(r => Field(r, "hostName").Equals(legacyHost, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) records[index] = legacy; else records.Insert(0, legacy);
        }
        var primary = Field(root, "primaryHostName");
        if (primary.Length == 0) primary = legacyHost;
        return new Snapshot(root, records, primary);
    }

    // Caller owns MailCertificateLifecycle for the complete read/provider/write operation.
    // Re-read immediately before writing: never publish an obsolete snapshot over another host.
    internal static async Task UpsertAsync<T>(JsonFileStore store, T state, CancellationToken token, string? initialPrimaryHostName = null)
    {
        var current = await ReadAsync(store, token);
        var replacement = JsonSerializer.SerializeToNode(state, Json)!.AsObject();
        var host = Field(replacement, "hostName");
        if (string.IsNullOrWhiteSpace(host)) throw new InvalidDataException("Mail certificate hostname is missing.");
        var records = current.Records.Select(r => (JsonObject)r.DeepClone()).ToList();
        var existing = records.FindIndex(r => Field(r, "hostName").Equals(host, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
        {
            // Retain unknown legacy metadata as well as all unrelated host records.
            var merged = records[existing];
            foreach (var field in replacement) SetCanonicalField(merged, field.Key, field.Value?.DeepClone());
            replacement = (JsonObject)merged.DeepClone();
            records[existing] = (JsonObject)replacement.DeepClone();
        }
        else records.Add((JsonObject)replacement.DeepClone());
        SetCanonicalField(replacement, "primaryHostName", JsonValue.Create(current.PrimaryHostName.Length > 0
            ? current.PrimaryHostName : !string.IsNullOrWhiteSpace(initialPrimaryHostName) ? initialPrimaryHostName : host));
        SetCanonicalField(replacement, "certificates", new JsonArray(records.Select(r => (JsonNode?)r).ToArray()));
        await store.WriteAsync(MailCertificateStoreReference.StatePath, replacement, token);
    }

    private static void SetCanonicalField(JsonObject target, string name, JsonNode? value)
    {
        // Legacy serializers used PascalCase. Replace every casing alias before writing
        // the canonical field, rather than creating e.g. HostName plus hostName.
        foreach (var key in target.Select(field => field.Key)
            .Where(key => key.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray()) target.Remove(key);
        target[name] = value;
    }

    internal static T Deserialize<T>(JsonObject value) => value.Deserialize<T>(Json)!;
}
