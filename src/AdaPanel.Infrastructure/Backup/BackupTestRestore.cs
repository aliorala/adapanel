using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using AdaPanel.Core.Dtos.Backup;

namespace AdaPanel.Infrastructure.Backup;

/// <summary>
/// Arsivi GECICI bir klasore acip butunlugunu olcer (yalitilmis test geri yukleme). Canli veriye
/// hicbir yerde dokunmaz: yalniz verilen <c>workRoot</c> altinda yazar ve isi bitince o klasoru siler.
/// <para>
/// Olculenler: her girdinin adi guvenli mi (zip-slip), acilan bayt == beyan edilen boyut, CRC32 ==
/// merkezi dizindeki CRC; <c>databases/*.sql</c> dokumleri tamamlanmis mi (mysqldump kapanis satiri) ve
/// icinde sema/veri var mi. Olculmeyenler (<c>.bak</c>, taninmayan) "gecti" SAYILMAZ, ayri sayilir.
/// </para>
/// </summary>
internal static class BackupTestRestore
{
    /// <summary>Gecici klasore yazilacak toplam bayt tavani; asildiginda kalan girdiler diske yazilmadan okunur.</summary>
    internal const long DefaultExtractCapBytes = 2L * 1024 * 1024 * 1024;

    internal static async Task<BackupTestRestoreResultDto> RunAsync(
        BackupRecordDto record, string archivePath, string workRoot, long extractCapBytes, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var result = new BackupTestRestoreResultDto { BackupId = record.Id, Name = record.Name, LiveDataTouched = false };
        var workDirectory = Path.Combine(workRoot, "test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workDirectory);
            if (IsZip(archivePath))
                await VerifyZipAsync(archivePath, workDirectory, extractCapBytes, result, cancellationToken);
            else if (archivePath.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            {
                result.EntryCount = 1;
                await CheckDumpAsync(File.OpenRead(archivePath), Path.GetFileName(archivePath), result, cancellationToken);
            }
            else
            {
                result.Outcome = "not_tested";
                result.NotTestedReason = "backup.test_restore.unsupported_format";
            }

            if (result.Outcome != "not_tested")
                result.Outcome = result.FileFailures == 0 && result.DatabaseDumpFailures == 0 ? "passed" : "failed";
        }
        catch (InvalidDataException error)
        {
            result.Outcome = "failed";
            result.FileFailures++;
            result.Issues.Add("archive=unreadable; reason=" + Trim(error.Message));
        }
        finally
        {
            result.CleanedUp = DeleteAndConfirm(workDirectory);
            if (!result.CleanedUp) result.Issues.Add("cleanup=failed; path=" + workDirectory);
            result.DurationMs = clock.ElapsedMilliseconds;
        }
        return result;
    }

    private static bool IsZip(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> head = stackalloc byte[4];
        return stream.Read(head) == 4 && head[0] == 0x50 && head[1] == 0x4B;
    }

    private static async Task VerifyZipAsync(string archivePath, string workDirectory, long extractCap,
        BackupTestRestoreResultDto result, CancellationToken cancellationToken)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        var fullRoot = Path.GetFullPath(workDirectory) + Path.DirectorySeparatorChar;
        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
            result.EntryCount++;

            var target = Path.GetFullPath(Path.Combine(workDirectory, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                // Zip-slip: arsivin KENDISI zararli/bozuk; hicbir yere yazilmaz.
                result.FileFailures++;
                result.Issues.Add($"entry={Trim(entry.FullName)}; reason=unsafe_path");
                continue;
            }

            var toDisk = result.ExtractedBytes + entry.Length <= extractCap;
            uint crc = 0;
            long read = 0;
            try
            {
                await using var input = entry.Open();
                Stream? output = null;
                if (toDisk)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                }
                try
                {
                    var buffer = new byte[81920];
                    int count;
                    while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        crc = Crc32.Update(crc, buffer.AsSpan(0, count));
                        read += count;
                        if (output is not null) await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    }
                }
                finally { if (output is not null) await output.DisposeAsync(); }
            }
            catch (Exception error) when (error is InvalidDataException or IOException or NotSupportedException)
            {
                result.FileFailures++;
                result.Issues.Add($"entry={Trim(entry.FullName)}; reason=read_failed; detail={Trim(error.Message)}");
                continue;
            }

            if (toDisk) result.ExtractedBytes += read; else result.StreamVerifiedBytes += read;
            if (read != entry.Length)
            {
                result.FileFailures++;
                result.Issues.Add($"entry={Trim(entry.FullName)}; reason=size_mismatch; expected={entry.Length}; actual={read}");
            }
            else if (crc != entry.Crc32)
            {
                result.FileFailures++;
                result.Issues.Add($"entry={Trim(entry.FullName)}; reason=crc_mismatch");
            }
            else if (entry.FullName.StartsWith("databases/", StringComparison.OrdinalIgnoreCase))
            {
                if (entry.FullName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) && toDisk)
                    await CheckDumpAsync(File.OpenRead(target), entry.FullName, result, cancellationToken);
                else if (entry.FullName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                    result.DatabaseDumpsUnchecked++;
                else if (!entry.FullName.EndsWith('/'))
                    result.DatabaseDumpsUnchecked++;
            }
        }
        if (result.EntryCount == 0)
        {
            result.FileFailures++;
            result.Issues.Add("archive=empty");
        }
    }

    private static async Task CheckDumpAsync(Stream stream, string name, BackupTestRestoreResultDto result, CancellationToken cancellationToken)
    {
        await using var _ = stream;
        result.DatabaseDumpsChecked++;
        var statements = 0;
        var completed = false;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (line.StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("INSERT INTO", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("CREATE DATABASE", StringComparison.OrdinalIgnoreCase)) statements++;
            if (line.StartsWith("-- Dump completed", StringComparison.OrdinalIgnoreCase)) completed = true;
        }
        if (statements == 0)
        {
            result.DatabaseDumpFailures++;
            result.Issues.Add($"dump={Trim(name)}; reason=no_schema_or_data");
        }
        else if (!completed)
        {
            result.DatabaseDumpFailures++;
            result.Issues.Add($"dump={Trim(name)}; reason=truncated_no_completion_marker");
        }
    }

    private static bool DeleteAndConfirm(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* olcum asagida */ }
        return !Directory.Exists(directory);
    }

    private static string Trim(string value) => value.Length > 200 ? value[..200] : value;

    /// <summary>Kendi CRC32'miz (IEEE): ek paket gerektirmez.</summary>
    private static class Crc32
    {
        private static readonly uint[] Table = Build();

        internal static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            var value = ~crc;
            foreach (var b in data) value = Table[(value ^ b) & 0xFF] ^ (value >> 8);
            return ~value;
        }

        private static uint[] Build()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                var c = i;
                for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }
    }
}
