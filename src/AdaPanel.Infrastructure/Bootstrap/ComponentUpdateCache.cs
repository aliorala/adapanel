using System.Text.Json;
using System.Collections.Concurrent;

namespace AdaPanel.Infrastructure.Bootstrap;

internal static class ComponentUpdateCache
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static object Gate(string path) => Gates.GetOrAdd(Path.GetFullPath(path), _ => new object());
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    internal static Dictionary<string, ComponentUpdateInfo> Read(string path)
    {
        lock (Gate(path)) return ReadCore(path);
    }

    private static Dictionary<string, ComponentUpdateInfo> ReadCore(string path)
    {
        if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var data = JsonSerializer.Deserialize<Dictionary<string, ComponentUpdateInfo>>(stream, Options)
            ?? throw new InvalidDataException("Component update cache is empty.");
        if (data.Any(entry => entry.Value is null || string.IsNullOrWhiteSpace(entry.Value.AvailableVersion)))
            throw new InvalidDataException("Component update cache contains an incomplete result.");
        return new(data, StringComparer.OrdinalIgnoreCase);
    }

    internal static void Write(string path, Dictionary<string, ComponentUpdateInfo> data)
    {
        // Windows can deny replacement while another request still holds the old file.
        lock (Gate(path)) WriteCore(path, data);
    }

    private static void WriteCore(string path, Dictionary<string, ComponentUpdateInfo> data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, data, Options);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

internal sealed class ComponentUpdateInfo
{
    public string AvailableVersion { get; set; } = string.Empty;
    // Kept for compatibility with existing cache files. Readers compare actual installed versions.
    public bool UpdateAvailable { get; set; }
    public bool VersionCheckSupported { get; set; }

    public bool VersionCheckFailed { get; set; }
    public DateTime CheckedAtUtc { get; set; }
}
