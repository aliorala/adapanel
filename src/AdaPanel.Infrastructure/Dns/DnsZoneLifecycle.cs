namespace AdaPanel.Infrastructure.Dns;

/// <summary>Site intent precedes canonical native-zone leases; JSON updates are nested last.</summary>
internal static class DnsZoneLifecycle
{
    private sealed class Entry
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal int References;
    }

    private static readonly Dictionary<(string Store, string Kind, string Name), Entry> Entries = [];

    internal static Task<IDisposable> EnterAsync(string statePath, string siteName, CancellationToken token)
        => EnterCoreAsync(statePath, "site", siteName, token);

    internal static Task<IDisposable> EnterNativeAsync(string statePath, string zoneName, CancellationToken token)
        => EnterCoreAsync(statePath, "native", zoneName.TrimEnd('.'), token);

    private static async Task<IDisposable> EnterCoreAsync(string statePath, string kind, string name, CancellationToken token)
    {
        var key = (Path.GetFullPath(statePath).ToUpperInvariant(), kind, name.ToUpperInvariant());
        Entry entry;
        lock (Entries)
        {
            if (!Entries.TryGetValue(key, out entry!)) Entries.Add(key, entry = new Entry());
            entry.References++;
        }
        try { await entry.Gate.WaitAsync(token).ConfigureAwait(false); }
        catch { Return(key, entry); throw; }
        var lease = new Lease(key, entry);
        try { token.ThrowIfCancellationRequested(); return lease; }
        catch { lease.Dispose(); throw; }
    }

    private static void Return((string Store, string Kind, string Name) key, Entry entry)
    {
        lock (Entries)
        {
            if (--entry.References != 0) return;
            Entries.Remove(key);
            entry.Gate.Dispose();
        }
    }

    private sealed class Lease((string Store, string Kind, string Name) key, Entry entry) : IDisposable
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
