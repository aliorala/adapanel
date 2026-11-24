using System.Reflection;
using System.Text;
using AdaPanel.Core.Dtos.Php;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Php;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class PhpIniPublicationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledTemporaryWriteLeavesOriginalBytesAndNoTemporaryFile(bool cancelled)
    {
        var root = Path.Combine(Path.GetTempPath(), "adapanel-ini-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var path = Path.Combine(root, "php.ini");
        var original = Encoding.UTF8.GetBytes("[PHP]\r\nmemory_limit=128M\r\n");
        await File.WriteAllBytesAsync(path, original);
        using var cts = new CancellationTokenSource();
        try
        {
            using var lease = await PhpIniFile.EnterAsync(path, default);
            await Assert.ThrowsAnyAsync<Exception>(() => PhpIniFile.PublishBytesAsync(path, Encoding.UTF8.GetBytes("changed"), cts.Token,
                stream => new InterruptedWrite(stream, cancelled ? cts : null)));
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
            Assert.Equal(new[] { path }, Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task GlobalSettingsAndIsolationRemovalWaitForTheSameFileAndKeepBothChanges()
    {
        using var f = new Fixture();
        await File.WriteAllLinesAsync(f.Ini, PhpIniSiteIsolation.Upsert(["[PHP]", "memory_limit=128M"], "fixture.test", f.Root));
        using var held = await PhpIniFile.EnterAsync(f.Ini, default);
        var global = f.Service.UpdateGlobalPhpSettingsAsync(new() { MemoryLimit = "512M", PostMaxSize = "0" });
        await WaitForReferences(f.Ini, 2);
        var isolation = f.Service.EnsureSiteIsolationAsync("fixture.test", false);
        await WaitForReferences(f.Ini, 3);
        held.Dispose();
        await Task.WhenAll(global, isolation);
        var lines = await File.ReadAllLinesAsync(f.Ini);
        Assert.Contains("memory_limit = 512M", lines);
        Assert.DoesNotContain(lines, line => line.StartsWith("open_basedir", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailedRedisVerificationCannotRollBackANewerSettingsWrite()
    {
        using var f = new Fixture();
        await File.WriteAllLinesAsync(f.Ini, ["[PHP]", "memory_limit=128M", ";extension=redis"]);
        var source = Path.Combine(f.Root, "source.dll"); var dll = Path.Combine(f.Root, "redis.dll");
        await File.WriteAllBytesAsync(source, [1, 2]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activation = PhpService.ActivateRedisFilesAsync(f.Ini, source, dll, async _ => { entered.TrySetResult(); await release.Task; return false; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var global = f.Service.UpdateGlobalPhpSettingsAsync(new() { MemoryLimit = "512M", PostMaxSize = "0" });
        Exception? failure = null;
        try { failure = await Record.ExceptionAsync(async () => { await WaitForReferences(f.Ini, 2); Assert.False(global.IsCompleted); }); }
        finally { release.TrySetResult(); }
        Assert.False(await activation); await global;
        Assert.Null(failure);
        var lines = await File.ReadAllLinesAsync(f.Ini);
        Assert.Contains("memory_limit = 512M", lines); Assert.DoesNotContain("extension=redis", lines);
    }

    private static async Task WaitForReferences(string path, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (PhpIniFile.ReferencesForPath(path) < count) await Task.Delay(5, timeout.Token);
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "adapanel-ini-concurrency-" + Guid.NewGuid().ToString("N"));
        internal string Ini => Path.Combine(Root, "php.ini");
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly AdaPanelDbContext db;
        internal readonly PhpService Service;
        internal Fixture()
        {
            Directory.CreateDirectory(Root); connection.Open();
            db = new(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options); db.Database.EnsureCreated();
            db.InstalledComponents.Add(new() { ComponentId = "php-8.3", InstallDir = Root, Status = AdaPanel.Core.Enums.ComponentInstallStatus.Installed }); db.SaveChanges();
            var gate = DispatchProxy.Create<IConcurrencyLock, PostgresDatabaseIdentityConsumersTests.StrictProxy>();
            ((PostgresDatabaseIdentityConsumersTests.StrictProxy)(object)gate).Handler = (_, _) => Task.CompletedTask;
            Service = new(db, null!, gate, null!, NullLogger<PhpService>.Instance) { RefreshPhpProcessesAsync = _ => Task.CompletedTask };
        }
        public void Dispose() { db.Dispose(); connection.Dispose(); Directory.Delete(Root, true); }
    }
    private sealed class InterruptedWrite(Stream inner, CancellationTokenSource? cancellation) : Stream
    {
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => inner.Length; public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush(); public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            await inner.WriteAsync(buffer[..Math.Min(2, buffer.Length)], token);
            if (cancellation is not null) { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            throw new IOException("fixture disk write failed");
        }
    }
}
