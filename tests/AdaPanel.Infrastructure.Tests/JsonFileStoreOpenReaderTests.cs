using System.Text.Json;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class JsonFileStoreOpenReaderTests
{
    [Fact]
    public async Task Publication_succeeds_while_a_reader_keeps_the_old_snapshot_open()
    {
        using var fixture = new Fixture();
        await fixture.Store.WriteAsync("state.json", new[] { 1 });
        await using var reader = new FileStream(fixture.Store.ResolvePath("state.json"),
            FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        await fixture.Store.WriteAsync("state.json", new[] { 2 });
        Assert.Equal(new[] { 2 }, await fixture.Store.ReadAsync("state.json", Array.Empty<int>()));
        Assert.Equal(new[] { 1 }, await JsonSerializer.DeserializeAsync<int[]>(reader));
    }

    [Fact]
    public async Task Concurrent_initial_writes_leave_complete_json_and_no_temporary_files()
    {
        using var fixture = new Fixture();
        await Task.WhenAll(Enumerable.Range(0, 32).Select(i => fixture.Store.WriteAsync("state.json", new[] { i, i })));
        var final = await fixture.Store.ReadAsync("state.json", Array.Empty<int>());
        Assert.Equal(2, final.Length);
        Assert.Equal(final[0], final[1]);
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.tmp*"));
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "ada-json-reader-" + Guid.NewGuid().ToString("N"));
        internal readonly JsonFileStore Store;
        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            Store = new(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Root }));
        }
        public void Dispose()
        {
            // Only test-owned, directly created files; no recursive traversal.
            foreach (var file in Directory.GetFiles(Root)) File.Delete(file);
            Directory.Delete(Root);
        }
    }
}
