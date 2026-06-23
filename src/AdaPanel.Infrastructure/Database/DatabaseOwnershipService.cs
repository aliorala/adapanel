using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Database;

// Plesk benzeri DB sahipliği (config/db-ownership.json). Anahtar = "engine|dbname" (küçük harf).
// Atomik oku-değiştir-yaz (UpdateAsync) ile eşzamanlı kayıplara karşı korunur.
internal sealed class DatabaseOwnershipService(JsonFileStore fileStore) : IDatabaseOwnershipService
{
    private const string StatePath = "config/db-ownership.json";

    private static string Key(string engine, string databaseName) =>
        $"{(engine ?? string.Empty).Trim().ToLowerInvariant()}|{(databaseName ?? string.Empty).Trim().ToLowerInvariant()}";

    public async Task RecordAsync(string engine, string databaseName, Guid ownerUserId, string? siteName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            return;
        }

        await fileStore.UpdateAsync(StatePath, NewState(), existing =>
        {
            var state = Wrap(existing);
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
        await fileStore.UpdateAsync(StatePath, NewState(), existing =>
        {
            var state = Wrap(existing);
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

        await fileStore.UpdateAsync(StatePath, NewState(), existing =>
        {
            var state = Wrap(existing);
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

    private async Task<Dictionary<string, OwnershipEntry>> LoadAsync(CancellationToken cancellationToken) =>
        Wrap(await fileStore.ReadAsync<Dictionary<string, OwnershipEntry>>(StatePath, [], cancellationToken));

    private static Dictionary<string, OwnershipEntry> NewState() => new(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, OwnershipEntry> Wrap(Dictionary<string, OwnershipEntry> state) =>
        new(state, StringComparer.OrdinalIgnoreCase);

    private sealed class OwnershipEntry
    {
        public string OwnerId { get; set; } = string.Empty;
        public string SiteName { get; set; } = string.Empty;
    }
}
