using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Infrastructure.Backup;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

// Reuse the real producer/controller/SQLite fixture and its non-parallel collection.
// An empty whole-file hash is the supported legacy format, not a forged modern hash.
public sealed partial class ScopedBackupExtractionAccessTests
{
    [Theory]
    [InlineData("Customer", "crc")]
    [InlineData("Customer", "short-length")]
    [InlineData("Customer", "long-length")]
    [InlineData("Administrator", "crc")]
    [InlineData("Administrator", "short-length")]
    [InlineData("Administrator", "long-length")]
    public async Task SelectedIntegrityRejectsCorruptLegacyEntriesBeforeReturningANewZip(string role, string corruption)
    {
        using var owned = await IntegrityFixture.Create(role);
        var fixture = owned.Value;
        await CorruptSelectedEntry(fixture, fixture.SelectedEntry, corruption);
        await AssertSelectionIntegrityRejected(fixture, fixture.Selection());
    }

    [Fact]
    public async Task SelectedIntegrityAlsoProtectsOwnedSubscriptionArchives()
    {
        using var owned = await IntegrityFixture.Create("Customer", "subscription");
        var fixture = owned.Value;
        await CorruptSelectedEntry(fixture, fixture.SelectedEntry, "crc");
        await AssertSelectionIntegrityRejected(fixture, fixture.Selection());
    }

    [Fact]
    public async Task SelectedIntegrityDoesNotReturnAnEarlierGoodEntryWhenALaterSelectedEntryIsCorrupt()
    {
        using var owned = await IntegrityFixture.Create();
        var fixture = owned.Value;
        const string later = "databases/later.sql";
        using (var zip = ZipFile.Open(fixture.Record.Path, ZipArchiveMode.Update))
        using (var writer = new StreamWriter(zip.CreateEntry(later).Open()))
            writer.Write("later private database contents");
        await CorruptSelectedEntry(fixture, later, "crc");
        using (var source = ZipFile.OpenRead(fixture.Record.Path))
            Assert.True(source.Entries.ToList().FindIndex(entry => entry.FullName == fixture.SelectedEntry) <
                source.Entries.ToList().FindIndex(entry => entry.FullName == later));
        await AssertSelectionIntegrityRejected(fixture, fixture.Selection(fixture.SelectedEntry, later));
    }

    [Fact]
    public async Task SelectedIntegrityChecksEntriesMatchedByADirectorySelection()
    {
        using var owned = await IntegrityFixture.Create();
        var fixture = owned.Value;
        await CorruptSelectedEntry(fixture, fixture.SelectedEntry, "crc");
        var directory = fixture.SelectedEntry[..(fixture.SelectedEntry.LastIndexOf('/') + 1)];
        await AssertSelectionIntegrityRejected(fixture, fixture.Selection(directory));
    }

    [Fact]
    public async Task SelectedIntegrityChecksLegacyZipAfterAuthenticatedDecryption()
    {
        using var owned = await IntegrityFixture.Create();
        var fixture = owned.Value;
        await CorruptSelectedEntry(fixture, fixture.SelectedEntry, "crc");
        var encrypted = fixture.Record.Path + ".enc";
        await BackupArchiveCipher.EncryptFileAsync(fixture.Record.Path, encrypted, "Fixture!Selected42");
        fixture.Record.Path = encrypted;
        fixture.Record.Encrypted = true;
        await PersistLegacySelectionRecord(fixture);
        var request = fixture.Selection();
        request.EncryptionPassword = "Fixture!Selected42";
        await AssertSelectionIntegrityRejected(fixture, request);
    }

