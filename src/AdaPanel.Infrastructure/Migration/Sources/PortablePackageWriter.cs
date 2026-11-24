using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Migration.Packages;

namespace AdaPanel.Infrastructure.Migration.Sources;

public static class PortablePackageWriter
{
    public static async Task<PortableMigrationPackage> UploadAsync(PortableMigrationManifest manifest,
        IEnumerable<(string Path, string SourcePath)> payloads, IMigrationPackageStore store, string privateStage,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        PortableMigrationStorage.Guard(privateStage);
        var expectedPayloads = manifest.Payloads.ToDictionary(p => p.Path, StringComparer.Ordinal);
        var zipPath = Path.Combine(privateStage, "normalized-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await using (var output = new FileStream(zipPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var payload in payloads)
                {
                    token.ThrowIfCancellationRequested();
                    PortableMigrationStorage.Guard(payload.SourcePath);
                    var expected = expectedPayloads[payload.Path];
                    await using var source = new FileStream(payload.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (source.Length != expected.Size) throw new InvalidDataException("migration.source.payload_changed");
                    var entry = zip.CreateEntry(payload.Path, CompressionLevel.NoCompression);
                    await using var target = entry.Open();
                    await SourceArchiveReader.CopyAsync(source, target, expected.Size, token);
                }
                var manifestEntry = zip.CreateEntry("manifest.json", CompressionLevel.NoCompression);
                await using var manifestStream = manifestEntry.Open();
                await JsonSerializer.SerializeAsync(manifestStream, manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web), token);
            }
            token.ThrowIfCancellationRequested();
            await using var input = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await store.UploadAsync(input, token);
        }
        finally { if (File.Exists(zipPath)) File.Delete(zipPath); }
    }

    public static async Task<PortableMigrationPayload> DescribeAsync(string logicalPath, string sourcePath, CancellationToken token = default)
    {
        PortableMigrationStorage.Guard(sourcePath);
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA256.HashDataAsync(source, token);
        return new() { Path = logicalPath, Size = source.Length, Sha256 = Convert.ToHexString(hash) };
    }
}
