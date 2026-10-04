using System.IO.Compression;
using System.Security.Claims;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Infrastructure.Backup;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using F = AdaPanel.Infrastructure.Tests.SubscriptionBackupMeasurementFixture;

namespace AdaPanel.Infrastructure.Tests;

public sealed partial class ScopedBackupExtractionAccessTests
{
    [Theory]
    [InlineData("Customer", "site")]
    [InlineData("Reseller", "site")]
    [InlineData("Administrator", "site")]
    [InlineData("Customer", "subscription")]
    [InlineData("Reseller", "subscription")]
    public async Task SelectedDownloadDeliversOnlyTheRequestedBytesAndDeletesItsPrivateFile(string role, string type)
    {
        using var fixture = await Fixture.Create(role, type);
        var source = await File.ReadAllBytesAsync(fixture.Record.Path);
        var index = await File.ReadAllBytesAsync(fixture.Backup.Store.ResolvePath("config/backups.json"));
        var result = Assert.IsType<FileStreamResult>(await fixture.Backups.DownloadItemsAsync(fixture.Selection(), default));
        var stream = Assert.IsType<FileStream>(result.FileStream);
        var path = stream.Name;
        try
        {
            Assert.Equal("application/zip", result.ContentType);
            Assert.EndsWith("-selected.zip", result.FileDownloadName, StringComparison.Ordinal);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var entry = Assert.Single(zip.Entries);
            Assert.Equal(fixture.SelectedEntry, entry.FullName);
            using var reader = new StreamReader(entry.Open());
            Assert.Contains("mysql:private_ledger", await reader.ReadToEndAsync());
        }
        finally { await stream.DisposeAsync(); }
        Assert.False(File.Exists(path));
        Assert.Equal(source, await File.ReadAllBytesAsync(fixture.Record.Path));
        Assert.Equal(index, await File.ReadAllBytesAsync(fixture.Backup.Store.ResolvePath("config/backups.json")));
        Assert.Empty(Directory.EnumerateFiles(fixture.Backup.Root, "mysql-private_ledger.sql", SearchOption.AllDirectories));
        Assert.DoesNotContain(await fixture.Db.AuditLogs.AsNoTracking().ToArrayAsync(), row => row.Action == "backup.items_restored");
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("overlap")]
    [InlineData("slashes")]
    [InlineData("case")]
    public async Task DirectoryAndNormalizedSelectionsAreCompleteAndNotDuplicated(string selection)
    {
        using var fixture = await Fixture.Create("Customer", "site");
        var items = selection switch
        {
            "directory" => new[] { "databases/" },
            "overlap" => new[] { "databases/", fixture.SelectedEntry, fixture.SelectedEntry },
            "slashes" => new[] { fixture.SelectedEntry.Replace('/', '\\') },
            _ => new[] { fixture.SelectedEntry.ToUpperInvariant() }
        };
        var result = Assert.IsType<FileStreamResult>(await fixture.Backups.DownloadItemsAsync(fixture.Selection(items), default));
        await using var stream = result.FileStream;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        Assert.Equal(fixture.SelectedEntry, Assert.Single(zip.Entries).FullName);
    }

    [Fact]
    public async Task AnEmptySelectedDirectoryIsAnActualEntryRatherThanAnEmptyFalseSuccess()
    {
        using var fixture = await Fixture.Create("Customer", "site");
        using (var zip = ZipFile.Open(fixture.Record.Path, ZipArchiveMode.Update)) zip.CreateEntry("files/empty/");
        fixture.Record.ContentHash = await BackupContentHash.ComputeAsync(fixture.Record.Path, default);
        await fixture.Backup.Store.WriteAsync("config/backups.json", new[] { fixture.Record });
        var result = Assert.IsType<FileStreamResult>(await fixture.Backups.DownloadItemsAsync(fixture.Selection("files/empty/"), default));
        await using var stream = result.FileStream;
        using var selected = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        Assert.Equal("files/empty/", Assert.Single(selected.Entries).FullName);
    }

    [Fact]
    public async Task ActualMvcStreamingFailureDisposesThePrivateDownloadHandle()
    {
        using var fixture = await Fixture.Create("Customer", "site");
        var result = Assert.IsType<FileStreamResult>(await fixture.Backups.DownloadItemsAsync(fixture.Selection(), default));
        var stream = Assert.IsType<FileStream>(result.FileStream);
        var path = stream.Name;
        var http = fixture.Backups.HttpContext;
        http.Request.Method = "POST";
        http.Response.Body = new FailedResponseBody();
        var context = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var executor = new FileStreamResultExecutor(NullLoggerFactory.Instance);
        await Assert.ThrowsAsync<IOException>(() => executor.ExecuteAsync(context, result));
        Assert.False(stream.CanRead);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("partial")]
    [InlineData("empty")]
    public async Task UnmatchedSelectionsDoNotProduceAnEmptyOrPartialSuccess(string selection)
    {
        using var fixture = await Fixture.Create("Customer", "site");
        var request = fixture.Selection();
        request.Items = selection switch
        {
            "empty" => [], "partial" => [fixture.SelectedEntry, "not-present.txt"], _ => ["missing/"]
        };
        var before = SelectedTemporaryFiles();
        var error = await Assert.ThrowsAsync<AppException>(() => fixture.Backups.DownloadItemsAsync(request, default));
        Assert.Equal(selection == "empty" ? "backup.items_required" : "backup.items_not_found", error.MessageKey);
        Assert.Equal(before, SelectedTemporaryFiles());
    }

