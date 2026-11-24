using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
using AdaPanel.Infrastructure.Migration.Packages;

namespace AdaPanel.Infrastructure.Migration.Sources;

public sealed record SourceArchiveFileMetadata(DateTimeOffset ModificationTime, int UnixMode);
public sealed record SourceArchiveInventory(IReadOnlyDictionary<string, string> Files, IReadOnlySet<string> Directories)
{
    public IReadOnlyDictionary<string, SourceArchiveFileMetadata> FileMetadata { get; init; } = new Dictionary<string, SourceArchiveFileMetadata>(StringComparer.Ordinal);
    private string[]? sortedFiles;
    private string[]? sortedDirectories;
    public IEnumerable<KeyValuePair<string, string>> FilesUnder(string root)
        => Under(sortedFiles ??= Files.Keys.Order(StringComparer.Ordinal).ToArray(), root).Select(p => new KeyValuePair<string, string>(p, Files[p]));
    public IEnumerable<string> DirectoriesUnder(string root)
        => Under(sortedDirectories ??= Directories.Order(StringComparer.Ordinal).ToArray(), root);
    private static IEnumerable<string> Under(string[] sorted, string root)
    {
        var prefix = root + "/";
        var index = Array.BinarySearch(sorted, prefix, StringComparer.Ordinal);
        if (index < 0) index = ~index;
        for (; index < sorted.Length && sorted[index].StartsWith(prefix, StringComparison.Ordinal); index++) yield return sorted[index];
    }
}

