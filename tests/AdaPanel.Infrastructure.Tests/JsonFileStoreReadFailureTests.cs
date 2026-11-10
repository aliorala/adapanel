using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class JsonFileStoreReadFailureTests
{
    [Theory]
    [InlineData("missing.json")]
    [InlineData("missing-parent/state.json")]
    public async Task Only_missing_sources_return_the_supplied_fallback(string path)
    {
        using var fixture = new Fixture();
        var fallback = new[] { 9 };
        Assert.Same(fallback, await fixture.Store.ReadAsync(path, fallback));
        Assert.Empty(Directory.GetFileSystemEntries(fixture.Root));
    }

    [Theory]
    [InlineData("[1,2]", false)]
    [InlineData("null", true)]
    public async Task Valid_json_and_json_null_preserve_existing_semantics(string json, bool returnsFallback)
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Path, json);
        var fallback = new[] { 9 };
        var value = await fixture.Store.ReadAsync("state.json", fallback);
        if (returnsFallback) Assert.Same(fallback, value);
        else Assert.Equal(new[] { 1, 2 }, value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{broken")]
    [InlineData("[1,2]")]
    public async Task Malformed_or_incompatible_json_stops_updates_without_mutation(string json)
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Path, json);
        var mutations = 0;
        await Assert.ThrowsAsync<JsonException>(() => fixture.Store.ReadAsync("state.json", 9));
        await Assert.ThrowsAsync<JsonException>(() => fixture.Store.UpdateAsync("state.json", 9, value => { mutations++; return value + 1; }));
        Assert.Equal(0, mutations);
        Assert.Equal(json, File.ReadAllText(fixture.Path));
        Assert.Equal(new[] { fixture.Path }, Directory.GetFiles(fixture.Root));
    }

    [Fact]
    public async Task A_directory_is_a_read_failure_and_never_reaches_the_update_mutator()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Path);
        var marker = System.IO.Path.Combine(fixture.Path, "marker.txt");
        File.WriteAllText(marker, "original");
        var mutations = 0;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Store.ReadAsync("state.json", 9));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Store.UpdateAsync("state.json", 9, value => { mutations++; return value + 1; }));
        Assert.Equal(0, mutations);
        Assert.Equal("original", File.ReadAllText(marker));
        Assert.Empty(Directory.GetFiles(fixture.Root));
        File.Delete(marker);
        Directory.Delete(fixture.Path);
    }

    [Fact]
    public async Task Sharing_failure_propagates_and_preserves_the_original_without_mutation()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Path, "[1]");
        var mutations = 0;
        using (var exclusive = new FileStream(fixture.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => fixture.Store.ReadAsync("state.json", Array.Empty<int>()));
            await Assert.ThrowsAsync<IOException>(() => fixture.Store.UpdateAsync("state.json", Array.Empty<int>(), value => { mutations++; return new[] { 2 }; }));
        }
        Assert.Equal(0, mutations);
        Assert.Equal("[1]", File.ReadAllText(fixture.Path));
        Assert.Equal(new[] { fixture.Path }, Directory.GetFiles(fixture.Root));
        // A failed read also releases the UpdateAsync path lock.
        Assert.Equal(new[] { 3 }, await fixture.Store.UpdateAsync("state.json", Array.Empty<int>(), _ => new[] { 3 }));
    }

    [Fact]
    public async Task Denied_read_access_propagates_and_preserves_the_original_without_mutation()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Path, "[1]");
        var file = new FileInfo(fixture.Path);
        var originalDescriptor = file.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorBinaryForm();
        var restricted = file.GetAccessControl(AccessControlSections.Access);
        restricted.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
            FileSystemRights.ReadData, AccessControlType.Deny));
        var mutations = 0;
        try
        {
            // Only this fixture-owned file is restricted; always restore its DACL.
            file.SetAccessControl(restricted);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Store.ReadAsync("state.json", Array.Empty<int>()));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Store.UpdateAsync("state.json", Array.Empty<int>(), value => { mutations++; return new[] { 2 }; }));
        }
        finally
        {
            var restored = new FileSecurity();
            restored.SetSecurityDescriptorBinaryForm(originalDescriptor, AccessControlSections.Access);
            file.SetAccessControl(restored);
        }
        Assert.Equal(0, mutations);
        Assert.Equal("[1]", File.ReadAllText(fixture.Path));
        Assert.Equal(new[] { fixture.Path }, Directory.GetFiles(fixture.Root));
    }

    [Theory]
    [InlineData("missing.json", false)]
    [InlineData("missing-parent/state.json", false)]
    [InlineData("state.json", true)]
    public async Task Precancelled_reads_and_updates_cancel_even_when_the_source_is_missing(string path, bool exists)
    {
        using var fixture = new Fixture();
        if (exists) File.WriteAllText(fixture.Path, "[1]");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var mutations = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Store.ReadAsync(path, Array.Empty<int>(), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Store.UpdateAsync(path, Array.Empty<int>(), value => { mutations++; return new[] { 2 }; }, cancellation.Token));
        Assert.Equal(0, mutations);
        if (exists) Assert.Equal("[1]", File.ReadAllText(fixture.Path));
        else Assert.Empty(Directory.GetFileSystemEntries(fixture.Root));
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ada-json-read-failure-" + Guid.NewGuid().ToString("N"));
        internal readonly JsonFileStore Store;
        internal string Path => System.IO.Path.Combine(Root, "state.json");
        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            Store = new(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Root }));
        }
        public void Dispose()
        {
            foreach (var file in Directory.GetFiles(Root)) File.Delete(file);
            // Clean the one known nested fixture even if an assertion failed.
            if (Directory.Exists(Path))
            {
                File.Delete(System.IO.Path.Combine(Path, "marker.txt"));
                Directory.Delete(Path);
            }
            Directory.Delete(Root);
        }
    }
}