    [Theory]
    [InlineData("../private.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:\\private.txt")]
    [InlineData("files/../private.txt")]
    [InlineData("files//private.txt")]
    public async Task SelectionCannotIntroduceUnsafeArchivePaths(string path)
    {
        using var fixture = await Fixture.Create("Customer", "site");
        var error = await Assert.ThrowsAsync<AppException>(() => fixture.Backups.DownloadItemsAsync(fixture.Selection(path), default));
        Assert.Equal("backup.invalid_entry_path", error.MessageKey);
    }

    [Theory]
    [InlineData("traversal")]
    [InlineData("duplicate")]
    public async Task SelectedSourceEntriesMustAlsoBeSafeAndUnambiguous(string defect)
    {
        using var fixture = await Fixture.Create("Customer", "site");
        using (var zip = ZipFile.Open(fixture.Record.Path, ZipArchiveMode.Update))
        using (var writer = new StreamWriter(zip.CreateEntry(defect == "traversal"
            ? "databases/../private.txt" : fixture.SelectedEntry.ToUpperInvariant()).Open()))
            writer.Write("unexpected content");
        fixture.Record.ContentHash = await BackupContentHash.ComputeAsync(fixture.Record.Path, default);
        await fixture.Backup.Store.WriteAsync("config/backups.json", new[] { fixture.Record });
        var error = await Assert.ThrowsAsync<AppException>(() => fixture.Backups.DownloadItemsAsync(fixture.Selection("databases/"), default));
        Assert.Equal("backup.invalid_entry_path", error.MessageKey);
    }