    [Fact]
    public async Task SelectedIntegrityDoesNotReadAnUnselectedCorruptEntry()
    {
        using var owned = await IntegrityFixture.Create();
        var fixture = owned.Value;
        string other;
        using (var zip = ZipFile.OpenRead(fixture.Record.Path))
            other = zip.Entries.First(entry => entry.FullName != fixture.SelectedEntry && entry.Length > 0).FullName;
        await CorruptSelectedEntry(fixture, other, "crc");
        await AssertSelectedDatabaseBytes(fixture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectedIntegrityKeepsHealthyLegacyAndStampedArchivesReadable(bool stamped)
    {
        using var owned = await IntegrityFixture.Create();
        var fixture = owned.Value;
        if (!stamped) await PersistLegacySelectionRecord(fixture);
        else Assert.StartsWith("sha256:", fixture.Record.ContentHash, StringComparison.Ordinal);
        await AssertSelectedDatabaseBytes(fixture);
    }

    [Fact]
    public async Task SelectedIntegrityStillRejectsAModernWholeArchiveHashMismatchFirst()
    {
        using var owned = await IntegrityFixture.Create();
        var fixture = owned.Value;
        await CorruptSelectedEntry(fixture, fixture.SelectedEntry, "crc", legacy: false);
        await AssertSelectionIntegrityRejected(fixture, fixture.Selection(), "backup.integrity_mismatch", 409);
    }

    [Fact]
    public async Task SelectedIntegrityPreservesValidZeroLengthFiles()
    {
        using var owned = await IntegrityFixture.Create();
        var fixture = owned.Value;
        const string empty = "files/empty.txt";
        using (var zip = ZipFile.Open(fixture.Record.Path, ZipArchiveMode.Update)) zip.CreateEntry(empty);
        await PersistLegacySelectionRecord(fixture);
        var response = Assert.IsType<FileStreamResult>(await fixture.Backups.DownloadItemsAsync(fixture.Selection(empty), default));
        await using var stream = response.FileStream;
        using var selected = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var entry = Assert.Single(selected.Entries);
        Assert.Equal(empty, entry.FullName);
        Assert.Equal(0, entry.Length);
        using var contents = entry.Open();
        Assert.Equal(-1, contents.ReadByte());
    }

    [Fact]
    public async Task SelectedIntegrityCancellationDoesNotPublishOrLeakAnArchive()
    {
        using var owned = await IntegrityFixture.Create();
        var fixture = owned.Value;
        await PersistLegacySelectionRecord(fixture);
        var temporary = IntegrityTemporaryFiles();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Backups.DownloadItemsAsync(fixture.Selection(), cancellation.Token));
        Assert.Equal(temporary, IntegrityTemporaryFiles());
        Assert.Equal(0, BackupSourceLifecycle.CountForStore(fixture.Backup.Store.ResolvePath("config/backups.json")));
    }

    private static async Task AssertSelectedDatabaseBytes(Fixture fixture)
    {
        var before = IntegrityTemporaryFiles();
        var response = Assert.IsType<FileStreamResult>(await fixture.Backups.DownloadItemsAsync(fixture.Selection(), default));
        await using (var stream = response.FileStream)
        using (var selected = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
        {
            var entry = Assert.Single(selected.Entries);
            Assert.Equal(fixture.SelectedEntry, entry.FullName);
            using var reader = new StreamReader(entry.Open());
            Assert.Equal("-- measured mysql:private_ledger", await reader.ReadToEndAsync());
        }
        Assert.Equal(before, IntegrityTemporaryFiles());
    }

    private static async Task AssertSelectionIntegrityRejected(Fixture fixture, DownloadBackupItemsRequest request,
        string key = "backup.archive_corrupt", int status = 400)
    {
        var source = await File.ReadAllBytesAsync(fixture.Record.Path);
        var indexPath = fixture.Backup.Store.ResolvePath("config/backups.json");
        var index = await File.ReadAllBytesAsync(indexPath);
        var auditCount = await fixture.Db.AuditLogs.CountAsync();
        var temporary = IntegrityTemporaryFiles();
        var error = await Record.ExceptionAsync(async () =>
        {
            var result = await fixture.Backups.DownloadItemsAsync(request, default);
            // Dispose an unexpected successful baseline response as well; the failing
            // assertion must not leave plaintext staging files behind.
            if (result is FileStreamResult file) await file.FileStream.DisposeAsync();
        });
        Assert.Equal(source, await File.ReadAllBytesAsync(fixture.Record.Path));
        Assert.Equal(index, await File.ReadAllBytesAsync(indexPath));
        Assert.Equal(auditCount, await fixture.Db.AuditLogs.CountAsync());
        Assert.Equal(temporary, IntegrityTemporaryFiles());
        Assert.Equal(0, BackupSourceLifecycle.CountForStore(indexPath));
        var failure = Assert.IsType<AppException>(error);
        Assert.Equal(key, failure.MessageKey);
        Assert.Equal(status, failure.HttpStatusCode);
    }

    private static async Task PersistLegacySelectionRecord(Fixture fixture)
    {
        fixture.Record.ContentHash = string.Empty;
        fixture.Record.Size = new FileInfo(fixture.Record.Path).Length;
        await fixture.Backup.Store.WriteAsync("config/backups.json", new[] { fixture.Record });
    }

    private static async Task CorruptSelectedEntry(Fixture fixture, string name, string corruption, bool legacy = true)
    {
        var bytes = await File.ReadAllBytesAsync(fixture.Record.Path);
        var central = -1;
        for (var offset = 0; offset <= bytes.Length - 46; offset++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4)) != 0x02014b50) continue;
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 28, 2));
            if (offset + 46 + nameLength <= bytes.Length && Encoding.UTF8.GetString(bytes, offset + 46, nameLength) == name)
            { central = offset; break; }
        }
        Assert.True(central >= 0, "The real producer archive must contain the requested entry.");
        var local = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(central + 42, 4)));
        Assert.Equal(0x04034b50u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(local, 4)));
        if (corruption == "crc")
        {
            var changed = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(central + 16, 4)) ^ 1u;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(central + 16, 4), changed);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(local + 14, 4), changed);
        }
        else
        {
            var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(central + 24, 4));
            Assert.True(length > 1 && length < uint.MaxValue);
            var changed = corruption == "short-length" ? length - 1 : length + 1;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(central + 24, 4), changed);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(local + 22, 4), changed);
        }
        await File.WriteAllBytesAsync(fixture.Record.Path, bytes);
        // The existing structural probe intentionally cannot detect these content faults.
        Assert.Equal(BackupIntegrity.Sound, BackupArchiveProbe.Measure(fixture.Record.Path, "zip").State);
        if (legacy) await PersistLegacySelectionRecord(fixture);
    }

    private static string[] IntegrityTemporaryFiles() => SelectedTemporaryFiles()
        .Concat(Directory.GetFiles(Path.GetTempPath(), "adabk-open-*.tmp"))
        .Order(StringComparer.Ordinal).ToArray();

    private sealed class IntegrityFixture(Fixture value) : IDisposable
    {
        internal Fixture Value { get; } = value;
        internal static async Task<IntegrityFixture> Create(string role = "Customer", string type = "site") =>
            new(await Fixture.Create(role, type));
        public void Dispose()
        {
            // The reused fixture's recursive cleanup is permitted only for its exact
            // unique direct child of TEMP. These tests never create reparse points.
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Value.Backup.Root));
            var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            Assert.Equal(temporary, Path.GetDirectoryName(root), ignoreCase: true);
            const string prefix = "adapanel-subscription-measurement-";
            var name = Path.GetFileName(root);
            Assert.StartsWith(prefix, name, StringComparison.Ordinal);
            Assert.True(Guid.TryParseExact(name[prefix.Length..], "N", out _));
            Assert.False(File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint));
            Value.Dispose();
        }
    }
}
