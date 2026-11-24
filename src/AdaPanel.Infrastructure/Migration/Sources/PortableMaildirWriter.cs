using System.IO.Compression;
using System.Text.Json;
using AdaPanel.Infrastructure.Migration.Packages;

namespace AdaPanel.Infrastructure.Migration.Sources;

public sealed record PortableMaildirArchiveResult(long EmailCount, long FileCount);
public sealed record PortableMaildirFileMetadata(string Path, DateTimeOffset SourceDateUtc, int UnixMode, string MaildirFlags, bool IsMessage);

/// <summary>Shared cPanel/Plesk producer. Dates come from source TAR metadata, never the MIME Date header.</summary>
public static class PortableMaildirWriter
{
    public const string MetadataEntryName = "__adapanel_source_metadata__.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<PortableMaildirArchiveResult> CreateAsync(SourceArchiveInventory source, string mailRoot,
        string destination, PortableMigrationPackageOptions limits, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        PortableMigrationStorage.Guard(destination);
        SourceArchiveReader.ValidateLogicalPath(mailRoot);
        var prefix = mailRoot + "/";
        var files = source.FilesUnder(mailRoot).ToArray();
        var directories = source.DirectoriesUnder(mailRoot).ToArray();
        if ((long)files.Length + directories.Length + 1 > limits.MaxItems) throw Error("item_limit");
        foreach (var logical in files.Select(p => p.Key).Concat(directories))
        {
            var relative = logical[prefix.Length..];
            SourceArchiveReader.ValidateLogicalPath(relative);
            if (relative.Equals(MetadataEntryName, StringComparison.OrdinalIgnoreCase) || relative.StartsWith(MetadataEntryName + "/", StringComparison.OrdinalIgnoreCase))
                throw Error("metadata_name_collision");
        }
        foreach (var file in files)
            if (!source.FileMetadata.ContainsKey(file.Key)) throw Error("source_date_missing");
        long emailCount = 0;
        await using var disk = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await using var bounded = new BoundedWriteStream(disk, limits.MaxPayloadBytes);
        using (var zip = new ZipArchive(bounded, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var directory in directories)
            {
                token.ThrowIfCancellationRequested();
                zip.CreateEntry(directory[prefix.Length..] + "/", CompressionLevel.NoCompression);
            }
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                PortableMigrationStorage.Guard(file.Value);
                var relative = file.Key[prefix.Length..];
                var metadata = source.FileMetadata[file.Key];
                var entry = zip.CreateEntry(relative, CompressionLevel.NoCompression);
                var sourceDate = metadata.ModificationTime.ToUniversalTime();
                // DOS ZIP timestamps have a smaller range and two-second precision. The sidecar
                // below remains authoritative and retains the original UTC ticks without clamping.
                entry.LastWriteTime = sourceDate.Year is >= 1980 and <= 2107 ? sourceDate : new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                entry.ExternalAttributes = ((metadata.UnixMode & 0xFFF) | 0x8000) << 16;
                await using var target = entry.Open();
                await using var input = new FileStream(file.Value, FileMode.Open, FileAccess.Read, FileShare.Read);
                await SourceArchiveReader.CopyAsync(input, target, limits.MaxPayloadBytes, token);
            }
            var metadataEntry = zip.CreateEntry(MetadataEntryName, CompressionLevel.NoCompression);
            await using var metadataStream = metadataEntry.Open();
            await using var writer = new Utf8JsonWriter(metadataStream);
            writer.WriteStartObject(); writer.WriteNumber("version", 1); writer.WriteString("format", "maildir-source-metadata");
            writer.WriteStartArray("files");
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                var relative = file.Key[prefix.Length..];
                var metadata = source.FileMetadata[file.Key];
                var slash = relative.LastIndexOf('/');
                var parent = slash < 0 ? "" : relative[..slash];
                var message = parent is "cur" or "new" || parent.EndsWith("/cur", StringComparison.Ordinal) || parent.EndsWith("/new", StringComparison.Ordinal);
                if (message) emailCount++;
                var name = relative[(relative.LastIndexOf('/') + 1)..];
                var marker = name.LastIndexOf(":2,", StringComparison.Ordinal);
                var flags = !message || marker < 0 ? "" : name[(marker + 3)..];
                JsonSerializer.Serialize(writer, new PortableMaildirFileMetadata(relative, metadata.ModificationTime.ToUniversalTime(), metadata.UnixMode, flags, message), Json);
                if (writer.BytesPending >= 64 * 1024) await writer.FlushAsync(token);
            }
            writer.WriteEndArray(); writer.WriteEndObject(); await writer.FlushAsync(token);
        }
        await bounded.FlushAsync(token);
        return new(emailCount, files.Length);
    }
    private static InvalidDataException Error(string key) => new("migration.maildir." + key);
    private sealed class BoundedWriteStream(Stream inner, long limit) : Stream
    {
        private long size;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => size;
        public override long Position { get => size; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) { Check(count); inner.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); inner.Write(buffer); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Check(buffer.Length); await inner.WriteAsync(buffer, cancellationToken); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        private void Check(int count)
        {
            if (count > limit - size) throw Error("archive_size_limit");
            size += count;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
