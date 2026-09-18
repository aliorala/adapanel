using AdaPanel.Infrastructure.Bootstrap;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class ComponentWebPublicationTests : IDisposable
{
    private readonly string area = Path.Combine(Path.GetTempPath(), "adapanel-publication-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(area, "component");
    private void Write(string path, string content)
    {
        var full = Path.Combine(Root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public async Task Adminer_update_publishes_new_code_and_keeps_access_gate_and_sessions()
    {
        Write("adminer.php", "old package");
        Write("webroot/index.php", "old package");
        Write("webroot/adapanel-health.php", "health marker");
        Write("webroot/.user.ini", "auto_prepend_file=adapanel-signon.php");
        Write("webroot/adapanel-signon.php", "access gate");
        Write("webroot/web.config", "custom IIS settings");
        Write("webroot/tmp/session", "session data");
        Write("webroot/adapanel-tokens/web.config", "deny access");
        var preserved = ComponentStatePreservation.Capture(Root,
            ComponentBootstrapper.DataDirectoriesToPreserve("adminer").Concat(ComponentBootstrapper.ConfigurationFilesToPreserve("adminer")));
        Directory.Delete(Root, true);
        Write("adminer.php", "new package");
        preserved.Restore();
        await ComponentWebPublication.RestoreAsync("adminer", Root, CancellationToken.None);
        Assert.Equal("new package", File.ReadAllText(Path.Combine(Root, "webroot/index.php")));
        Assert.Equal("access gate", File.ReadAllText(Path.Combine(Root, "webroot/adapanel-signon.php")));
        Assert.Equal("custom IIS settings", File.ReadAllText(Path.Combine(Root, "webroot/web.config")));
        Assert.Equal("session data", File.ReadAllText(Path.Combine(Root, "webroot/tmp/session")));
        Assert.Equal("deny access", File.ReadAllText(Path.Combine(Root, "webroot/adapanel-tokens/web.config")));
    }

    [Fact]
    public async Task Downloading_an_unpublished_tool_does_not_publish_it()
    {
        Write("adminer.php", "new package");
        await ComponentWebPublication.RestoreAsync("adminer", Root, CancellationToken.None);
        Assert.False(Directory.Exists(Path.Combine(Root, "webroot")));
    }

    [Fact]
    public async Task Roundcube_update_recreates_assets_and_keeps_health_and_Iis_settings()
    {
        Write("public_html/adapanel-health.php", "health marker");
        Write("public_html/web.config", "existing handlers and access rules");
        Write("public_html/.user.ini", "diagnostics settings");
        Write("adapanel-data/roundcube.db", "user database");
        var preserved = ComponentStatePreservation.Capture(Root,
            ComponentBootstrapper.DataDirectoriesToPreserve("roundcube").Concat(ComponentBootstrapper.ConfigurationFilesToPreserve("roundcube")));
        Directory.Delete(Root, true);
        foreach (var name in new[] { "skins", "program", "plugins" }) Write(name + "/asset.txt", "new asset");
        Write("public_html/index.php", "new entry");
        preserved.Restore();
        await ComponentWebPublication.RestoreAsync("roundcube", Root, CancellationToken.None);
        await ComponentWebPublication.RestoreAsync("roundcube", Root, CancellationToken.None);
        foreach (var name in new[] { "skins", "program", "plugins" })
            Assert.Equal("new asset", File.ReadAllText(Path.Combine(Root, "public_html", name, "asset.txt")));
        Assert.Equal("existing handlers and access rules", File.ReadAllText(Path.Combine(Root, "public_html/web.config")));
        Assert.Equal("user database", File.ReadAllText(Path.Combine(Root, "adapanel-data/roundcube.db")));
        ComponentWebPublication.DetachRoundcubeAssets(Root);
        Assert.True(File.Exists(Path.Combine(Root, "plugins/asset.txt")));
        Assert.False(Directory.Exists(Path.Combine(Root, "public_html/plugins")));
        Directory.Delete(Root, true);
    }

    public void Dispose()
    {
        if (Directory.Exists(Root)) ComponentWebPublication.DetachRoundcubeAssets(Root);
        if (Directory.Exists(area)) Directory.Delete(area, true);
    }
}
