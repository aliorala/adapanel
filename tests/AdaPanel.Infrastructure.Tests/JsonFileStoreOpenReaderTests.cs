using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
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

    [Fact]
    public async Task Publication_retries_until_a_non_delete_sharing_reader_closes()
    {
        using var fixture = new Fixture();
        await fixture.Store.WriteAsync("state.json", new[] { 1 });
        using var reader = new FileStream(fixture.Store.ResolvePath("state.json"),
            FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var publication = fixture.Store.WriteAsync("state.json", new[] { 2 });
        // WriteAsync must reach its retry delay while the incompatible handle is held.
        await Task.Delay(200);
        Assert.False(publication.IsCompleted);
        Assert.Equal(new[] { 1 }, await fixture.Store.ReadAsync("state.json", Array.Empty<int>()));
        reader.Dispose();
        await publication;
        Assert.Equal(new[] { 2 }, await fixture.Store.ReadAsync("state.json", Array.Empty<int>()));
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.tmp*"));
    }

    [Fact]
    public async Task Permanent_contention_preserves_original_and_cleans_temp_and_releases_gate()
    {
        using var fixture = new Fixture();
        await fixture.Store.WriteAsync("state.json", new[] { 1 });
        using var reader = new FileStream(fixture.Store.ResolvePath("state.json"),
            FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var error = await Assert.ThrowsAsync<IOException>(() => fixture.Store.WriteAsync("state.json", new[] { 2 }));
        Assert.Equal(unchecked((int)0x80070020), error.HResult);
        Assert.Equal(new[] { 1 }, await fixture.Store.ReadAsync("state.json", Array.Empty<int>()));
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.tmp*"));
        reader.Dispose();
        await fixture.Store.WriteAsync("state.json", new[] { 3 });
        Assert.Equal(new[] { 3 }, await fixture.Store.ReadAsync("state.json", Array.Empty<int>()));
    }

    [Fact]
    public async Task Cancellation_during_retry_preserves_original_and_cleans_temp_and_releases_gate()
    {
        using var fixture = new Fixture();
        await fixture.Store.WriteAsync("state.json", new[] { 1 });
        using var reader = new FileStream(fixture.Store.ResolvePath("state.json"),
            FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var cancellation = new CancellationTokenSource();
        var publication = fixture.Store.WriteAsync("state.json", new[] { 2 }, cancellation.Token);
        await Task.Delay(100);
        Assert.False(publication.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publication);
        Assert.Equal(new[] { 1 }, await fixture.Store.ReadAsync("state.json", Array.Empty<int>()));
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.tmp*"));
        reader.Dispose();
        await fixture.Store.WriteAsync("state.json", new[] { 3 });
    }

    [Fact]
    public async Task Replacement_preserves_a_private_protected_destination_Dacl()
    {
        using var fixture = new Fixture();
        await fixture.Store.WriteAsync("state.json", new[] { 1 });
        var file = new FileInfo(fixture.Store.ResolvePath("state.json"));
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
            FileSystemRights.FullControl, AccessControlType.Allow));
        file.SetAccessControl(security);
        var before = file.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        await fixture.Store.WriteAsync("state.json", new[] { 2 });
        Assert.Equal(before, new FileInfo(file.FullName).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
    }

    [Theory]
    [InlineData(32, 6)]
    [InlineData(33, 6)]
    [InlineData(1175, 6)]
    [InlineData(5, 1)]
    public async Task Only_known_intact_name_errors_are_retried_and_attempts_are_bounded(int code, int expectedAttempts)
    {
        using var fixture = new Fixture();
        var original = Path.Combine(fixture.Root, "state.json");
        var temporary = Path.Combine(fixture.Root, "replacement.tmp");
        File.WriteAllText(original, "old");
        File.WriteAllText(temporary, "new");
        var attempts = 0;
        var failure = new IOException("controlled native failure", unchecked((int)0x80070000) | code);
        var error = await Assert.ThrowsAsync<IOException>(() => JsonFileStore.ReplaceWithRetryAsync(
            temporary, original, null, default, (_, _) => { attempts++; throw failure; }));
        Assert.Same(failure, error);
        Assert.Equal(expectedAttempts, attempts);
        Assert.Equal("old", File.ReadAllText(original));
        Assert.Equal("new", File.ReadAllText(temporary));
    }

    [Fact]
    public async Task Retry_revalidates_authority_before_publishing()
    {
        using var fixture = new Fixture();
        var original = Path.Combine(fixture.Root, "state.json");
        var temporary = Path.Combine(fixture.Root, "replacement.tmp");
        File.WriteAllText(original, "old");
        File.WriteAllText(temporary, "new");
        var attempts = 0;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => JsonFileStore.ReplaceWithRetryAsync(
            temporary, original, _ => throw new UnauthorizedAccessException("authority revoked"), default,
            (_, _) => { attempts++; throw new IOException("locked", unchecked((int)0x80070497)); }));
        Assert.Equal(1, attempts);
        Assert.Equal("old", File.ReadAllText(original));
    }

    [Fact]
    public async Task Transient_1175_reuses_the_same_complete_replacement_and_then_publishes()
    {
        using var fixture = new Fixture();
        var original = Path.Combine(fixture.Root, "state.json");
        var temporary = Path.Combine(fixture.Root, "replacement.tmp");
        File.WriteAllText(original, "old");
        File.WriteAllText(temporary, "new");
        var attempts = 0;
        await JsonFileStore.ReplaceWithRetryAsync(temporary, original, null, default, (source, destination) =>
        {
            Assert.Equal("old", File.ReadAllText(destination));
            Assert.Equal("new", File.ReadAllText(source));
            if (++attempts == 1) throw new IOException("controlled 1175", unchecked((int)0x80070497));
            File.Replace(source, destination, null);
        });
        Assert.Equal(2, attempts);
        Assert.Equal("new", File.ReadAllText(original));
        Assert.False(File.Exists(temporary));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Missing_source_or_destination_prevents_retry_even_for_1175(bool removeSource)
    {
        using var fixture = new Fixture();
        var original = Path.Combine(fixture.Root, "state.json");
        var temporary = Path.Combine(fixture.Root, "replacement.tmp");
        File.WriteAllText(original, "old");
        File.WriteAllText(temporary, "new");
        var attempts = 0;
        await Assert.ThrowsAsync<IOException>(() => JsonFileStore.ReplaceWithRetryAsync(
            temporary, original, null, default, (source, destination) =>
            {
                attempts++;
                File.Delete(removeSource ? source : destination);
                throw new IOException("controlled 1175", unchecked((int)0x80070497));
            }));
        Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData(1176)]
    [InlineData(1177)]
    public async Task Partial_replace_failure_preserves_the_complete_recovery_file_with_the_original_private_Dacl(int code)
    {
        var attempts = 0;
        string? recoveryPath = null;
        var failure = new IOException("controlled partial replacement", unchecked((int)0x80070000) | code);
        string? expectedDacl = null;
        using var fixture = new Fixture((source, destination) =>
        {
            attempts++;
            recoveryPath = source;
            Assert.Equal(expectedDacl, AccessPolicy(source));
            if (code == 1176) File.Delete(destination);
            else File.Move(destination, destination + ".replaced");
            throw failure;
        });
        await fixture.Store.WriteAsync("state.json", new[] { "old-secret" });
        var original = fixture.Store.ResolvePath("state.json");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
            FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(original).SetAccessControl(security);
        expectedDacl = AccessPolicy(original);

        var error = await Assert.ThrowsAsync<IOException>(() => fixture.Store.WriteAsync("state.json", new[] { "new-secret" }));

        Assert.Same(failure, error);
        Assert.Equal(1, attempts);
        Assert.False(File.Exists(original));
        Assert.NotNull(recoveryPath);
        Assert.Equal(recoveryPath, Assert.Single(Directory.GetFiles(fixture.Root, "*.tmp*")));
        Assert.Equal(new[] { "new-secret" }, JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(recoveryPath)));
        Assert.Equal(expectedDacl, AccessPolicy(recoveryPath));
        if (code == 1177)
            Assert.Equal(new[] { "old-secret" }, JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(original + ".replaced")));
        else
            Assert.False(File.Exists(original + ".replaced"));
    }

    private static string AccessPolicy(string path)
    {
        var security = new FileInfo(path).GetAccessControl();
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        // Windows may reset the auto-inheritance history bit on creation. Compare the
        // actual ACEs and inheritance protection; both determine who can read recovery data.
        descriptor.SetFlags(descriptor.ControlFlags & ~ControlFlags.DiscretionaryAclAutoInherited);
        return descriptor.GetSddlForm(AccessControlSections.Access);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "ada-json-reader-" + Guid.NewGuid().ToString("N"));
        internal readonly JsonFileStore Store;
        internal Fixture(Action<string, string>? replace = null)
        {
            Directory.CreateDirectory(Root);
            Store = new(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Root }))
                { AtomicReplaceOverride = replace };
        }
        public void Dispose()
        {
            // Only test-owned, directly created files; no recursive traversal.
            foreach (var file in Directory.GetFiles(Root)) File.Delete(file);
            Directory.Delete(Root);
        }
    }
}
