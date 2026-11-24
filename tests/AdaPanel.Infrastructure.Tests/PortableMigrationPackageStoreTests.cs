using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Infrastructure.Migration.Packages;
using AdaPanel.Infrastructure.Options;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class PortableMigrationPackageStoreTests
{
    [Theory]
    [InlineData(PortableMigrationProvider.Adapanel)]
    [InlineData(PortableMigrationProvider.Cpanel)]
    [InlineData(PortableMigrationProvider.Plesk)]
    public async Task Complete_real_zip_roundtrips_all_resource_types_without_changing_source(PortableMigrationProvider provider)
    {
        using var f = new Fixture();
        var node = Manifest(provider);
        using var zip = Archive(node);
        var original = zip.ToArray();
        var package = await f.Store.UploadAsync(zip);
        Assert.NotEqual(package.Manifest.PackageId, package.Id);
        Assert.Equal(original, zip.ToArray());
        var read = await f.Store.ReadManifestAsync(package.Id);
        Assert.True(read.Complete);
        Assert.Equal(provider, read.Provider);
        Assert.Single(read.Sites); Assert.Single(read.Databases); Assert.Single(read.MailDomains);
        Assert.Single(read.DnsZones); Assert.Single(read.Tasks);
        await using (var content = await f.Store.OpenPayloadAsync(package.Id, "sites/s1/index.php"))
        using (var text = new StreamReader(content)) Assert.Equal("site", await text.ReadToEndAsync());
        var directory = Path.Combine(f.StoreRoot, package.Id.ToString("N"));
        var security = new DirectoryInfo(directory).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        Assert.DoesNotContain(security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)).Cast<FileSystemAccessRule>(),
            rule => rule.IdentityReference.Value is "S-1-1-0" or "S-1-5-11" or "S-1-5-32-545");
        await f.Store.DeleteAsync(package.Id);
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => f.Store.ReadManifestAsync(package.Id));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/absolute")]
    [InlineData("C:/absolute")]
    [InlineData("//server/share")]
    [InlineData("sites\\outside")]
    [InlineData("sites/a:stream")]
    [InlineData("sites/CON.txt")]
    [InlineData("sites/CON .txt")]
    [InlineData("sites/CONIN$")]
    [InlineData("sites/CONOUT$")]
    [InlineData("sites/com1")]
    [InlineData("sites/lpt¹.txt")]
    [InlineData("sites/trailing.")]
    [InlineData("sites/trailing ")]
    [InlineData("sites//empty")]
    public async Task Unsafe_zip_names_are_rejected_without_publication(string path)
    {
        using var f = new Fixture();
        using var zip = Archive(Manifest(), extraName: path);
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Store.UploadAsync(zip));
        Assert.Empty(Directory.GetDirectories(f.StoreRoot));
    }

    [Theory]
    [InlineData("missing_payload")]
    [InlineData("extra_payload")]
    [InlineData("duplicate_entry")]
    [InlineData("case_duplicate")]
    [InlineData("hash")]
    [InlineData("size")]
    [InlineData("duplicate_payload")]
    [InlineData("missing_manifest")]
    [InlineData("null_collection")]
    [InlineData("password")]
    [InlineData("provider")]
    [InlineData("source_id")]
    [InlineData("unknown_site")]
    [InlineData("incomplete_silent")]
    [InlineData("symlink")]
    [InlineData("reparse")]
    [InlineData("directory")]
    public async Task Invalid_archive_or_manifest_is_an_explicit_failure(string fault)
    {
        using var f = new Fixture();
        var node = Manifest();
        string? extra = null, omit = null;
        int? attributes = null;
        switch (fault)
        {
            case "missing_payload": omit = "db/d1.sql"; break;
            case "extra_payload": extra = "undeclared.txt"; break;
            case "duplicate_entry": extra = "db/d1.sql"; break;
            case "case_duplicate": extra = "DB/D1.SQL"; break;
            case "hash": node["payloads"]![0]!["sha256"] = new string('0',64); break;
            case "size": node["payloads"]![0]!["size"] = 99; break;
            case "duplicate_payload": node["payloads"]!.AsArray().Add(node["payloads"]![0]!.DeepClone()); break;
            case "missing_manifest": omit = "manifest.json"; break;
            case "null_collection": node["tasks"] = null; break;
            case "password": node["password"] = "must-never-be-accepted"; break;
            case "provider": node["provider"] = 0; break;
            case "source_id": node["packageId"] = Guid.Empty; break;
            case "unknown_site": node["databases"]![0]!["siteIds"] = new JsonArray("other"); break;
            case "incomplete_silent": node["complete"] = false; break;
            case "symlink": attributes = unchecked((int)0xA1FF0000); break;
            case "reparse": attributes = (int)FileAttributes.ReparsePoint; break;
            case "directory": extra = "empty/"; break;
        }
        using var zip = Archive(node, extra, omit, attributes);
        var original = zip.ToArray();
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Store.UploadAsync(zip));
        Assert.Equal(original, zip.ToArray());
        Assert.Empty(Directory.GetDirectories(f.StoreRoot));
    }

    [Theory]
    [InlineData("upload")]
    [InlineData("manifest")]
    [InlineData("payload")]
    [InlineData("total")]
    [InlineData("items")]
    [InlineData("ratio")]
    public async Task Configured_limits_are_enforced_before_publication(string limit)
    {
        var options = new PortableMigrationPackageOptions();
        switch (limit)
        {
            case "upload": options.MaxUploadBytes = 50; break;
            case "manifest": options.MaxManifestBytes = 20; break;
            case "payload": options.MaxPayloadBytes = 2; break;
            case "total": options.MaxTotalUncompressedBytes = 100; break;
            case "items": options.MaxItems = 2; break;
            case "ratio": options.MaxCompressionRatio = 1; break;
        }
        using var f = new Fixture(options);
        using var zip = Archive(Manifest());
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Store.UploadAsync(zip));
        Assert.Empty(Directory.GetDirectories(f.StoreRoot));
    }

    [Fact]
    public async Task Empty_site_and_explicitly_incomplete_source_retain_their_meaning()
    {
        using var f = new Fixture();
        var manifest = Manifest();
        manifest["sites"]![0]!["contentPath"] = "empty-site";
        manifest["sites"]![0]!["fileCount"] = 0;
        manifest["sites"]![0]!["emptyDirectories"] = new JsonArray("cache");
        manifest["complete"] = false;
        manifest["issues"]!.AsArray().Add(new JsonObject { ["code"] = "source.unavailable", ["resourceId"] = "unknown", ["detail"] = "Source inspection failed." });
        // An unrelated declared payload is allowed; absence of an empty site's contents is explicit.
        using var zip = Archive(manifest);
        var package = await f.Store.UploadAsync(zip);
        Assert.False(package.Manifest.Complete);
        Assert.Single(package.Manifest.Issues);
        Assert.Equal(0, package.Manifest.Sites[0].FileCount);
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Store.OpenPayloadAsync(package.Id, "../manifest.json"));
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Store.OpenPayloadAsync(package.Id, "not-declared"));
    }

    [Fact]
    public async Task Cancellation_during_nonseekable_upload_cleans_only_owned_staging()
    {
        using var f = new Fixture();
        using var first = Archive(Manifest());
        var original = await f.Store.UploadAsync(first);
        using var zip = Archive(Manifest());
        using var cts = new CancellationTokenSource();
        using var source = new CancellingStream(zip.ToArray(), cts);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Store.UploadAsync(source, cts.Token));
        Assert.Single(Directory.GetDirectories(f.StoreRoot));
        Assert.True((await f.Store.ReadManifestAsync(original.Id)).Complete);
    }

    [Fact]
    public async Task Payload_reader_holds_delete_lock_until_closed_and_modified_bytes_are_rejected()
    {
        using var f = new Fixture();
        using var zip = Archive(Manifest());
        var package = await f.Store.UploadAsync(zip);
        await using (var reader = await f.Store.OpenPayloadAsync(package.Id, "db/d1.sql"))
        {
            using var cts = new CancellationTokenSource(100);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Store.DeleteAsync(package.Id, cts.Token));
        }
        var stored = Path.Combine(f.StoreRoot, package.Id.ToString("N"), "files", "db", "d1.sql");
        File.WriteAllText(stored, "EVIL");
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Store.OpenPayloadAsync(package.Id, "db/d1.sql"));
        await f.Store.DeleteAsync(package.Id);
    }

    [Fact]
    public void Private_staging_rejects_configured_public_roots()
    {
        var root = Path.Combine(Path.GetTempPath(), "ada-package-public-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<InvalidDataException>(() => new PortableMigrationPackageStore(
            Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }), Microsoft.Extensions.Options.Options.Create(new PortableMigrationPackageOptions { PublicWebRoots = [root] })));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task Real_junction_in_stored_payload_path_is_never_followed()
    {
        using var f = new Fixture();
        using var zip = Archive(Manifest());
        var package = await f.Store.UploadAsync(zip);
        var db = Path.Combine(f.StoreRoot, package.Id.ToString("N"), "files", "db");
        File.Delete(Path.Combine(db, "d1.sql"));
        Directory.Delete(db);
        var outside = Path.Combine(f.Root, "outside");
        Directory.CreateDirectory(outside);
        var marker = Path.Combine(outside, "d1.sql");
        File.WriteAllText(marker, "outside-original");
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", db, outside }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        try
        {
            await Assert.ThrowsAsync<AdaPanel.Core.Common.AppException>(() => f.Store.OpenPayloadAsync(package.Id, "db/d1.sql"));
            await Assert.ThrowsAsync<AdaPanel.Core.Common.AppException>(() => f.Store.DeleteAsync(package.Id));
            Assert.Equal("outside-original", File.ReadAllText(marker));
        }
        finally
        {
            // Delete the known fixture junction itself, never its target.
            Directory.Delete(db);
        }
    }
    [Fact]
    public async Task Central_directory_entry_count_is_bounded_before_ziparchive_allocation()
    {
        using var f = new Fixture(new() { MaxItems = 50 });
        using var zip = Archive(Manifest());
        var bytes = zip.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 22 + 8), 60000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 22 + 10), 60000);
        using var damaged = new MemoryStream(bytes);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => f.Store.UploadAsync(damaged));
        Assert.Equal("migration.package.zip_metadata_limit", error.Message);
        Assert.Empty(Directory.GetDirectories(f.StoreRoot));
    }

    [Fact]
    public async Task Duplicate_json_property_cannot_silently_override_manifest_semantics()
    {
        using var f = new Fixture();
        var manifest = Manifest();
        var json = manifest.ToJsonString().Replace("\"complete\":true", "\"complete\":false,\"complete\":true");
        using var zip = Archive(manifest, manifestText: json);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => f.Store.UploadAsync(zip));
        Assert.Equal("migration.package.duplicate_json_property", error.Message);
        Assert.Empty(Directory.GetDirectories(f.StoreRoot));
    }
    private static readonly Dictionary<string, string> Files = new()
    {
        ["sites/s1/index.php"] = "site", ["db/d1.sql"] = "dump", ["mail/u1.mbox"] = "mail", ["tasks/task.ps1"] = "task"
    };
    private static JsonObject Manifest(PortableMigrationProvider provider = PortableMigrationProvider.Adapanel)
    {
        var manifest = new PortableMigrationManifest
        {
            PackageId = Guid.NewGuid(), Provider = provider, CreatedAtUtc = DateTimeOffset.UtcNow, SourceName = "test-source", Complete = true,
            Sites = [new() { Id = "s1", Name = "example.test", ContentPath = "sites/s1", FileCount = 1, SourcePhysicalPath = "/source/public", RuntimeProfile = "php", HostNames = ["example.test"] }],
            Databases = [new() { Id = "d1", Name = "db", Engine = "mariadb", SiteIds = ["s1"], DumpPath = "db/d1.sql", Format = "sql" }],
            MailDomains = [new() { Id = "m1", Name = "example.test", SiteIds = ["s1"], Accounts = [new() { Email = "user@example.test", ArchivePath = "mail/u1.mbox", ArchiveFormat = "mbox", EmailCount = 1, Active = true }], Aliases = [new() { Address = "alias@example.test", Destination = "user@example.test" }] }],
            DnsZones = [new() { Id = "z1", Name = "example.test", SiteIds = ["s1"], Records = [new() { Type = "A", Name = "@", Value = "192.0.2.1", Ttl = 3600 }] }],
            Tasks = [new() { Id = "t1", Name = "job", SiteIds = ["s1"], Kind = "script", Command = "pwsh", SourcePlatform = "windows", ScheduleType = "daily", ScheduleValue = "12:00", ArtifactPaths = ["tasks/task.ps1"] }],
            Payloads = Files.Select(p => new PortableMigrationPayload { Path = p.Key, Size = Encoding.UTF8.GetByteCount(p.Value), Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(p.Value))) }).ToList()
        };
        return JsonSerializer.SerializeToNode(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
    }
    private static MemoryStream Archive(JsonObject manifest, string? extraName = null, string? omit = null, int? attributes = null, string? manifestText = null)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string content, int? attrs = null)
            {
                var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
                if (attrs is not null) entry.ExternalAttributes = attrs.Value;
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
            if (omit != "manifest.json") Add("manifest.json", manifestText ?? manifest.ToJsonString());
            foreach (var item in Files)
                if (item.Key != omit) Add(item.Key, item.Value, attributes);
            if (extraName is not null) Add(extraName, "extra");
        }
        stream.Position = 0;
        return stream;
    }
    private sealed class CancellingStream(byte[] bytes, CancellationTokenSource cts) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = base.ReadAsync(buffer[..Math.Min(32, buffer.Length)], cancellationToken);
            cts.Cancel();
            return read;
        }
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "ada-package-test-" + Guid.NewGuid().ToString("N"));
        internal string StoreRoot => Path.Combine(Root, "migration-packages");
        internal readonly PortableMigrationPackageStore Store;
        internal Fixture(PortableMigrationPackageOptions? options = null)
        {
            Store = new(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Root }), Microsoft.Extensions.Options.Options.Create(options ?? new()));
        }
        public void Dispose() => PortableMigrationStorage.DeleteOwnedDirectory(Root);
    }
}
