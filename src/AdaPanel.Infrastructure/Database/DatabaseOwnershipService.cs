using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Database;

// DB sahipliği (config/db-ownership.json). PostgreSQL adları quoted identifier olarak saklanır.
// Atomik oku-değiştir-yaz (UpdateAsync) ile eşzamanlı kayıplara karşı korunur.
internal sealed class DatabaseOwnershipService(JsonFileStore fileStore) : IDatabaseOwnershipService
{
    private const string StatePath = "config/db-ownership.json";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonElement EmptyState = JsonSerializer.SerializeToElement(new Dictionary<string, OwnershipEntry>(), SerializerOptions);

    private static string Key(string engine, string databaseName) =>
        DatabaseResourceIdentity.Key(engine, databaseName);

    public async Task RecordAsync(string engine, string databaseName, Guid ownerUserId, string? siteName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            return;
        }
        if (ownerUserId == Guid.Empty)
        {
            throw InvalidState();
        }

        await UpdateStateAsync(state =>
        {
            state[Key(engine, databaseName)] = new OwnershipEntry
            {
                OwnerId = ownerUserId.ToString("N"),
                SiteName = (siteName ?? string.Empty).Trim()
            };
            return state;
        }, cancellationToken);
    }

    public async Task RemoveAsync(string engine, string databaseName, CancellationToken cancellationToken = default)
    {
        await UpdateStateAsync(state =>
        {
            state.Remove(Key(engine, databaseName));
            return state;
        }, cancellationToken);
    }

    public async Task RemoveBySiteAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var name = (siteName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            return;
        }

        await UpdateStateAsync(state =>
        {
            foreach (var key in state
                         .Where(pair => string.Equals(pair.Value.SiteName, name, StringComparison.OrdinalIgnoreCase))
                         .Select(pair => pair.Key)
                         .ToList())
            {
                state.Remove(key);
            }

            return state;
        }, cancellationToken);
    }

    public async Task<int> CountForOwnerAsync(Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(cancellationToken);
        var owner = ownerUserId.ToString("N");
        return state.Values.Count(entry => string.Equals(entry.OwnerId, owner, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<Guid?> GetOwnerAsync(string engine, string databaseName, CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(cancellationToken);
        return state.TryGetValue(Key(engine, databaseName), out var entry) && Guid.TryParse(entry.OwnerId, out var owner)
            ? owner
            : null;
    }

    public async Task<IReadOnlyCollection<(string Engine, string DatabaseName)>> GetOwnedAsync(Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(cancellationToken);
        var owner = ownerUserId.ToString("N");
        var result = new List<(string Engine, string DatabaseName)>();
        foreach (var pair in state)
        {
            if (!string.Equals(pair.Value.OwnerId, owner, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = pair.Key.Split('|', 2);
            if (parts.Length == 2)
            {
                result.Add((parts[0], parts[1]));
            }
        }

        return result;
    }

    public async Task<IReadOnlyCollection<(string Engine, string DatabaseName, Guid OwnerUserId, string SiteName)>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(cancellationToken);
        var result = new List<(string Engine, string DatabaseName, Guid OwnerUserId, string SiteName)>(state.Count);
        foreach (var pair in state)
        {
            var parts = pair.Key.Split('|', 2);
            // Wrap validates the entire snapshot before any ownership can be returned.
            result.Add((parts[0], parts[1], Guid.ParseExact(pair.Value.OwnerId, "N"), pair.Value.SiteName));
        }

        return result;
    }

    private async Task<Dictionary<string, OwnershipEntry>> LoadAsync(CancellationToken cancellationToken) =>
        ReadState(await fileStore.ReadAsync(StatePath, EmptyState, cancellationToken));

    private Task UpdateStateAsync(
        Func<Dictionary<string, OwnershipEntry>, Dictionary<string, OwnershipEntry>> mutate,
        CancellationToken cancellationToken) =>
        fileStore.UpdateAsync(StatePath, EmptyState,
            current => JsonSerializer.SerializeToElement(mutate(ReadState(current)), SerializerOptions), cancellationToken);

    private static Dictionary<string, OwnershipEntry> ReadState(JsonElement state)
    {
        // JsonElement preserves an explicit JSON null. A reference-type fallback would
        // turn that invalid document into an empty authority list and permit site fallback.
        if (state.ValueKind != JsonValueKind.Object) throw InvalidState();
        try
        {
            return Wrap(state.Deserialize<Dictionary<string, OwnershipEntry>>(SerializerOptions)!);
        }
        catch (JsonException)
        {
            throw InvalidState();
        }
    }

    private static AppException InvalidState() => new("database.ownership_invalid", 409, 409);

    private static Dictionary<string, OwnershipEntry> NewState() => new(StringComparer.Ordinal);

    private static Dictionary<string, OwnershipEntry> Wrap(Dictionary<string, OwnershipEntry> state)
    {
        var normalized = NewState();
        foreach (var (storedKey, entry) in state)
        {
            var parts = storedKey.Split('|', 2);
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]) ||
                entry is null || !Guid.TryParse(entry.OwnerId, out var owner) || owner == Guid.Empty)
            {
                throw InvalidState();
            }

            var normalizedEntry = new OwnershipEntry
            {
                OwnerId = owner.ToString("N"),
                SiteName = entry.SiteName ?? string.Empty
            };
            // Old records already lost PostgreSQL lettercase. Keep their stored spelling;
            // never infer a different physical database from a case-insensitive match.
            var key = Key(parts[0], parts[1]);
            if (normalized.TryGetValue(key, out var previous) &&
                (!string.Equals(previous.OwnerId, normalizedEntry.OwnerId, StringComparison.OrdinalIgnoreCase) ||
                 !string.Equals(previous.SiteName, normalizedEntry.SiteName, StringComparison.OrdinalIgnoreCase)))
            {
                // Alias normalization must not choose one tenant's authority over another.
                // UpdateAsync does not publish anything when this transformation throws.
                throw new AppException("database.ownership_conflict", 409, 409);
            }

            normalized[key] = normalizedEntry;
        }
        return normalized;
    }

    private sealed class OwnershipEntry
    {
        public string OwnerId { get; set; } = string.Empty;
        public string SiteName { get; set; } = string.Empty;
    }
}
