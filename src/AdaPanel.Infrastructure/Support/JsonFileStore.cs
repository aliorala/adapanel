using System.Collections.Concurrent;
using System.Security.AccessControl;
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

    internal Action<string, string>? AtomicReplaceOverride { get; init; }

    public async Task<T> ReadAsync<T>(string relativePath, T fallbackValue, CancellationToken cancellationToken = default)
    {
        var filePath = ResolvePath(relativePath);
        if (!File.Exists(filePath))
        {
            return fallbackValue;
        }

        // Paylaşımlı + Delete izinli aç: atomik yazım (File.Replace) okuma sırasında dosyayı
        // değiştirse bile paylaşım ihlali olmaz; okuyucu eski içeriği görmeye devam eder.
        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var value = await JsonSerializer.DeserializeAsync<T>(stream, SerializerOptions, cancellationToken);
        return value is null ? fallbackValue : value;
    }

    public async Task WriteAsync<T>(string relativePath, T value, CancellationToken cancellationToken = default)
    {
        // Serialize writers, including the first publication and replacement.
        // UpdateCoreAsync already holds this same gate and calls WriteCore directly.
        var key = ResolvePath(relativePath).ToLowerInvariant();
        var gate = PathLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try { await WriteCoreAsync(relativePath, value, null, cancellationToken); }
        finally { gate.Release(); }
    }

    private async Task WriteCoreAsync<T>(string relativePath, T value,
        Func<CancellationToken, Task>? beforePublication, CancellationToken cancellationToken)
    {
        var filePath = ResolvePath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? dataRoot);

        // ATOMİK yayın için önce tam JSON geçici dosyaya yazılır. Windows ReplaceFile'ın
        // kısmi başarısızlıklarında hedef adı kaybolabilir; bu durumda kurtarma dosyası korunur.
        // File.Create doğrudan kullanılsa yarım/bozuk JSON kalıp sonraki okumayı çökertebilirdi.
        var tempPath = filePath + ".tmp" + Guid.NewGuid().ToString("N");
        var preserveRecoveryFile = false;
        var replacementStarted = false;
        try
        {
            await using (var stream = CreatePublicationFile(tempPath, filePath))
            {
                await JsonSerializer.SerializeAsync(stream, value, SerializerOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            // Validate after serialization as well as any per-file lock/read wait. Until this
            // point the destination is unchanged; rejected authority leaves only a temp file.
            cancellationToken.ThrowIfCancellationRequested();
            if (beforePublication is not null) await beforePublication(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(filePath))
            {
                // On Windows MoveFileEx(overwrite) can reject an open destination
                // even when the reader shares Delete. ReplaceFile preserves the
                // old reader's snapshot while atomically publishing the new file.
                replacementStarted = true;
                await ReplaceWithRetryAsync(tempPath, filePath, beforePublication, cancellationToken, AtomicReplaceOverride);
            }
            else
            {
                try { File.Move(tempPath, filePath); }
                catch (IOException) when (File.Exists(filePath) && File.Exists(tempPath))
                {
                    // Another writer may have created the destination after Exists.
                    // Do not delete it first: readers must always see complete JSON.
                    replacementStarted = true;
                    await ReplaceWithRetryAsync(tempPath, filePath, beforePublication, cancellationToken, AtomicReplaceOverride);
                }
            }
        }
        catch (IOException error) when (replacementStarted && OperatingSystem.IsWindows()
            && error.HResult is unchecked((int)0x80070498) or unchecked((int)0x80070499))
        {
            // ReplaceFile 1176 can remove the destination; 1177 can rename it.
            // The complete replacement may now be the only recovery copy. Preserve it,
            // propagate the original failure and never attempt an overwrite fallback.
            preserveRecoveryFile = true;
            throw;
        }
        finally
        {
            if (!preserveRecoveryFile && File.Exists(tempPath))
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

    private static FileStream CreatePublicationFile(string tempPath, string filePath)
    {
        if (OperatingSystem.IsWindows() && File.Exists(filePath))
        {
            // A failed ReplaceFile can leave this file for recovery. Apply the existing
            // destination's DACL at creation, before any potentially secret JSON is written;
            // a protected file must never gain broader inherited directory permissions.
            var security = new FileInfo(filePath).GetAccessControl(AccessControlSections.Access);
            return new FileInfo(tempPath).Create(FileMode.CreateNew,
                FileSystemRights.Write | FileSystemRights.ReadData | FileSystemRights.Delete,
                FileShare.None, 4096, FileOptions.None, security);
        }

        return new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    }

    // ReplaceFile preserves the destination DACL and open readers' snapshots. A scanner
    // or a reader without Delete sharing can briefly prevent replacement. Only retry
    // errors that leave both names intact (1175 is ERROR_UNABLE_TO_REMOVE_REPLACED).
    // In particular, 1176/1177 can leave different file names and must not be retried.
    internal static async Task ReplaceWithRetryAsync(string tempPath, string filePath,
        Func<CancellationToken, Task>? beforePublication, CancellationToken cancellationToken,
        Action<string, string>? replace = null)
    {
        const int maxAttempts = 6;
        replace ??= static (source, destination) => File.Replace(source, destination, null);
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attempt > 0 && beforePublication is not null) await beforePublication(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                replace(tempPath, filePath);
                return;
            }
            catch (IOException error) when (OperatingSystem.IsWindows()
                && attempt < maxAttempts - 1
                && error.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021) or unchecked((int)0x80070497)
                && File.Exists(tempPath) && File.Exists(filePath))
            {
                // Total delay is bounded to 1.55 seconds; preserve the original exception
                // if contention persists. Cancellation also releases the per-path gate.
                await Task.Delay(TimeSpan.FromMilliseconds(50 * (1 << attempt)), cancellationToken);
            }
        }
    }

    /// <summary>
    /// Dosyayı ATOMİK oku-değiştir-yaz yapar: dosya başına kilit altında okur, <paramref name="mutate"/>
    /// ile değiştirir, yazar. Eşzamanlı çağrılarda kayıp güncelleme olmaz (ör. iki DB kullanıcısı aynı
    /// anda eklenince ikisi de korunur). Güncellenmiş değeri döner.
    /// </summary>
    public Task<T> UpdateAsync<T>(string relativePath, T fallbackValue, Func<T, T> mutate, CancellationToken cancellationToken = default) =>
        UpdateCoreAsync(relativePath, fallbackValue, mutate, null, cancellationToken);

    /// <summary>Checks authority immediately before atomic publication, under the path lock.</summary>
    internal Task<T> UpdateAsync<T>(string relativePath, T fallbackValue, Func<T, T> mutate,
        Func<CancellationToken, Task>? beforePublication, CancellationToken cancellationToken) =>
        UpdateCoreAsync(relativePath, fallbackValue, mutate, beforePublication, cancellationToken);

    private async Task<T> UpdateCoreAsync<T>(string relativePath, T fallbackValue, Func<T, T> mutate,
        Func<CancellationToken, Task>? beforePublication, CancellationToken cancellationToken)
    {
        var key = ResolvePath(relativePath).ToLowerInvariant();
        var gate = PathLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = await ReadAsync(relativePath, fallbackValue, cancellationToken);
            var updated = mutate(current);
            await WriteCoreAsync(relativePath, updated, beforePublication, cancellationToken);
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
