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
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
