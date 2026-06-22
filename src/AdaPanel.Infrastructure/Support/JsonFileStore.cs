using System.Collections.Concurrent;
using System.Text.Json;
using AdaPanel.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace AdaPanel.Infrastructure.Support;

public sealed class JsonFileStore(IOptions<PanelRuntimeOptions> runtimeOptions)
{
    // Dosya başına kilit: eşzamanlı oku-değiştir-yaz'da kayıp güncellemeyi önler (UpdateAsync).
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathLocks = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string dataRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(runtimeOptions.Value.DataRoot));

    public async Task<T> ReadAsync<T>(string relativePath, T fallbackValue, CancellationToken cancellationToken = default)
    {
        var filePath = ResolvePath(relativePath);
        if (!File.Exists(filePath))
        {
            return fallbackValue;
        }

        // Paylaşımlı + Delete izinli aç: atomik yazım (File.Move overwrite) okuma sırasında dosyayı
        // değiştirse bile paylaşım ihlali olmaz; okuyucu eski içeriği görmeye devam eder.
        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var value = await JsonSerializer.DeserializeAsync<T>(stream, SerializerOptions, cancellationToken);
        return value is null ? fallbackValue : value;
    }

    public async Task WriteAsync<T>(string relativePath, T value, CancellationToken cancellationToken = default)
    {
        var filePath = ResolvePath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? dataRoot);

        // ATOMİK yazım: önce geçici dosyaya yaz, sonra atomik taşı. Yarıda kesilen yazım (çökme,
        // yeniden başlatma, eşzamanlı yazım) hedef dosyayı BOZMAZ — ya eski ya da yeni tam içerik kalır.
        // File.Create doğrudan kullanılsa yarım/bozuk JSON kalıp sonraki okumayı çökertebilirdi.
        var tempPath = filePath + ".tmp" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, value, SerializerOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(tempPath, filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // En iyi çaba; geçici dosya kalsa bile zararsız.
                }
            }
        }
    }

    /// <summary>
    /// Dosyayı ATOMİK oku-değiştir-yaz yapar: dosya başına kilit altında okur, <paramref name="mutate"/>
    /// ile değiştirir, yazar. Eşzamanlı çağrılarda kayıp güncelleme olmaz (ör. iki DB kullanıcısı aynı
    /// anda eklenince ikisi de korunur). Güncellenmiş değeri döner.
    /// </summary>
    public async Task<T> UpdateAsync<T>(string relativePath, T fallbackValue, Func<T, T> mutate, CancellationToken cancellationToken = default)
    {
        var key = ResolvePath(relativePath).ToLowerInvariant();
        var gate = PathLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = await ReadAsync(relativePath, fallbackValue, cancellationToken);
            var updated = mutate(current);
            await WriteAsync(relativePath, updated, cancellationToken);
            return updated;
        }
        finally
        {
            gate.Release();
        }
    }

    public string EnsureDirectory(string relativePath)
    {
        var path = ResolvePath(relativePath);
        Directory.CreateDirectory(path);
        return path;
    }

    public string ResolvePath(string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(dataRoot, normalized));
        // Kök eşitliği VEYA kök+ayraç öneki: "C:\data" ile "C:\data2" karışmasın (sınır kaçışı).
        var rootPrefix = dataRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!string.Equals(fullPath, dataRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) &&
            !fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Requested path escapes AdaPanel data root.");
        }

        return fullPath;
    }
}
