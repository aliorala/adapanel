using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Xunit;
namespace AdaPanel.Infrastructure.Tests;
public sealed class PanelPreferencesConcurrencyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-preferences-race-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public async Task General_save_preserves_a_hostname_published_after_its_initial_read()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root });
        var store = new JsonFileStore(options);
        await store.WriteAsync("config/panel-preferences.json", new PanelPreferencesDto { PanelHostName = "old.example.test", ServerAddress = "panel.example.test", PreferredHttpsPort = 8888 });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publishHost = store.UpdateAsync("config/panel-preferences.json", new PanelPreferencesDto(), current =>
        { current.PanelHostName = "new.example.test"; return current; },
            async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); }, default);
        var marker = Path.Combine(root, "new-sites");
        Task<PanelPreferencesDto>? saving = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var request = new UpdatePanelPreferencesRequest
            {
                PanelName = "Fixture", DefaultLanguage = "en", ThemeMode = "light", UiDensity = "compact",
                DefaultSiteRoot = marker, DefaultBackupRoot = Path.Combine(root, "backups"),
                ServerAddress = "panel.example.test", PreferredHttpsPort = 9443, LoginLockoutMinutes = 15,
                DefaultRuntimeProfile = "auto"
            };
            saving = new PanelPreferencesService(store, options).UpdateAsync(request);
            // Directory creation follows the initial GetAsync and precedes publication.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!Directory.Exists(marker)) await Task.Delay(10, timeout.Token);
            Assert.False(saving.IsCompleted);
        }
        finally { release.TrySetResult(); await publishHost; if (saving is not null) await saving; }
        var result = await saving!;
        Assert.Equal("new.example.test", result.PanelHostName);
        Assert.Equal("Fixture", result.PanelName);
        Assert.Equal(9443, result.PreferredHttpsPort);
        var persisted = await new PanelPreferencesService(store, options).GetAsync();
        Assert.Equal("new.example.test", persisted.PanelHostName);
        Assert.Equal("Fixture", persisted.PanelName);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Settings_failure_preserves_both_documents_and_unrelated_configuration(bool failPreferences)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root });
        var initial = new JsonFileStore(options);
        const string preferences = "config/panel-preferences.json";
        const string runtime = "config/runtime-settings.json";
        await initial.WriteAsync(preferences, new PanelPreferencesDto { PanelName = "Previous", PreferredHttpsPort = 8890 });
        var original = System.Text.Json.Nodes.JsonNode.Parse("""{"Other":{"Keep":9},"Panel":{"PreferredHttpsPort":8890,"Custom":"keep"}}""")!;
        await initial.WriteAsync(runtime, original);
        var store = new JsonFileStore(options) { AtomicReplaceOverride = (source, target) =>
        {
            if (target == initial.ResolvePath(failPreferences ? preferences : runtime))
                throw new UnauthorizedAccessException("Controlled save failure");
            File.Replace(source, target, null);
        }};
        var request = new UpdatePanelPreferencesRequest { PanelName = "New", PreferredHttpsPort = 9443,
            DefaultSiteRoot = Path.Combine(root, "sites"), DefaultBackupRoot = Path.Combine(root, "backups") };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new PanelPreferencesService(store, options).UpdateAsync(request));
        var saved = await initial.ReadAsync(preferences, new PanelPreferencesDto());
        Assert.Equal("Previous", saved.PanelName);
        var actual = await initial.ReadAsync(runtime, new System.Text.Json.Nodes.JsonObject());
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(original, actual));
        await new PanelPreferencesService(initial, options).UpdateAsync(request);
        actual = await initial.ReadAsync(runtime, new System.Text.Json.Nodes.JsonObject());
        Assert.Equal(9443, (int)actual["Panel"]!["PreferredHttpsPort"]!);
        Assert.Equal("keep", (string?)actual["Panel"]!["Custom"]);
        Assert.Equal(9, (int)actual["Other"]!["Keep"]!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Commit_then_error_is_reconciled_from_disk_preserving_foreign_fields(bool preferencesCommitted)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root });
        var initial = new JsonFileStore(options);
        const string prefs = "config/panel-preferences.json", runtime = "config/runtime-settings.json";
        await initial.WriteAsync(prefs, new PanelPreferencesDto { PanelName = "Previous" });
        await initial.WriteAsync(runtime, new System.Text.Json.Nodes.JsonObject());
        var failNext = true;
        var store = new JsonFileStore(options) { AtomicReplaceOverride = (source, target) =>
        {
            File.Replace(source, target, null);
            if (!failNext || target != initial.ResolvePath(preferencesCommitted ? prefs : runtime)) return;
            failNext = false;
            var path = initial.ResolvePath(runtime);
            var edited = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            edited["IndependentEdit"] = 7;
            File.WriteAllText(path, edited.ToJsonString());
            throw new UnauthorizedAccessException("Controlled post-commit error");
        }};
        var request = new UpdatePanelPreferencesRequest { PanelName = "New", PreferredHttpsPort = 9443,
            DefaultSiteRoot = Path.Combine(root, "sites"), DefaultBackupRoot = Path.Combine(root, "backups") };
        var save = new PanelPreferencesService(store, options).UpdateAsync(request);
        if (preferencesCommitted) Assert.Equal("New", (await save).PanelName);
        else await Assert.ThrowsAsync<UnauthorizedAccessException>(() => save);
        var persisted = await initial.ReadAsync(runtime, new System.Text.Json.Nodes.JsonObject());
        Assert.Equal(7, (int)persisted["IndependentEdit"]!);
        Assert.Equal(preferencesCommitted ? 9443 : (int?)null, (int?)persisted["Panel"]?["PreferredHttpsPort"]);
        Assert.Equal(preferencesCommitted ? "New" : "Previous", (await initial.ReadAsync(prefs, new PanelPreferencesDto())).PanelName);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