/// <summary>Source names stay logical; random disk names also preserve UNIX Maildir ':' names without ADS.</summary>
public static class SourceArchiveReader
{
    public static async Task<SourceArchiveInventory> ExtractTarGzipAsync(Stream source, string privateStage,
        PortableMigrationPackageOptions limits, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        token.ThrowIfCancellationRequested();
        PortableMigrationStorage.Guard(privateStage);
        var gzipPath = Path.Combine(privateStage, "source.tar.gz");
        var tarPath = Path.Combine(privateStage, "source.tar");
        await using (var compressed = new FileStream(gzipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await CopyAsync(source, compressed, limits.MaxUploadBytes, token);
        await using (var compressed = new FileStream(gzipPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            if (compressed.Length < 18) throw Error("gzip_truncated");
            var footer = new byte[8];
            compressed.Position = compressed.Length - 8;
            await compressed.ReadExactlyAsync(footer, token);
            compressed.Position = 0;
            using var gzip = new GZipStream(compressed, CompressionMode.Decompress, leaveOpen: true);
            await using var tar = new FileStream(tarPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var crc = new Crc32();
            var length = await CopyAsync(gzip, tar, limits.MaxTotalUncompressedBytes, token, crc);
            if (crc.Value != BinaryPrimitives.ReadUInt32LittleEndian(footer)
                || unchecked((uint)length) != BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(4))) throw Error("gzip_truncated_or_multimember");
            if (length < 1024 || length % 512 != 0 || (double)length / compressed.Length > limits.MaxCompressionRatio) throw Error("tar_size_or_compression_limit");
        }
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var metadata = new Dictionary<string, SourceArchiveFileMetadata>(StringComparer.Ordinal);
        var directories = new HashSet<string>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var explicitNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long payloadBytes = 0;
        var count = 0;
        await using (var tarStream = new FileStream(tarPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // Require the actual POSIX end marker; a truncated TarReader can otherwise return EOF.
            var end = new byte[1024];
            tarStream.Position = tarStream.Length - end.Length;
            await tarStream.ReadExactlyAsync(end, token);
            if (end.Any(b => b != 0)) throw Error("tar_truncated");
            await PreflightTarAsync(tarStream, limits, token);
            tarStream.Position = 0;
            using var reader = new TarReader(tarStream, leaveOpen: true);
            while (await reader.GetNextEntryAsync(copyData: false, cancellationToken: token) is { } entry)
            {
                token.ThrowIfCancellationRequested();
                if (++count > limits.MaxItems) throw Error("item_limit");
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory)) throw Error("tar_special_entry");
                if (entry is PaxTarEntry pax && pax.ExtendedAttributes.Keys.Any(key => key.StartsWith("GNU.sparse", StringComparison.Ordinal) || key == "SCHILY.filetype")) throw Error("tar_sparse_or_special");
                if (entry.LinkName.Length != 0) throw Error("tar_link");
                var name = entry.Name;
                if (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
                if (entry.EntryType == TarEntryType.Directory) name = name.TrimEnd('/');
                if (name is "" or ".")
                {
                    if (entry.EntryType != TarEntryType.Directory) throw Error("path_invalid");
                    continue;
                }
                ValidateLogicalPath(name);
                if (!explicitNames.Add(name)) throw Error("duplicate_or_case_collision");
                var pieces = name.Split('/');
                var prefix = "";
                for (var i = 0; i < pieces.Length; i++)
                {
                    prefix = prefix.Length == 0 ? pieces[i] : prefix + "/" + pieces[i];
                    if (names.TryGetValue(prefix, out var original) && original != prefix) throw Error("case_collision");
                    names[prefix] = prefix;
                    if (names.Count > limits.MaxItems) throw Error("item_limit");
                    if (i < pieces.Length - 1)
                    {
                        if (files.ContainsKey(prefix)) throw Error("path_conflict");
                        directories.Add(prefix);
                    }
                }
                if (entry.EntryType == TarEntryType.Directory)
                {
                    if (entry.Length != 0 || files.ContainsKey(name)) throw Error("directory_invalid");
                    directories.Add(name);
                    continue;
                }
                if (directories.Contains(name)) throw Error("path_conflict");
                if (entry.Length < 0 || entry.Length > limits.MaxPayloadBytes || entry.Length > limits.MaxTotalUncompressedBytes - payloadBytes) throw Error("payload_size_limit");
                payloadBytes += entry.Length;
                var path = Path.Combine(privateStage, "entry-" + Guid.NewGuid().ToString("N"));
                PortableMigrationStorage.Guard(path);
                await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var copied = entry.DataStream is null ? 0 : await CopyAsync(entry.DataStream, output, entry.Length, token);
                if (copied != entry.Length) throw Error("tar_truncated");
                files.Add(name, path);
                metadata.Add(name, new SourceArchiveFileMetadata(entry.ModificationTime.ToUniversalTime(), (int)entry.Mode));
            }
            // No hidden second tar/archive after a premature end marker.
            var remainder = new byte[64 * 1024];
            int read;
            while ((read = await tarStream.ReadAsync(remainder, token)) != 0)
                if (remainder.AsSpan(0, read).IndexOfAnyExcept((byte)0) >= 0) throw Error("tar_trailing_data");
        }
        return new SourceArchiveInventory(files, directories) { FileMetadata = metadata };
    }

    private static async Task PreflightTarAsync(FileStream tar, PortableMigrationPackageOptions limits, CancellationToken token)
    {
        tar.Position = 0;
        var block = new byte[512];
        var entries = 0;
        while (tar.Position + 512 <= tar.Length)
        {
            await tar.ReadExactlyAsync(block, token);
            if (block.All(b => b == 0))
            {
                if (tar.Position + 512 > tar.Length) throw Error("tar_truncated");
                await tar.ReadExactlyAsync(block, token);
                if (block.Any(b => b != 0)) throw Error("tar_end_marker_invalid");
                while (tar.Position < tar.Length)
                {
                    await tar.ReadExactlyAsync(block, token);
                    if (block.Any(b => b != 0)) throw Error("tar_trailing_data");
                }
                return;
            }
            if (++entries > limits.MaxItems) throw Error("item_limit");
            var checksum = 0UL;
            for (var i = 0; i < block.Length; i++) checksum += i is >= 148 and < 156 ? 32UL : block[i];
            if (TarSize(block.AsSpan(148, 8)) != checksum) throw Error("tar_checksum_invalid");
            var type = block[156];
            if (type is not (0 or (byte)'0' or (byte)'5' or (byte)'x' or (byte)'g' or (byte)'L')) throw Error("tar_special_entry");
            var size = TarSize(block.AsSpan(124, 12));
            if (type is (byte)'x' or (byte)'g' or (byte)'L')
            {
                if (size > (ulong)limits.MaxManifestBytes) throw Error("tar_metadata_limit");
            }
            else if (size > (ulong)limits.MaxPayloadBytes) throw Error("payload_size_limit");
            if (size > (ulong)(tar.Length - tar.Position)) throw Error("tar_truncated");
            var padded = checked((size + 511) / 512 * 512);
            if (padded > (ulong)(tar.Length - tar.Position)) throw Error("tar_truncated");
            tar.Position += (long)padded;
        }
        throw Error("tar_truncated");
    }
    private static ulong TarSize(ReadOnlySpan<byte> field)
    {
        ulong size = 0;
        if ((field[0] & 0x80) != 0)
        {
            if ((field[0] & 0x40) != 0) throw Error("tar_size_invalid");
            size = (uint)(field[0] & 0x7f);
            foreach (var b in field[1..])
            {
                if (size > ulong.MaxValue >> 8) throw Error("tar_size_invalid");
                size = (size << 8) | b;
            }
            return size;
        }
        foreach (var b in field)
        {
            if (b is 0 or 32) continue;
            if (b is < (byte)'0' or > (byte)'7' || size > ulong.MaxValue >> 3) throw Error("tar_size_invalid");
            size = (size << 3) | (uint)(b - '0');
        }
        return size;
    }

    public static void ValidateLogicalPath(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 4096 || name.StartsWith('/') || name.Contains('\\') || name.Any(char.IsControl)) throw Error("path_invalid");
        var segments = name.Split('/');
        if (segments[0].Contains(':') || segments.Any(p => p is "" or "." or ".." || p.Length > 255)) throw Error("path_invalid");
    }

    internal static async Task<long> CopyAsync(Stream source, Stream destination, long limit, CancellationToken token, Crc32? crc = null)
    {
        var buffer = new byte[64 * 1024];
        long size = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, token)) != 0)
        {
            token.ThrowIfCancellationRequested();
            if (read > limit - size) throw Error("stream_size_limit");
            size += read;
            crc?.Append(buffer.AsSpan(0, read));
            await destination.WriteAsync(buffer.AsMemory(0, read), token);
        }
        token.ThrowIfCancellationRequested();
        return size;
    }
    private static InvalidDataException Error(string code) => new("migration.source." + code);
    internal sealed class Crc32
    {
        private uint value = uint.MaxValue;
        private static readonly uint[] Table = Enumerable.Range(0, 256).Select(i =>
        {
            var crc = (uint)i;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            return crc;
        }).ToArray();
        internal uint Value => ~value;
        internal void Append(ReadOnlySpan<byte> data)
        {
            foreach (var b in data) value = Table[(value ^ b) & 255] ^ (value >> 8);
        }
    }
}
