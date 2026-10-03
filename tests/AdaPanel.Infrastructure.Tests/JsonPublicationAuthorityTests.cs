using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class JsonPublicationAuthorityTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task AuthorityRevokedWhileWaitingForTheFileLockCannotPublish()
    {
        using var f = new Fixture();
        await f.Store.WriteAsync("state.json", new[] { "original" });
        var entered = Signal(); var release = Signal(); var authorized = true;
        var first = f.Store.UpdateAsync("state.json", Array.Empty<string>(), rows => [.. rows, "first"],
            async _ => { entered.TrySetResult(); await release.Task.WaitAsync(Deadline); }, default);
        await entered.Task.WaitAsync(Deadline);
        var second = f.Store.UpdateAsync("state.json", Array.Empty<string>(), rows => [.. rows, "forbidden"],
            _ => authorized ? Task.CompletedTask : Task.FromException(new UnauthorizedAccessException()), default);
        Assert.False(second.IsCompleted);
        authorized = false;
        release.TrySetResult();
        await first.WaitAsync(Deadline);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => second.WaitAsync(Deadline));
        Assert.Equal(new[] { "original", "first" }, await f.Store.ReadAsync("state.json", Array.Empty<string>()));
        Assert.Empty(Directory.GetFiles(f.Root, "*.tmp*"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalPublicationWaitPreservesTheOldDocumentOnRejectionOrCancellation(bool cancel)
    {
        using var f = new Fixture();
        await f.Store.WriteAsync("state.json", new[] { "original" });
        var original = await File.ReadAllBytesAsync(f.Store.ResolvePath("state.json"));
        var entered = Signal(); var release = Signal();
        using var cancellation = new CancellationTokenSource();
        var action = f.Store.UpdateAsync("state.json", Array.Empty<string>(), _ => ["replacement"],
            async _ =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(Deadline);
                if (!cancel) throw new UnauthorizedAccessException();
            }, cancellation.Token);
        await entered.Task.WaitAsync(Deadline);
        Assert.Equal(original, await File.ReadAllBytesAsync(f.Store.ResolvePath("state.json")));
        Assert.Contains("replacement", await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(f.Root, "*.tmp*"))));
        if (cancel) cancellation.Cancel();
        release.TrySetResult();
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action.WaitAsync(Deadline));
        else await Assert.ThrowsAsync<UnauthorizedAccessException>(() => action.WaitAsync(Deadline));
        Assert.Equal(original, await File.ReadAllBytesAsync(f.Store.ResolvePath("state.json")));
        Assert.Empty(Directory.GetFiles(f.Root, "*.tmp*"));
        await f.Store.UpdateAsync("state.json", Array.Empty<string>(), rows => [.. rows, "next"]);
        Assert.Equal(new[] { "original", "next" }, await f.Store.ReadAsync("state.json", Array.Empty<string>()));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "AdaPanelJsonPublication-" + Guid.NewGuid().ToString("N"));
        internal JsonFileStore Store { get; }
        internal Fixture() => Store = new(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Root }));
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }
}
