using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.SiteBuilder;
using Microsoft.Extensions.Options;

namespace AdaPanel.Infrastructure.Migration.Packages;

/// <summary>Accepts only a manifest and its declared payloads; never executes or parses their contents.</summary>
public sealed partial class PortableMigrationPackageStore : IMigrationPackageStore
{
    private const string ManifestName = "manifest.json";
    private readonly string root;
    private readonly PortableMigrationPackageOptions limits;
    private readonly IIisSiteService? sites;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 64
    };

    public PortableMigrationPackageStore(IOptions<PanelRuntimeOptions> runtime, IOptions<PortableMigrationPackageOptions> options,
        IIisSiteService? sites = null)
    {
        this.sites = sites;
        limits = options.Value;
        if (limits.MaxManifestBytes is <= 0 or > 4 * 1024 * 1024 || limits.MaxItems is <= 0 or > 100_000
            || limits.MaxUploadBytes <= 0 || limits.MaxPayloadBytes <= 0 || limits.MaxTotalUncompressedBytes <= 0
            || limits.MaxCompressionRatio <= 0) throw new ArgumentException("migration.package.limits_invalid");
        root = Path.GetFullPath(Path.Combine(Environment.ExpandEnvironmentVariables(runtime.Value.DataRoot), "migration-packages"));
        // Production awaits native inventory before any filesystem creation.
        if (sites is null) PortableMigrationStorage.CreatePrivateDirectory(root, limits.PublicWebRoots);
    }

    public async Task<PortableMigrationPackage> UploadAsync(Stream zip, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(zip);
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureRootAsync(cancellationToken);
        var id = Guid.NewGuid(); // The untrusted source PackageId is never a filesystem address.
        var staging = Path.Combine(root, "staging-" + id.ToString("N"));
        var destination = PackagePath(id);
        Guard(staging);
        CreatePrivateDirectory(staging);
        try
        {
            var upload = Path.Combine(staging, "upload.zip");
            await using (var output = new FileStream(upload, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await CopyBoundedAsync(zip, output, limits.MaxUploadBytes, cancellationToken);
            var payloadRoot = Path.Combine(staging, "files");
            Directory.CreateDirectory(payloadRoot);
            PortableMigrationManifest manifest;
            await using (var input = new FileStream(upload, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await PreflightZipAsync(input, cancellationToken);
                using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
                if (archive.Entries.Count > limits.MaxItems + 1) throw Error("item_limit");
                var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                long declaredTotal = 0;
                foreach (var entry in archive.Entries)
                {
                    ValidatePath(entry.FullName);
                    var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
                    if (unixType != 0 && unixType != 0x8000 || (entry.ExternalAttributes & (int)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                        throw Error("link_or_directory_entry");
                    if (!entries.TryAdd(entry.FullName, entry)) throw Error("duplicate_entry");
                    var max = entry.FullName == ManifestName ? limits.MaxManifestBytes : limits.MaxPayloadBytes;
                    if (entry.Length > max || entry.Length < 0 || entry.CompressedLength < 0) throw Error("entry_size_limit");
                    if (entry.Length > 0 && (entry.CompressedLength == 0 || (double)entry.Length / entry.CompressedLength > limits.MaxCompressionRatio))
                        throw Error("compression_ratio");
                    if (entry.Length > limits.MaxTotalUncompressedBytes - declaredTotal) throw Error("total_size_limit");
                    declaredTotal += entry.Length;
                }
                if (!entries.TryGetValue(ManifestName, out var manifestEntry) || manifestEntry.FullName != ManifestName) throw Error("manifest_missing");
                await using var manifestStream = manifestEntry.Open();
                using var buffer = new MemoryStream();
                await CopyBoundedAsync(manifestStream, buffer, limits.MaxManifestBytes, cancellationToken);
                if (buffer.Length != manifestEntry.Length) throw Error("entry_size_mismatch");
                manifest = DeserializeManifest(buffer.ToArray());
                ValidateManifest(manifest);
                var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ManifestName };
                foreach (var payload in manifest.Payloads)
                {
                    if (!expected.Add(payload.Path)) throw Error("duplicate_payload");
                    if (!entries.TryGetValue(payload.Path, out var entry) || entry.FullName != payload.Path) throw Error("payload_missing");
                    if (payload.Size != entry.Length) throw Error("payload_size_mismatch");
                    var path = Resolve(payloadRoot, payload.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    Guard(path);
                    await using var source = entry.Open();
                    await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    var (size, hash) = await CopyBoundedAsync(source, target, Math.Min(payload.Size, limits.MaxPayloadBytes), cancellationToken);
                    if (size != payload.Size || !hash.Equals(payload.Sha256, StringComparison.OrdinalIgnoreCase)) throw Error("payload_hash_or_size_mismatch");
                }
                if (entries.Count != expected.Count || entries.Keys.Any(path => !expected.Contains(path))) throw Error("undeclared_entry");
            }
            // Publish only validated contents; the uploaded ZIP is never retained or modified.
            File.Delete(upload);
            await File.WriteAllBytesAsync(Path.Combine(staging, ManifestName), JsonSerializer.SerializeToUtf8Bytes(manifest, Json), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Guard(staging);
            Guard(destination);
            Directory.Move(staging, destination);
            return new PortableMigrationPackage(id, manifest);
        }
        finally
        {
            // A failed upload owns only its random staging directory.
            if (Directory.Exists(staging)) DeleteOwnedDirectory(staging);
        }
    }

    public async Task<PortableMigrationManifest> ReadManifestAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await EnsureRootAsync(cancellationToken);
        await using var gate = await LockAsync(id, cancellationToken);
        return await ReadManifestCoreAsync(id, cancellationToken);
    }

    public async Task<Stream> OpenPayloadAsync(Guid id, string payloadPath, CancellationToken cancellationToken = default)
    {
        await EnsureRootAsync(cancellationToken);
        ValidatePath(payloadPath);
        var gate = await LockAsync(id, cancellationToken);
        FileStream? stream = null;
        try
        {
            var manifest = await ReadManifestCoreAsync(id, cancellationToken);
            var payload = manifest.Payloads.SingleOrDefault(p => p.Path == payloadPath) ?? throw Error("payload_not_declared");
            var path = Resolve(Path.Combine(PackagePath(id), "files"), payloadPath);
            Guard(path);
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length != payload.Size) throw Error("payload_size_mismatch");
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            if (!hash.Equals(payload.Sha256, StringComparison.OrdinalIgnoreCase)) throw Error("payload_hash_mismatch");
            stream.Position = 0;
            return new LockedPayloadStream(stream, gate);
        }
        catch
        {
            if (stream is not null) await stream.DisposeAsync();
            await gate.DisposeAsync();
            throw;
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await EnsureRootAsync(cancellationToken);
        await using var gate = await LockAsync(id, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var path = PackagePath(id);
        Guard(path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("migration.package.not_found");
        DeleteOwnedDirectory(path);
    }

    private async Task<PortableMigrationManifest> ReadManifestCoreAsync(Guid id, CancellationToken token)
    {
        var path = Resolve(PackagePath(id), ManifestName);
        Guard(path);
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var buffer = new MemoryStream();
        await CopyBoundedAsync(source, buffer, limits.MaxManifestBytes, token);
        var manifest = DeserializeManifest(buffer.ToArray());
        ValidateManifest(manifest);
        return manifest;
    }

    private PortableMigrationManifest DeserializeManifest(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw Error("manifest_invalid");
            foreach (var name in new[] { "version", "packageId", "provider", "createdAtUtc", "sourceName", "complete", "issues", "sites", "databases", "mailDomains", "dnsZones", "tasks", "payloads" })
                if (!document.RootElement.TryGetProperty(name, out _)) throw Error("manifest_field_missing");
            ValidateJson(document.RootElement);
            return JsonSerializer.Deserialize<PortableMigrationManifest>(bytes, Json) ?? throw Error("manifest_null");
        }
        catch (JsonException error) { throw new InvalidDataException("migration.package.manifest_invalid", error); }
    }

    private static void ValidateJson(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Error("duplicate_json_property");
                ValidateJson(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) ValidateJson(item);
    }

    private async Task<FileStream> LockAsync(Guid id, CancellationToken token)
    {
        if (id == Guid.Empty) throw Error("id_invalid");
        var package = PackagePath(id);
        Guard(package);
        if (!Directory.Exists(package)) throw new DirectoryNotFoundException("migration.package.not_found");
        var path = Path.Combine(root, id.ToString("N") + ".lock");
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Guard(path);
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when (error.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021))
            { await Task.Delay(50, token); }
        }
    }

    private async Task EnsureRootAsync(CancellationToken token)
        => PortableMigrationStorage.CreatePrivateDirectory(root, await MigrationStoragePolicy.PublicRootsAsync(sites, limits.PublicWebRoots, token));

    private string PackagePath(Guid id)
    {
        if (id == Guid.Empty) throw Error("id_invalid");
        return Path.Combine(root, id.ToString("N"));
    }
    private static string Resolve(string directory, string relative)
    {
        ValidatePath(relative);
        var fullPath = Path.GetFullPath(Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw Error("path_invalid");
        Guard(fullPath);
        return fullPath;
    }
    private static void Guard(string path) => PortableMigrationStorage.Guard(path);
    private static void CreatePrivateDirectory(string path) => PortableMigrationStorage.CreatePrivateDirectory(path);
    private static void DeleteOwnedDirectory(string path) => PortableMigrationStorage.DeleteOwnedDirectory(path);
    private static async Task<(long Size, string Hash)> CopyBoundedAsync(Stream source, Stream target, long limit, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var read = await source.ReadAsync(buffer, token);
            if (read == 0) break;
            if (read > limit - total) throw Error("stream_size_limit");
            total += read;
            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), token);
        }
        token.ThrowIfCancellationRequested();
        return (total, Convert.ToHexString(hash.GetHashAndReset()));
    }
    private static InvalidDataException Error(string code) => new("migration.package." + code);
}
