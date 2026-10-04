namespace AdaPanel.Infrastructure.SelfUpdate;

/// <summary>Orders release checks and their cached outcomes for one data root.</summary>
/// <remarks>This gate may wait for GitHub. It must be released before taking the pending-package gate.</remarks>
internal static class SelfUpdateCheckLifecycle
{
    private sealed class Entry
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal int References;
    }

    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);

    internal static int ReferencesForPath(string path)
    {
        lock (Entries) return Entries.GetValueOrDefault(Path.GetFullPath(path))?.References ?? 0;
    }

    internal static async Task<IDisposable> EnterAsync(string path, CancellationToken token)
    {
        var key = Path.GetFullPath(path);
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

    private static void Return(string key, Entry entry)
    {
        lock (Entries)
        {
            if (--entry.References != 0) return;
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
