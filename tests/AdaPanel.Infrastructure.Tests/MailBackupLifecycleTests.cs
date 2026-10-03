using System.IO.Compression;
using System.Reflection;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class MailBackupLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartiallyStoppedRuntimeIsRestartedWhenStopThrows(bool restore)
    {
        using var f = new Fixture();
        var original = new IOException("stop failed after ending task");
        f.Runtime.OnStop = _ => throw original;
        Assert.Same(original, await Assert.ThrowsAsync<IOException>(() => f.Run(restore)));
        Assert.Equal(1, f.Runtime.Starts);
        Assert.Equal("original", File.ReadAllText(f.LiveFile));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterStopRestartsWithIndependentTokenAndKeepsLiveData(bool restore)
    {
        using var f = new Fixture();
        using var cts = new CancellationTokenSource();
        f.Runtime.OnStop = _ => { cts.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Run(restore, cts.Token));
        Assert.Equal(1, f.Runtime.Starts);
        Assert.False(f.Runtime.StartWasCanceled);
        Assert.True(f.Runtime.StartWasBounded);
        Assert.Equal("original", File.ReadAllText(f.LiveFile));
        Assert.Empty(Directory.EnumerateFiles(f.BackupRoot, "*.zip", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartFailureCannotMaskOriginalStopFailure(bool restore)
    {
        using var f = new Fixture();
        var original = new IOException("original-stop-error");
        f.Runtime.OnStop = _ => throw original;
        f.Runtime.OnStart = _ => throw new InvalidOperationException("restart-error");
        var caught = await Assert.ThrowsAsync<IOException>(() => f.Run(restore));
        Assert.Same(original, caught);
        Assert.Equal(1, f.Runtime.Starts);
        Assert.True(caught.Data.Contains("backup.mail_service_start_failed"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulCopyPersistsRestartExceptionAsWarning(bool restore)
    {
        using var f = new Fixture();
        f.Runtime.OnStart = _ => throw new IOException("cannot launch scheduler");
        var result = await f.Run(restore);
        Assert.Contains(result.Warnings, w => w.MessageKey == "backup.mail_service_start_failed");
        Assert.Contains((await f.Service.GetBackupsAsync()).Single().Warnings,
            w => w.MessageKey == "backup.mail_service_start_failed");
        if (restore) Assert.Equal("restored_with_errors", result.Status);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("outside")]
    [InlineData("traversal")]
    [InlineData("duplicate")]
    [InlineData("crc")]
    [InlineData("directory-payload")]
    public async Task InvalidMailArchiveCannotMoveLiveDataOrTouchInstallation(string kind)
    {
        using var f = new Fixture(kind);
        var exception = await Record.ExceptionAsync(() => f.Run(true));
        Assert.NotNull(exception);
        Assert.Equal("original", File.ReadAllText(f.LiveFile));
        Assert.False(File.Exists(Path.Combine(f.InstallRoot, "stalwart.exe")));
        Assert.Equal(0, f.Runtime.Stops);
        Assert.Equal(0, f.Runtime.Starts);
    }

    [Fact]
    public async Task BackupCopyFailurePreservesOriginalExceptionAndRemovesPartialArchive()
    {
        using var f = new Fixture();
        using var locked = new FileStream(f.LiveFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        f.Runtime.OnStart = _ => throw new InvalidOperationException("restart-error");
        var error = await Assert.ThrowsAsync<IOException>(() => f.Run(false));
        Assert.Equal(1, f.Runtime.Starts);
        Assert.True(error.Data.Contains("backup.mail_service_start_failed"));
        Assert.Empty(Directory.EnumerateFiles(f.BackupRoot, "*.zip", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CancellationDuringRestartCannotLoseCommittedRestoreOutcome()
    {
        using var f = new Fixture();
        using var cts = new CancellationTokenSource();
        f.Runtime.OnStart = _ =>
        {
            cts.Cancel();
            return Task.FromResult<BackupRestoreWarningDto?>(new() { MessageKey = "backup.mail_service_start_failed", Target = "AdaStalwart", Detail = "Cannot start" });
        };
        var result = await f.Run(true, cts.Token);
        Assert.Equal("restored_with_errors", result.Status);
        Assert.Equal("restored", File.ReadAllText(f.LiveFile));
        var persisted = Assert.Single(await f.Service.GetBackupsAsync());
        Assert.Equal(result.Status, persisted.Status);
        Assert.Equal("original", File.ReadAllText(Path.Combine(persisted.PreservedPath, "store.txt")));
        Assert.Single(persisted.Warnings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlreadyCancelledRequestNeverTouchesRuntime(bool restore)
    {
        using var f = new Fixture();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Run(restore, cts.Token));
        Assert.Equal(0, f.Runtime.Stops);
        Assert.Equal(0, f.Runtime.Starts);
    }

    [Fact]
    public async Task RestoreReplacesDataAndPreservesPreviousDirectory()
    {
        using var f = new Fixture();
        var result = await f.Run(true);
        Assert.Equal("restored", result.Status);
        Assert.Equal("restored", File.ReadAllText(f.LiveFile));
        Assert.Equal("original", File.ReadAllText(Path.Combine(Assert.Single(Directory.GetDirectories(f.InstallRoot, "data.old-*")), "store.txt")));
        Assert.Equal(1, f.Runtime.Starts);
    }

    [Fact]
    public async Task ConcurrentInstancesCannotRestartMailInsideAnotherOfflineOperation()
    {
        using var f = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Runtime.OnStop = async _ => { entered.TrySetResult(); await release.Task; };
        var first = f.Run(false);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cts = new CancellationTokenSource();
        var second = f.NewService().BackupMailAsync(new BackupMailRequest(), cts.Token);
        var stopCount = 0;
        try
        {
            await Task.Delay(100);
            stopCount = f.Runtime.Stops;
            cts.Cancel();
        }
        finally { cts.Cancel(); release.TrySetResult(); }
        await first;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await second);
        Assert.Equal(1, stopCount);
        Assert.Equal(1, f.Runtime.Starts);
    }

    private sealed class Runtime(string root) : IMailBackupRuntime
    {
        public int Stops, Starts;
        public bool StartWasCanceled, StartWasBounded;
        public Func<CancellationToken, Task> OnStop = _ => Task.CompletedTask;
        public Func<CancellationToken, Task<BackupRestoreWarningDto?>> OnStart = _ => Task.FromResult<BackupRestoreWarningDto?>(null);
        public string GetInstallDirectory() => root;
        public async Task StopAsync(string directory, CancellationToken ct) { Interlocked.Increment(ref Stops); await OnStop(ct); }
        public async Task<BackupRestoreWarningDto?> StartAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Starts);
            StartWasCanceled = ct.IsCancellationRequested;
            StartWasBounded = ct.CanBeCanceled;
            ct.ThrowIfCancellationRequested();
            return await OnStart(ct);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"adapanel-mail-lifecycle-{Guid.NewGuid():N}");
        public string InstallRoot => Path.Combine(Root, "stalwart");
        public string LiveFile => Path.Combine(InstallRoot, "data", "store.txt");
        public string BackupRoot => Path.Combine(Root, "backups");
        public Runtime Runtime { get; }
        public JsonFileStore Store { get; }
        public BackupService Service { get; }
        public BackupRecordDto Archive { get; }
        public Fixture(string kind = "valid")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LiveFile)!);
            Directory.CreateDirectory(BackupRoot);
            File.WriteAllText(LiveFile, "original");
            Store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Path.Combine(Root, "panel") }));
            Runtime = new Runtime(InstallRoot);
            var zipPath = Path.Combine(Root, "restore.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                if (kind != "empty") Write(zip, "data/store.txt", "restored");
                if (kind == "outside") Write(zip, "stalwart.exe", "overwrite executable");
                if (kind == "traversal") Write(zip, "data/../../escaped.txt", "escape");
                if (kind == "duplicate") Write(zip, "data/store.txt", "second");
                if (kind == "directory-payload") Write(zip, "data/ignored/", "not a directory");
            }
            if (kind == "crc")
            {
                var bytes = File.ReadAllBytes(zipPath);
                var start = bytes.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes("restored"));
                Assert.True(start >= 0);
                bytes[start] = (byte)'X';
                File.WriteAllBytes(zipPath, bytes);
            }
            Archive = new BackupRecordDto { Id = "mail-original", Name = "mail.zip", Type = "mail", Source = "stalwart", Path = zipPath, Format = "zip", Status = "created", CreatedAtUtc = DateTime.UtcNow, Size = new FileInfo(zipPath).Length };
            Service = NewService();
        }
        public BackupService NewService()
        {
            var preferences = DispatchProxy.Create<IPanelPreferencesService, PostgresDatabaseIdentityConsumersTests.StrictProxy>();
            ((PostgresDatabaseIdentityConsumersTests.StrictProxy)(object)preferences).Handler = (method, _) => method.Name == "GetAsync"
                ? Task.FromResult(new PanelPreferencesDto { DefaultBackupRoot = BackupRoot }) : throw new NotSupportedException(method.Name);
            return new BackupService(null!, null!, null!, preferences, null!, Store, null!, mailBackupRuntime: Runtime);
        }
        public async Task<BackupRecordDto> Run(bool restore, CancellationToken ct = default)
        {
            if (!restore) return await Service.BackupMailAsync(new BackupMailRequest(), ct);
            await Store.WriteAsync("config/backups.json", new[] { Archive });
            return await Service.RestoreAsync(new RestoreBackupRequest { BackupId = Archive.Id }, ct);
        }
        private static void Write(ZipArchive zip, string name, string value) { using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.NoCompression).Open()); writer.Write(value); }
        public void Dispose()
        {
            Assert.StartsWith(Path.Combine(Path.GetTempPath(), "adapanel-mail-lifecycle-"), Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(Root, recursive: true);
        }
    }
}
