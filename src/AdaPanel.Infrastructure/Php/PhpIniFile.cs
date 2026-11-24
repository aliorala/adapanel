using System.Text;

namespace AdaPanel.Infrastructure.Php;

/// <summary>One lease covers fresh read, transformation, publication and any verification rollback.</summary>
internal static class PhpIniFile
{
    private sealed class Entry { internal readonly SemaphoreSlim Gate = new(1, 1); internal int References; }
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);
    internal static int ReferencesForPath(string path) { lock (Entries) return Entries.GetValueOrDefault(Path.GetFullPath(path))?.References ?? 0; }
    internal static async Task<IDisposable> EnterAsync(string path, CancellationToken token)
    {
        var key = Path.GetFullPath(path);
        Entry entry;
        lock (Entries)
        {
            if (!Entries.TryGetValue(key, out entry!)) Entries.Add(key, entry = new Entry());
            entry.References++;
        }
        try { await entry.Gate.WaitAsync(token); }
        catch { Return(key, entry); throw; }
        var lease = new Lease(key, entry);
        try { token.ThrowIfCancellationRequested(); return lease; }
        catch { lease.Dispose(); throw; }
    }
    private static void Return(string key, Entry entry)
    {
        lock (Entries)
            if (--entry.References == 0) { Entries.Remove(key); entry.Gate.Dispose(); }
    }
    private sealed class Lease(string key, Entry entry) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            entry.Gate.Release(); Return(key, entry);
        }
    }

    // Caller owns EnterAsync, including across Redis verification and compensation.
    internal static async Task<string[]> ReadLinesAsync(string path, CancellationToken token)
    {
        using var lease = await EnterAsync(path, token);
        return await File.ReadAllLinesAsync(path, token);
    }

    internal static Task PublishLinesAsync(string path, IEnumerable<string> lines, CancellationToken token) =>
        PublishBytesAsync(path, new UTF8Encoding(false).GetBytes(string.Join(Environment.NewLine, lines) + Environment.NewLine), token);

    internal static async Task PublishBytesAsync(string path, byte[] bytes, CancellationToken token,
        Func<Stream, Stream>? writeStream = null)
    {
        token.ThrowIfCancellationRequested();
        var temporary = path + ".adapanel-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var stream = writeStream?.Invoke(file) ?? file;
                await stream.WriteAsync(bytes, token);
                await stream.FlushAsync(token);
            }
            token.ThrowIfCancellationRequested();
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
