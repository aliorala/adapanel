using System.Buffers.Binary;
using System.IO.Compression;
using AdaPanel.Infrastructure.Migration.Packages;
using ZstdSharp;

namespace AdaPanel.Infrastructure.Migration.Sources;

/// <summary>Disk-backed Plesk archive layers sharing one expansion budget across all referenced payloads.</summary>
internal sealed class PleskArchiveReader(string stage, PortableMigrationPackageOptions limits, CancellationToken token)
{
    private long bytes;
    private int items;
    private readonly Dictionary<string, SourceArchiveInventory> cache = new(StringComparer.OrdinalIgnoreCase);
    internal bool HasZipWallClockDates { get; private set; }
    internal async Task<SourceArchiveInventory> ExtractAsync(string path, string format, int depth = 0)
    {
        if (depth > 6) throw Error("archive_depth_limit");
        if (cache.TryGetValue(path, out var known)) return known;
        var folder = Path.Combine(stage, "layer-" + Guid.NewGuid().ToString("N"));
        PortableMigrationStorage.CreatePrivateDirectory(folder, limits.PublicWebRoots);
        var remaining = Remaining();
        SourceArchiveInventory result;
        if (format == "zip") result = await ExtractZipAsync(path, folder, remaining);
        else
        {
            var gzipPath = path;
            if (format is "tar" or "tzst")
            {
                gzipPath = Path.Combine(folder, "wrapped.tgz");
                await using var raw = File.OpenRead(path);
                using var decoded = format == "tzst" ? new DecompressionStream(raw, leaveOpen: true) : null;
                decoded?.SetParameter(ZstdSharp.Unsafe.ZSTD_dParameter.ZSTD_d_windowLogMax, 27);
                await using var compressed = new FileStream(gzipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true);
                var expanded = await SourceArchiveReader.CopyAsync(decoded ?? (Stream)raw, gzip, remaining.MaxTotalUncompressedBytes, token);
                if (format == "tzst" && (double)expanded / Math.Max(1, raw.Length) > limits.MaxCompressionRatio) throw Error("compression_limit");
            }
            else if (format != "tgz") throw Error("compression_unsupported");
            await using var input = File.OpenRead(gzipPath);
            result = await SourceArchiveReader.ExtractTarGzipAsync(input, folder, remaining, token);
        }
        var expandedBytes = result.Files.Values.Sum(file => new FileInfo(file).Length);
        var expandedItems = result.Files.Count + result.Directories.Count;
        if (expandedBytes > limits.MaxTotalUncompressedBytes - bytes || expandedItems > limits.MaxItems - items) throw Error("aggregate_limit");
        bytes += expandedBytes; items += expandedItems;
        cache.Add(path, result);
        return result;
    }
    internal static string Format(string path) => path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) ? "tgz"
        : path.EndsWith(".tzst", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".tar.zst", StringComparison.OrdinalIgnoreCase) ? "tzst"
        : path.EndsWith(".tar", StringComparison.OrdinalIgnoreCase) ? "tar"
        : path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? "zip" : "";
    private PortableMigrationPackageOptions Remaining() => new()
    {
        PublicWebRoots = limits.PublicWebRoots, MaxUploadBytes = Math.Max(limits.MaxUploadBytes, limits.MaxTotalUncompressedBytes),
        MaxPayloadBytes = limits.MaxPayloadBytes, MaxTotalUncompressedBytes = limits.MaxTotalUncompressedBytes - bytes,
        MaxItems = limits.MaxItems - items, MaxCompressionRatio = limits.MaxCompressionRatio, MaxManifestBytes = limits.MaxManifestBytes
    };
    private async Task<SourceArchiveInventory> ExtractZipAsync(string path, string folder, PortableMigrationPackageOptions remaining)
    {
        // DOS timestamps have no source timezone. Preserve the exposed ZIP time but require review;
        // never describe it as a proven original UTC delivery date.
        HasZipWallClockDates = true;
        await using var input = File.OpenRead(path);
        var checksums = await PreflightZipAsync(input, remaining.MaxItems, token);
        using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        if (zip.Entries.Count != checksums.Count) throw Error("zip_invalid");
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var metadata = new Dictionary<string, SourceArchiveFileMetadata>(StringComparer.Ordinal);
        var dirs = new HashSet<string>(StringComparer.Ordinal);
        var allNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var explicitNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        for (var index = 0; index < zip.Entries.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var entry = zip.Entries[index];
            var directory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\') || (entry.ExternalAttributes & 0x10) != 0;
            var name = entry.FullName.Replace('\\', '/').TrimEnd('/');
            SourceArchiveReader.ValidateLogicalPath(name);
            if (!explicitNames.Add(name)) throw Error("duplicate_or_case_collision");
            var unixType = (entry.ExternalAttributes >> 16) & 0xf000;
            if (unixType is not (0 or 0x8000 or 0x4000) || (unixType == 0x4000) != directory && unixType != 0)
                throw Error("zip_special_entry");
            var segments = name.Split('/'); var prefix = "";
            for (var i = 0; i < segments.Length; i++)
            {
                prefix = prefix.Length == 0 ? segments[i] : prefix + "/" + segments[i];
                if (allNames.TryGetValue(prefix, out var original) && original != prefix || i < segments.Length - 1 && files.ContainsKey(prefix)) throw Error("path_collision");
                allNames[prefix] = prefix;
                if (allNames.Count > remaining.MaxItems) throw Error("item_limit");
                if (i < segments.Length - 1) dirs.Add(prefix);
            }
            if (directory)
            {
                if (files.ContainsKey(name) || entry.Length != 0) throw Error("path_collision");
                dirs.Add(name); continue;
            }
            if (dirs.Contains(name) || entry.Length > remaining.MaxPayloadBytes || entry.Length > remaining.MaxTotalUncompressedBytes - expanded ||
                entry.Length > 0 && (entry.CompressedLength == 0 || (double)entry.Length / entry.CompressedLength > remaining.MaxCompressionRatio)) throw Error("payload_limit");
            var disk = Path.Combine(folder, "entry-" + Guid.NewGuid().ToString("N"));
            await using (var source = entry.Open())
            await using (var target = new FileStream(disk, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var crc = new SourceArchiveReader.Crc32();
                var copied = await SourceArchiveReader.CopyAsync(source, target, entry.Length, token, crc);
                if (copied != entry.Length || crc.Value != checksums[index]) throw Error("zip_truncated_or_crc");
            }
            File.SetLastWriteTimeUtc(disk, entry.LastWriteTime.UtcDateTime);
            files.Add(name, disk); expanded += entry.Length;
            metadata.Add(name, new(entry.LastWriteTime.ToUniversalTime(), (entry.ExternalAttributes >> 16) & 0xfff));
        }
        return new(files, dirs) { FileMetadata = metadata };
    }
    private static async Task<List<uint>> PreflightZipAsync(FileStream source, int limit, CancellationToken token)
    {
        if (source.Length < 22) throw Error("zip_invalid");
        var tail = new byte[(int)Math.Min(source.Length, 65557)];
        source.Position = source.Length - tail.Length; await source.ReadExactlyAsync(tail, token);
        var at = -1;
        for (var i = tail.Length - 22; i >= 0; i--)
            if (U32(tail, i) == 0x06054b50 && i + 22 + U16(tail, i + 20) == tail.Length) { at = i; break; }
        if (at < 0 || U16(tail, at + 4) != 0 || U16(tail, at + 6) != 0 || U16(tail, at + 8) != U16(tail, at + 10)) throw Error("zip_multivolume_or_invalid");
        ulong count = U16(tail, at + 10), size = U32(tail, at + 12), start = U32(tail, at + 16);
        var end = source.Length - tail.Length + at;
        if (count == ushort.MaxValue || size == uint.MaxValue || start == uint.MaxValue)
        {
            if (end < 20) throw Error("zip64_invalid");
            var locator = new byte[20]; source.Position = end - 20; await source.ReadExactlyAsync(locator, token);
            if (U32(locator, 0) != 0x07064b50 || U32(locator, 4) != 0 || U32(locator, 16) != 1) throw Error("zip64_invalid");
            var offset = BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8));
            if (offset > (ulong)(end - 20 - 56)) throw Error("zip64_invalid");
            source.Position = (long)offset; var record = new byte[56]; await source.ReadExactlyAsync(record, token);
            ulong R(int index) => BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(index));
            if (U32(record, 0) != 0x06064b50 || R(4) < 44 || U32(record, 16) != 0 || U32(record, 20) != 0 || R(24) != R(32)) throw Error("zip64_invalid");
            count = R(32); size = R(40); start = R(48); end = (long)offset;
        }
        if (count > (ulong)Math.Max(0, limit) || size > 128UL * 1024 * 1024 || start > (ulong)end || size != (ulong)end - start) throw Error("zip_metadata_limit");
        source.Position = (long)start; var checksums = new List<uint>((int)count);
        for (ulong i = 0; i < count; i++)
        {
            var header = new byte[46]; await source.ReadExactlyAsync(header, token);
            if (U32(header, 0) != 0x02014b50 || (U16(header, 8) & 1) != 0 || U16(header, 34) != 0) throw Error("zip_encrypted_or_invalid");
            var rest = U16(header, 28) + U16(header, 30) + U16(header, 32);
            if (source.Position + rest > end) throw Error("zip_invalid");
            checksums.Add(U32(header, 16)); source.Position += rest;
        }
        if (source.Position != end) throw Error("zip_invalid");
        source.Position = 0; return checksums;
    }
    private static ushort U16(byte[] data, int at) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at));
    private static uint U32(byte[] data, int at) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
    private static InvalidDataException Error(string code) => new("migration.plesk." + code);
}
