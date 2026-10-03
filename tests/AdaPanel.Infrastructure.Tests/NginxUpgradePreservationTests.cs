using System.Text;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Nginx;
using AdaPanel.Infrastructure.Bootstrap;
using AdaPanel.Infrastructure.Nginx;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class NginxUpgradePreservationTests : IDisposable
{
    private readonly string area = Path.Combine(Path.GetTempPath(), "adapanel-nginx-upgrade-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(area, "nginx");
    private static readonly byte[] CustomConfig = Encoding.UTF8.GetBytes("# custom configuration\r\ninclude custom/site.conf;\r\n");
    private static readonly byte[] Metadata = Encoding.UTF8.GetBytes("{\"listenPort\":9099,\"serverName\":\"kept.test\",\"customMetadata\":\"keep\"}");
    private static readonly CommandResult Valid = new() { ExitCode = 0 };

    private void Write(string relative, byte[] bytes)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private void ExistingConfiguration(bool metadata = true)
    {
        Write("conf/nginx.conf", CustomConfig);
        Write("conf/custom/site.conf", Encoding.UTF8.GetBytes("server { listen 9099; }"));
        if (metadata) Write("conf/adapanel.json", Metadata);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Upgrade_or_failed_extraction_preserves_configuration_metadata_and_includes(bool extractionFails)
    {
        ExistingConfiguration();
        var state = ComponentStatePreservation.Capture(Root,
            ComponentBootstrapper.DataDirectoriesToPreserve("nginx").Concat(ComponentBootstrapper.ConfigurationFilesToPreserve("nginx")));
        Directory.Delete(Root, true);
        if (!extractionFails)
        {
            Write("conf/nginx.conf", Encoding.UTF8.GetBytes("package default"));
            Write("new-version.txt", Encoding.UTF8.GetBytes("new binary"));
        }
        state.Restore();
        Assert.Equal(CustomConfig, File.ReadAllBytes(Path.Combine(Root, "conf/nginx.conf")));
        Assert.Equal(Metadata, File.ReadAllBytes(Path.Combine(Root, "conf/adapanel.json")));
        Assert.Equal("server { listen 9099; }", File.ReadAllText(Path.Combine(Root, "conf/custom/site.conf")));
        if (!extractionFails) Assert.Equal("new binary", File.ReadAllText(Path.Combine(Root, "new-version.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Post_install_validates_and_starts_restored_configuration_without_replacing_it(bool metadataExists)
    {
        ExistingConfiguration(metadataExists);
        var events = new List<string>();
        var result = await NginxInstallationSupport.PrepareAsync(Root, true,
            () => throw new InvalidOperationException("Existing configuration must not allocate a default port"),
            (path, _) =>
            {
                Assert.Equal(CustomConfig, File.ReadAllBytes(Path.Combine(Root, path)));
                events.Add("validate"); return Task.FromResult(Valid);
            }, _ => { events.Add("register"); return Task.CompletedTask; },
            _ => { events.Add("start"); return Task.CompletedTask; }, default);
        Assert.Equal(new[] { "validate", "register", "start" }, events);
        Assert.Equal(CustomConfig, File.ReadAllBytes(Path.Combine(Root, "conf/nginx.conf")));
        if (metadataExists)
        {
            Assert.Equal(9099, result.ListenPort);
            Assert.Equal(Metadata, File.ReadAllBytes(Path.Combine(Root, "conf/adapanel.json")));
        }
        else Assert.False(File.Exists(Path.Combine(Root, "conf/adapanel.json")));
    }

    [Fact]
    public async Task Invalid_restored_configuration_is_reported_without_registering_or_starting()
    {
        ExistingConfiguration();
        var activated = false;
        var error = await Assert.ThrowsAsync<AppException>(() => NginxInstallationSupport.PrepareAsync(Root, true,
            () => new() { ListenPort = 8088 },
            (path, _) => Task.FromResult(File.ReadAllBytes(Path.Combine(Root, path)).SequenceEqual(CustomConfig)
                ? new CommandResult { ExitCode = 1, StandardError = "custom include failed" } : Valid),
            _ => { activated = true; return Task.CompletedTask; }, _ => Task.CompletedTask, default));
        Assert.Equal("nginx.config_invalid", error.MessageKey);
        Assert.False(activated);
        Assert.Equal(CustomConfig, File.ReadAllBytes(Path.Combine(Root, "conf/nginx.conf")));
        Assert.Equal(Metadata, File.ReadAllBytes(Path.Combine(Root, "conf/adapanel.json")));
    }

    [Fact]
    public async Task Malformed_restored_metadata_is_not_silently_replaced()
    {
        ExistingConfiguration();
        Write("conf/adapanel.json", Encoding.UTF8.GetBytes("{invalid"));
        await Assert.ThrowsAsync<JsonException>(() => NginxInstallationSupport.PrepareAsync(Root, true,
            () => new(), (_, _) => Task.FromResult(Valid), _ => Task.CompletedTask, _ => Task.CompletedTask, default));
        Assert.Equal(CustomConfig, File.ReadAllBytes(Path.Combine(Root, "conf/nginx.conf")));
        Assert.Equal("{invalid", File.ReadAllText(Path.Combine(Root, "conf/adapanel.json")));
    }

    [Fact]
    public async Task First_install_replaces_packaged_defaults_with_the_selected_available_port()
    {
        Write("conf/nginx.conf", Encoding.UTF8.GetBytes("package default"));
        var result = await NginxInstallationSupport.PrepareAsync(Root, false,
            () => new() { ListenPort = 9088 }, (_, _) => Task.FromResult(Valid), _ => Task.CompletedTask, _ => Task.CompletedTask, default);
        Assert.Equal(9088, result.ListenPort);
        Assert.Contains("listen 127.0.0.1:9088;", File.ReadAllText(Path.Combine(Root, "conf/nginx.conf")));
        Assert.Equal(9088, (await NginxInstallationSupport.ReadConfigurationAsync(Root, default)).ListenPort);
    }

    [Fact]
    public void Preservation_is_decided_before_package_extraction_and_rejects_orphaned_metadata()
    {
        Assert.False(NginxInstallationSupport.HasConfigurationToPreserve(Root));
        ExistingConfiguration(metadata: false);
        Assert.True(NginxInstallationSupport.HasConfigurationToPreserve(Root));
        Write("conf/adapanel.json", Metadata);
        File.Delete(Path.Combine(Root, "conf/nginx.conf"));
        var error = Assert.Throws<AppException>(() => NginxInstallationSupport.HasConfigurationToPreserve(Root));
        Assert.Equal("nginx.config_invalid", error.MessageKey);
        Assert.Equal(Metadata, File.ReadAllBytes(Path.Combine(Root, "conf/adapanel.json")));
    }

    [Theory]
    [InlineData("validate")]
    [InlineData("register")]
    [InlineData("start")]
    public async Task Failed_first_install_restores_absence_so_retry_creates_panel_configuration(string stage)
    {
        Write("conf/nginx.conf", Encoding.UTF8.GetBytes("package default"));
        await Assert.ThrowsAsync<IOException>(() => NginxInstallationSupport.PrepareAsync(Root, false,
            () => new() { ListenPort = 9088 },
            (_, _) => stage == "validate" ? throw new IOException("fixture validation failed") : Task.FromResult(Valid),
            _ => stage == "register" ? throw new IOException("fixture registration failed") : Task.CompletedTask,
            _ => stage == "start" ? throw new IOException("fixture start failed") : Task.CompletedTask, default));
        Assert.False(File.Exists(Path.Combine(Root, "conf/nginx.conf")));
        Assert.False(File.Exists(Path.Combine(Root, "conf/adapanel.json")));
        var preserve = NginxInstallationSupport.HasConfigurationToPreserve(Root);
        Assert.False(preserve);
        // Retry extracts a fresh package before Prepare, just like Bootstrapper.
        Write("conf/nginx.conf", Encoding.UTF8.GetBytes("package default"));
        var configuration = await NginxInstallationSupport.PrepareAsync(Root, preserve,
            () => new() { ListenPort = 9089 }, (_, _) => Task.FromResult(Valid),
            _ => Task.CompletedTask, _ => Task.CompletedTask, default);
        Assert.Equal(9089, configuration.ListenPort);
        Assert.Contains("listen 127.0.0.1:9089;", File.ReadAllText(Path.Combine(Root, "conf/nginx.conf")));
        Assert.Equal(9089, (await NginxInstallationSupport.ReadConfigurationAsync(Root, default)).ListenPort);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_existing_install_preserves_user_configuration_with_or_without_metadata(bool metadataExists)
    {
        ExistingConfiguration(metadataExists);
        await Assert.ThrowsAsync<IOException>(() => NginxInstallationSupport.PrepareAsync(Root, true,
            () => throw new InvalidOperationException("No defaults for user configuration"),
            (_, _) => Task.FromResult(Valid), _ => Task.CompletedTask,
            _ => throw new IOException("fixture start failed"), default));
        Assert.Equal(CustomConfig, File.ReadAllBytes(Path.Combine(Root, "conf/nginx.conf")));
        Assert.Equal(metadataExists, File.Exists(Path.Combine(Root, "conf/adapanel.json")));
        if (metadataExists) Assert.Equal(Metadata, File.ReadAllBytes(Path.Combine(Root, "conf/adapanel.json")));
    }

    [Fact]
    public void Failed_first_extraction_does_not_turn_a_packaged_config_into_user_configuration()
    {
        Write("conf/nginx.conf", Encoding.UTF8.GetBytes("package default"));
        Write("conf/custom/kept.conf", Encoding.UTF8.GetBytes("unrelated preserved include"));
        Write("nginx.exe", Encoding.UTF8.GetBytes("fixture binary is never executed"));
        var bootstrapper = new ComponentBootstrapper(null!, null!, null!, null!,
            Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = area }),
            null!, null!, NullLogger<ComponentBootstrapper>.Instance);
        bootstrapper.RollbackInstallationFiles(new() { Id = "nginx", Type = "zip" }, Root, false);
        Assert.False(File.Exists(Path.Combine(Root, "nginx.exe")));
        Assert.False(NginxInstallationSupport.HasConfigurationToPreserve(Root));
        Assert.Equal("unrelated preserved include", File.ReadAllText(Path.Combine(Root, "conf/custom/kept.conf")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Existing_install_rollback_preserves_user_configuration_even_without_metadata(bool metadataExists)
    {
        ExistingConfiguration(metadataExists);
        Write("nginx.exe", Encoding.UTF8.GetBytes("fixture binary is never executed"));
        var bootstrapper = new ComponentBootstrapper(null!, null!, null!, null!,
            Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = area }),
            null!, null!, NullLogger<ComponentBootstrapper>.Instance);
        bootstrapper.RollbackInstallationFiles(new() { Id = "nginx", Type = "zip" }, Root, true);
        Assert.False(File.Exists(Path.Combine(Root, "nginx.exe")));
        Assert.True(NginxInstallationSupport.HasConfigurationToPreserve(Root));
        Assert.Equal(CustomConfig, File.ReadAllBytes(Path.Combine(Root, "conf/nginx.conf")));
        Assert.Equal("server { listen 9099; }", File.ReadAllText(Path.Combine(Root, "conf/custom/site.conf")));
        Assert.Equal(metadataExists, File.Exists(Path.Combine(Root, "conf/adapanel.json")));
        if (metadataExists) Assert.Equal(Metadata, File.ReadAllBytes(Path.Combine(Root, "conf/adapanel.json")));
    }

    [Fact]
    public async Task Later_first_install_failure_preserves_successfully_generated_panel_configuration()
    {
        Write("conf/nginx.conf", Encoding.UTF8.GetBytes("package default"));
        await NginxInstallationSupport.PrepareAsync(Root, false,
            () => new() { ListenPort = 9090 }, (_, _) => Task.FromResult(Valid),
            _ => Task.CompletedTask, _ => Task.CompletedTask, default);
        Write("nginx.exe", Encoding.UTF8.GetBytes("fixture binary is never executed"));
        var bootstrapper = new ComponentBootstrapper(null!, null!, null!, null!,
            Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = area }),
            null!, null!, NullLogger<ComponentBootstrapper>.Instance);
        bootstrapper.RollbackInstallationFiles(new() { Id = "nginx", Type = "zip" }, Root, false);
        Assert.False(File.Exists(Path.Combine(Root, "nginx.exe")));
        Assert.True(NginxInstallationSupport.HasConfigurationToPreserve(Root));
        Assert.Contains("listen 127.0.0.1:9090;", File.ReadAllText(Path.Combine(Root, "conf/nginx.conf")));
        Assert.Equal(9090, (await NginxInstallationSupport.ReadConfigurationAsync(Root, default)).ListenPort);
    }

    [Fact]
    public void First_install_rollback_does_not_clean_configuration_outside_managed_roots()
    {
        Write("conf/nginx.conf", Encoding.UTF8.GetBytes("package default"));
        var bootstrapper = new ComponentBootstrapper(null!, null!, null!, null!,
            Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Path.Combine(area, "managed") }),
            null!, null!, NullLogger<ComponentBootstrapper>.Instance);
        bootstrapper.RollbackInstallationFiles(new() { Id = "nginx", Type = "zip" }, Root, false);
        Assert.Equal("package default", File.ReadAllText(Path.Combine(Root, "conf/nginx.conf")));
    }

    public void Dispose() { if (Directory.Exists(area)) Directory.Delete(area, true); }
}