    [Theory]
    [InlineData("correct")]
    [InlineData("wrong")]
    [InlineData("missing")]
    public async Task EncryptedSelectionUsesTheBodyPasswordWithoutPublishingPlaintextArtifacts(string password)
    {
        using var fixture = await Fixture.Create("Customer", "site");
        var encrypted = fixture.Record.Path + ".enc";
        await BackupArchiveCipher.EncryptFileAsync(fixture.Record.Path, encrypted, "Selection!Password42");
        fixture.Record.Path = encrypted;
        fixture.Record.Encrypted = true;
        fixture.Record.ContentHash = await BackupContentHash.ComputeAsync(encrypted, default);
        await fixture.Backup.Store.WriteAsync("config/backups.json", new[] { fixture.Record });
        var request = fixture.Selection();
        request.EncryptionPassword = password == "correct" ? "Selection!Password42" : password == "wrong" ? "Wrong!Password" : "";
        var before = SelectedTemporaryFiles();
        if (password == "correct")
        {
            var response = Assert.IsType<FileStreamResult>(await fixture.Backups.DownloadItemsAsync(request, default));
            await using (var stream = response.FileStream)
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
            using (var reader = new StreamReader(Assert.Single(zip.Entries).Open()))
                Assert.Contains("mysql:private_ledger", await reader.ReadToEndAsync());
        }
        else
        {
            var error = await Assert.ThrowsAsync<AppException>(() => fixture.Backups.DownloadItemsAsync(request, default));
            Assert.Equal(password == "wrong" ? "backup.decrypt_wrong_password" : "backup.password_required", error.MessageKey);
        }
        Assert.Equal(before, SelectedTemporaryFiles());
        Assert.Empty(Directory.EnumerateFiles(fixture.Backup.Root, "mysql-private_ledger.sql", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("password")]
    [InlineData("logout")]
    [InlineData("scope")]
    [InlineData("cancel")]
    public async Task LastResponseBoundaryRejectsChangedAuthorityAndDisposesTheCompletedZip(string change)
    {
        using var fixture = await Fixture.Create("Customer", "site");
        using var cancellation = new CancellationTokenSource();
        string? temporaryPath = null;
        fixture.AfterDownload(async result =>
        {
            temporaryPath = Assert.IsType<FileStream>(result.Content).Name;
            if (change == "password") await fixture.Db.Users.Where(user => user.Id == fixture.Actor)
                .ExecuteUpdateAsync(update => update.SetProperty(user => user.PasswordHash, "changed"));
            else if (change == "logout") await fixture.Sessions.RevokeAsync(fixture.SessionId);
            else if (change == "scope") await fixture.Backup.Scopes.SetScopeAsync(F.Owner, [], 0, 0);
            else cancellation.Cancel();
        });
        var error = await Record.ExceptionAsync(() => fixture.Backups.DownloadItemsAsync(fixture.Selection(), cancellation.Token));
        if (change == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.True(Assert.IsType<AppException>(error).HttpStatusCode is 401 or 403);
        Assert.NotNull(temporaryPath);
        Assert.False(File.Exists(temporaryPath));
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("cancel")]
    public async Task AFailureAfterZipPreparationCleansTheStagingFileAndSourceLease(string failure)
    {
        using var fixture = await Fixture.Create("Customer", "site");
        using var cancellation = new CancellationTokenSource();
        var before = SelectedTemporaryFiles();
        var calls = 0;
        var error = await Record.ExceptionAsync(() => fixture.Backup.Service.DownloadItemsAsync(fixture.Selection(), (_, token) =>
        {
            if (++calls == 2)
            {
                Assert.Single(SelectedTemporaryFiles().Except(before));
                if (failure == "failure") throw new IOException("Fixture final callback failure");
                cancellation.Cancel();
            }
            return Task.CompletedTask;
        }, cancellation.Token));
        if (failure == "failure") Assert.IsType<IOException>(error);
        else Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.Equal(2, calls);
        Assert.Equal(before, SelectedTemporaryFiles());
        Assert.Equal(0, BackupSourceLifecycle.CountForStore(fixture.Backup.Store.ResolvePath("config/backups.json")));
    }

    [Theory]
    [InlineData("password")]
    [InlineData("scope")]
    [InlineData("cancel")]
    [InlineData("unchanged")]
    public async Task SourceWaitRevalidatesAuthorityAndSnapshotsTheSelection(string change)
    {
        using var fixture = await Fixture.Create("Customer", "site");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        var index = fixture.Backup.Store.ResolvePath("config/backups.json");
        var lease = await BackupSourceLifecycle.EnterAsync(index, fixture.Record.Type, fixture.Record.Source, deadline.Token);
        var request = fixture.Selection();
        var pending = fixture.Backups.DownloadItemsAsync(request, cancellation.Token);
        try
        {
            while (!pending.IsCompleted && BackupSourceLifecycle.ReferencesForSource(index, fixture.Record.Type, fixture.Record.Source) < 2)
                await Task.Delay(1, deadline.Token);
            Assert.False(pending.IsCompleted);
            if (change == "password") await fixture.Db.Users.Where(user => user.Id == fixture.Actor)
                .ExecuteUpdateAsync(update => update.SetProperty(user => user.PasswordHash, "changed"));
            else if (change == "scope") await fixture.Backup.Scopes.SetScopeAsync(F.Owner, [], 0, 0);
            else if (change == "cancel") cancellation.Cancel();
            request.Items[0] = "missing.txt";
            request.EncryptionPassword = "another draft";
        }
        finally { lease.Dispose(); }
        if (change == "unchanged")
        {
            var response = Assert.IsType<FileStreamResult>(await pending.WaitAsync(deadline.Token));
            await using var stream = response.FileStream;
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            Assert.Equal(fixture.SelectedEntry, Assert.Single(zip.Entries).FullName);
        }
        else if (change == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        else Assert.True((await Assert.ThrowsAsync<AppException>(() => pending)).HttpStatusCode is 401 or 403);
    }

    [Theory]
    [InlineData("Customer")]
    [InlineData("Reseller")]
    public async Task SuspendedReadOnlyAndApiTokenRulesPermitOnlyTheExactDownloadPost(string role)
    {
        using var fixture = await Fixture.Create(role, "site");
        await fixture.Backup.Scopes.SuspendAsync(fixture.Actor, "manual");
        var user = fixture.Backups.User;
        await SubscriptionSuspension.EnsureAllowedAsync(fixture.Backup.Scopes, user, "POST", "/api/v1/backup/download-items", default);
        var tokenPrincipal = new ClaimsPrincipal(new ClaimsIdentity(user.Claims.Where(claim =>
            claim.Type is ClaimTypes.NameIdentifier or ClaimTypes.Role), PanelTokenAuthenticationHandler.SchemeName));
        PanelTokenCapabilities.EnsureAllowed(tokenPrincipal, "POST", "/api/v1/backup/download-items");
        fixture.Backups.HttpContext.User = tokenPrincipal;
        var response = Assert.IsType<FileStreamResult>(await fixture.Backups.DownloadItemsAsync(fixture.Selection(), default));
        await response.FileStream.DisposeAsync();
        foreach (var (method, path) in new[]
        {
            ("POST", "/api/v1/backup/restore-items"), ("POST", "/api/v1/backup/download-items/extra"),
            ("DELETE", "/api/v1/backup/download-items"), ("PUT", "/api/v1/backup/download-items")
        })
        {
            await Assert.ThrowsAsync<AppException>(() => SubscriptionSuspension.EnsureAllowedAsync(fixture.Backup.Scopes, user, method, path, default));
            Assert.Throws<AppException>(() => PanelTokenCapabilities.EnsureAllowed(tokenPrincipal, method, path));
        }
        Assert.True(PanelAuthentication.IsStateChanging("POST")); // CSRF policy remains unchanged.
    }

    private static string[] SelectedTemporaryFiles() => Directory.GetFiles(Path.GetTempPath(), "adabk-selected-*.zip").Order(StringComparer.Ordinal).ToArray();

    private sealed class FailedResponseBody : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("Fixture HTTP response failed.");
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromException(new IOException("Fixture HTTP response failed."));
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("Fixture HTTP response failed."));
    }
}
