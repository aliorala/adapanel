namespace AdaPanel.Infrastructure.Backup;

/// <summary>Serializes plan, archive and index mutations for one source in one panel store.</summary>
internal static class BackupSourceLifecycle
{
    private sealed class Entry
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal int References;
    }

    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal);

    private static string StoreKey(string indexPath) => Path.GetFullPath(indexPath).ToUpperInvariant() + "\n";

    internal static int CountForStore(string indexPath)
    {
        lock (Entries) return Entries.Keys.Count(key => key.StartsWith(StoreKey(indexPath), StringComparison.Ordinal));
    }

    internal static int ReferencesForSource(string indexPath, string type, string source)
    {
        lock (Entries) return Entries.GetValueOrDefault(StoreKey(indexPath) + BackupSourceIdentity.Key(type, source))?.References ?? 0;
    }

    internal static async Task<IDisposable> EnterAsync(string indexPath, string type, string source,
        CancellationToken cancellationToken)
    {
        var key = StoreKey(indexPath) + BackupSourceIdentity.Key(type, source);
        Entry entry;
        lock (Entries)
        {
            if (!Entries.TryGetValue(key, out entry!)) Entries.Add(key, entry = new Entry());
            entry.References++;
        }
        try { await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch { Return(key, entry); throw; }
        return new Lease(key, entry);
    }

    private static void Return(string key, Entry entry)
    {
        lock (Entries)
            if (--entry.References == 0)
            {
                Entries.Remove(key);
                entry.Gate.Dispose();
            }
    }

    private sealed class Lease(string key, Entry entry) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            entry.Gate.Release();
            Return(key, entry);
        }
    }
}
