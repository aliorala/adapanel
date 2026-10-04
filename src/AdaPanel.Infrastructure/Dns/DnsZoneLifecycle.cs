namespace AdaPanel.Infrastructure.Dns;

/// <summary>Serializes one site's desired state, native sync and publication bookkeeping.</summary>
internal static class DnsZoneLifecycle
{
    private sealed class Entry
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal int References;
    }

    private static readonly Dictionary<(string Store, string Site), Entry> Entries = [];

    internal static async Task<IDisposable> EnterAsync(string statePath, string siteName, CancellationToken token)
    {
        var key = (Path.GetFullPath(statePath).ToUpperInvariant(), siteName.ToUpperInvariant());
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

    private static void Return((string Store, string Site) key, Entry entry)
    {
        lock (Entries)
        {
            if (--entry.References != 0) return;
            Entries.Remove(key);
            entry.Gate.Dispose();
        }
    }

    private sealed class Lease((string Store, string Site) key, Entry entry) : IDisposable
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
