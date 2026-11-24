using System.IO.Compression;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class BackupSnapshotTests
{
    [Fact]
    public void NativeScriptEncoding_has_ASCII_first_command_without_NUL_or_BOM_and_no_embedded_paths()
    {
        var bytes = WindowsBackupSnapshotRuntime.BuildNativeScript([@"C:\", @"D:\"]);
        Assert.Equal((byte)'S', bytes[0]);
        Assert.Equal("SET CONTEXT VOLATILE NOWRITERS\r\nSET METADATA .\\metadata.cab\r\nADD VOLUME C:\\ ALIAS APVSS0\r\nADD VOLUME D:\\ ALIAS APVSS1\r\nCREATE\r\nEXEC .\\hold.cmd\r\nEXIT\r\n", System.Text.Encoding.ASCII.GetString(bytes));
        Assert.All(bytes, item => Assert.InRange(item, (byte)1, (byte)127));
        // Unicode user/temp/Windows directory names are passed as process/environment values,
        // never lossy-encoded into Diskshadow or CMD source.
        Assert.All(WindowsBackupSnapshotRuntime.NativeHelperCommand, character => Assert.InRange((int)character, 1, 127));
        Assert.Contains("%APVSS_POWERSHELL%", WindowsBackupSnapshotRuntime.NativeHelperCommand);
        Assert.Contains("%~dp0hold.ps1", WindowsBackupSnapshotRuntime.NativeHelperCommand);
    }

    [Theory]
    [InlineData(@"C:\site")]
    [InlineData("C:\\; CREATE")]
    [InlineData(@"\\server\share")]
    public void NativeScriptEncoding_refuses_non_volume_input(string input)
        => Assert.Throws<ArgumentException>(() => WindowsBackupSnapshotRuntime.BuildNativeScript([input]));

    [Fact]
    public async Task Lease_cleanup_removes_fixed_native_metadata_without_touching_adjacent_files()
    {
        var parent = Path.Combine(Path.GetTempPath(), "adapanel-cleanup-test-" + Guid.NewGuid().ToString("N"));
        var own = Path.Combine(parent, "AdaPanel-vss-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(own);
        var adjacent = Path.Combine(parent, "another-backup.cab");
        File.WriteAllText(adjacent, "preserve");
        File.WriteAllText(Path.Combine(own, "metadata.cab"), "native metadata");
        // Construct the same disposal lease with no native process: exercise cleanup only.
        var type = typeof(WindowsBackupSnapshotRuntime).GetNestedType("Lease", System.Reflection.BindingFlags.NonPublic)!;
        var lease = (IBackupSnapshotLease)Activator.CreateInstance(type,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
            binder: null, args: [own, Array.Empty<string>(), Array.Empty<string>()], culture: null)!;
        try
        {
            await lease.DisposeAsync();
            Assert.False(Directory.Exists(own));
            Assert.Equal("preserve", File.ReadAllText(adjacent));
        }
        finally
        {
            if (File.Exists(Path.Combine(own, "metadata.cab"))) File.Delete(Path.Combine(own, "metadata.cab"));
            if (Directory.Exists(own)) Directory.Delete(own, false);
            File.Delete(adjacent);
            Directory.Delete(parent, false);
        }
    }

    [Fact]
    public void Production_registration_refuses_missing_snapshot_runtime_before_constructing_service()
    {
        var services = new ServiceCollection();
        services.AddAdaPanelInfrastructure(new ConfigurationBuilder().Build());
        var descriptor = Assert.Single(services, service => service.ServiceType == typeof(AdaPanel.Core.Interfaces.IBackupService));
        Assert.NotNull(descriptor.ImplementationFactory);
        using var missingRuntime = new ServiceCollection().BuildServiceProvider();
        var error = Assert.Throws<InvalidOperationException>(() => descriptor.ImplementationFactory!(missingRuntime));
        Assert.Contains(nameof(IBackupSnapshotRuntime), error.Message);
        Assert.Contains(services, item => item.ServiceType == typeof(IBackupSnapshotRuntime) && item.ImplementationType == typeof(WindowsBackupSnapshotRuntime));
    }

    [Fact]
    public async Task Locked_live_files_are_read_from_one_snapshot_and_lease_is_released()
    {
        using var area = new BackupSkippedFileTests.AtlamaAlani();
        foreach (var name in new[] { "jobs.db", "jobs.db-wal", "jobs.db-shm", "worker.lock" })
            area.SiteDosyasiUret(name, "snapshot-" + name);
        var copy = Path.Combine(area.Kok, "immutable");
        Directory.CreateDirectory(copy);
        foreach (var file in Directory.GetFiles(area.SiteKoku)) File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
        var runtime = new Runtime(new Lease(area.SiteKoku, copy));
        area.SiteDosyasiUret("jobs.db", "NEW LIVE DATA");
        using var locked = new FileStream(Path.Combine(area.SiteKoku, "worker.lock"), FileMode.Open, FileAccess.Read, FileShare.None);
        var record = await area.Servis(runtime).BackupSiteAsync(area.SiteIstegi());
        Assert.Equal("created", record.Status);
        Assert.Equal(1, runtime.Captures);
        Assert.True(runtime.Lease.Disposed);
        using var archive = ZipFile.OpenRead(record.Path);
        foreach (var name in new[] { "jobs.db", "jobs.db-wal", "jobs.db-shm", "worker.lock" })
        {
            using var reader = new StreamReader(Assert.IsType<ZipArchiveEntry>(archive.GetEntry("files/" + name)).Open());
            Assert.Equal("snapshot-" + name, await reader.ReadToEndAsync());
        }
        using var manifest = new StreamReader(archive.GetEntry("manifest.json")!.Open());
        Assert.Contains("volume-crash-consistent-nowriters", await manifest.ReadToEndAsync());
    }

    [Fact]
    public async Task Capture_failure_never_falls_back_to_live_copy_or_publishes_success()
    {
        using var area = new BackupSkippedFileTests.AtlamaAlani();
        area.SiteDosyasiUret("jobs.db", "live");
        await Assert.ThrowsAsync<IOException>(() => area.Servis(new FailingRuntime()).BackupSiteAsync(area.SiteIstegi()));
        Assert.Empty(await area.Servis().GetBackupsAsync());
    }

    [Fact]
    public async Task Panel_database_WAL_SHM_and_config_come_from_same_capture_released_before_tasks()
    {
        using var area = new BackupSkippedFileTests.AtlamaAlani();
        area.PanelVeritabaniUret();
        area.ConfigDosyasiUret("automation.json", "{\"snapshot\":true}");
        var db = area.DosyaDeposu.ResolvePath("data/adapanel.db");
        File.WriteAllText(db + "-wal", "committed-wal");
        File.WriteAllText(db + "-shm", "snapshot-shm");
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[] { Path.GetDirectoryName(db)!, area.DosyaDeposu.ResolvePath("config") })
        {
            var copy = Path.Combine(area.Kok, "immutable-" + Path.GetFileName(root));
            Directory.CreateDirectory(copy);
            foreach (var file in Directory.GetFiles(root)) File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
            map.Add(root, copy);
        }
        var runtime = new MultiRuntime(map);
        using var locked = new FileStream(db, FileMode.Open, FileAccess.Read, FileShare.None);
        var service = area.Servis(runtime);
        service.TaskExportRunner = (_, _) =>
        {
            Assert.True(runtime.Disposed);
            return Task.FromResult(new AdaPanel.Infrastructure.Support.CommandResult { StandardOutput = "[]" });
        };
        var record = await service.BackupPanelAsync(new BackupPanelRequest());
        Assert.Equal("created", record.Status);
        Assert.Equal(1, runtime.Captures);
        using var archive = ZipFile.OpenRead(record.Path);
        Assert.NotNull(archive.GetEntry("data/adapanel.db"));
        Assert.NotNull(archive.GetEntry("data/adapanel.db-shm"));
        using var wal = new StreamReader(archive.GetEntry("data/adapanel.db-wal")!.Open());
        Assert.Equal("committed-wal", await wal.ReadToEndAsync());
        Assert.NotNull(archive.GetEntry("config/automation.json"));
        Assert.Null(archive.GetEntry("keys"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lease_is_disposed_when_snapshot_scan_fails_or_is_cancelled(bool cancel)
    {
        using var area = new BackupSkippedFileTests.AtlamaAlani();
        using var cts = new CancellationTokenSource();
        var lease = new Lease(area.SiteKoku, area.SiteKoku)
        { Resolve = () => { if (cancel) { cts.Cancel(); throw new OperationCanceledException(cts.Token); } throw new IOException("snapshot lost"); } };
        var runtime = new Runtime(lease);
        await Assert.ThrowsAnyAsync<Exception>(() => area.Servis(runtime).BackupSiteAsync(area.SiteIstegi(), cts.Token));
        Assert.True(lease.Disposed);
        Assert.Empty(await area.Servis().GetBackupsAsync());
    }

    [Fact]
    public void Hash_detects_same_size_same_timestamp_WAL_change_and_deletion()
    {
        var before = new BackupFileIndex { Entries = [new() { Path = "files/jobs.db-wal", Size = 8, ModifiedUtcTicks = 1, Sha256 = "AA" }, new() { Path = "files/deleted", Sha256 = "CC" }] };
        var after = new BackupFileIndex { Entries = [new() { Path = "files/jobs.db-wal", Size = 8, ModifiedUtcTicks = 1, Sha256 = "BB" }] };
        var diff = BackupChainPlanner.Diff(before, after);
        Assert.Equal(["files/jobs.db-wal"], diff.Changed);
        Assert.Equal(["files/deleted"], diff.Deleted);
    }

    [Fact]
    public void Source_root_rejects_UNC_and_reparse_traversal()
    {
        Assert.Throws<IOException>(() => WindowsBackupSnapshotRuntime.ValidateRoot(@"\\localhost\share\site"));
        using var area = new BackupSkippedFileTests.AtlamaAlani();
        var link = Path.Combine(area.Kok, "link");
        // Junction creation needs no symlink privilege and changes only this test's directory.
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.Arguments = $"/d /s /c \"mklink /J \"{link}\" \"{area.SiteKoku}\"\"";
        using var process = System.Diagnostics.Process.Start(start)!; process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        try { Assert.Throws<IOException>(() => WindowsBackupSnapshotRuntime.ValidateRoot(link)); }
        finally { Directory.Delete(link); }
    }

    private sealed class FailingRuntime : IBackupSnapshotRuntime
    {
        public Task<IBackupSnapshotLease> CaptureAsync(IReadOnlyCollection<string> roots, CancellationToken cancellationToken) => throw new IOException("VSS unavailable");
    }
    private sealed class MultiRuntime(Dictionary<string, string> map) : IBackupSnapshotRuntime, IBackupSnapshotLease
    {
        public int Captures;
        public bool Disposed;
        public DateTime CapturedAtUtc => DateTime.UtcNow;
        public Task<IBackupSnapshotLease> CaptureAsync(IReadOnlyCollection<string> roots, CancellationToken cancellationToken)
        {
            Captures++;
            Assert.Equal(map.Keys.Order(), roots.Order());
            return Task.FromResult<IBackupSnapshotLease>(this);
        }
        public string ResolveRoot(string root) { Assert.False(Disposed); return map[root]; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class Runtime(Lease lease) : IBackupSnapshotRuntime
    {
        public Lease Lease => lease;
        public int Captures { get; private set; }
        public Task<IBackupSnapshotLease> CaptureAsync(IReadOnlyCollection<string> roots, CancellationToken cancellationToken)
        { Captures++; Assert.Contains(lease.Original, roots); return Task.FromResult<IBackupSnapshotLease>(lease); }
    }
    private sealed class Lease(string original, string snapshot) : IBackupSnapshotLease
    {
        public string Original => original;
        public Func<string>? Resolve { get; init; }
        public bool Disposed { get; private set; }
        public DateTime CapturedAtUtc => new(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc);
        public string ResolveRoot(string root) { Assert.False(Disposed); Assert.Equal(original, root); return Resolve?.Invoke() ?? snapshot; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
