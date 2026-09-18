using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Bootstrap;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class ComponentStatePreservationTests : IDisposable
{
    private readonly string area = Path.Combine(Path.GetTempPath(), "adapanel-preserve-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(area, "runtime");
    private void Write(string relative, string content)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Theory]
    [InlineData("php-8.3", "php.ini")]
    [InlineData("roundcube", "config/config.inc.php")]
    [InlineData("roundcube", "public_html/web.config")]
    [InlineData("mariadb-11", "my.ini")]
    [InlineData("phpmyadmin", "config.inc.php")]
    public void Upgrade_keeps_custom_configuration_and_new_package_files(string component, string config)
    {
        Write(config, "custom configuration");
        var state = ComponentStatePreservation.Capture(Root, ComponentBootstrapper.ConfigurationFilesToPreserve(component));
        Directory.Delete(Root, true);
        Write(config, "package default");
        Write("new-version.txt", "new binary");
        state.Restore();
        Assert.Equal("custom configuration", File.ReadAllText(Path.Combine(Root, config)));
        Assert.Equal("new binary", File.ReadAllText(Path.Combine(Root, "new-version.txt")));
        state.Restore(); // tamamlanan geri koyma ikinci kez çalışınca yeni dosyayı değiştirmez
    }

    [Theory]
    [InlineData("roundcube", "adapanel-data/roundcube.db")]
    [InlineData("mariadb-11", "data/customer.db")]
    [InlineData("stalwart-mail", "data/mail.db")]
    public void Existing_data_wins_over_a_nonempty_packaged_directory(string component, string file)
    {
        Write(file, "customer data");
        var state = ComponentStatePreservation.Capture(Root, ComponentBootstrapper.DataDirectoriesToPreserve(component));
        Write(file, "empty factory database");
        state.Restore();
        Assert.Equal("customer data", File.ReadAllText(Path.Combine(Root, file)));
    }

    [Fact]
    public void Failed_extraction_can_restore_state_into_an_absent_install_directory()
    {
        Write("php.ini", "custom");
        var state = ComponentStatePreservation.Capture(Root, ["php.ini"]);
        Directory.Delete(Root, true);
        state.Restore();
        Assert.Equal("custom", File.ReadAllText(Path.Combine(Root, "php.ini")));
    }

    [Fact]
    public void Restore_failure_is_reported_and_saved_data_remains_recoverable()
    {
        Write("config/config.inc.php", "custom");
        var state = ComponentStatePreservation.Capture(Root, ["config/config.inc.php"]);
        Directory.Delete(Path.Combine(Root, "config"));
        Write("config", "blocks the directory");
        var error = Assert.Throws<AggregateException>(() => state.Restore());
        Assert.Contains("Recovery path:", error.ToString());
        File.Delete(Path.Combine(Root, "config"));
        state.Restore();
        Assert.Equal("custom", File.ReadAllText(Path.Combine(Root, "config/config.inc.php")));
    }

    [Fact]
    public void Outside_path_is_rejected_before_any_state_is_moved()
    {
        Write("php.ini", "custom");
        Assert.Throws<AppException>(() => ComponentStatePreservation.Capture(Root, ["php.ini", "../outside"]));
        Assert.Equal("custom", File.ReadAllText(Path.Combine(Root, "php.ini")));
    }

    [Fact]
    public async Task Php_update_keeps_tenant_sections_and_custom_limits_after_post_install()
    {
        Write("php.ini", "memory_limit=768M\r\n[PATH=C:/sites/customer]\r\nopen_basedir=C:/sites/customer\r\nupload_max_filesize=32M\r\n");
        var state = ComponentStatePreservation.Capture(Root, ComponentBootstrapper.ConfigurationFilesToPreserve("php-8.3"));
        Directory.Delete(Root, true);
        Write("php.ini-production", "memory_limit=128M");
        state.Restore();
        await ComponentBootstrapper.EnsurePhpIniAsync(Root, CancellationToken.None);
        var actual = File.ReadAllText(Path.Combine(Root, "php.ini"));
        Assert.Contains("memory_limit=768M", actual);
        Assert.Contains("[PATH=C:/sites/customer]", actual);
        Assert.Contains("open_basedir=C:/sites/customer", actual);
        Assert.Contains("upload_max_filesize=32M", actual);
    }

    [Fact]
    public void Trailing_separator_keeps_saved_files_outside_the_deleted_root()
    {
        Write("php.ini", "custom");
        var state = ComponentStatePreservation.Capture(Root + Path.DirectorySeparatorChar, ["php.ini"]);
        Directory.Delete(Root, true);
        state.Restore();
        Assert.Equal("custom", File.ReadAllText(Path.Combine(Root, "php.ini")));
    }

    [Fact]
    public void Upgrade_reuses_existing_credential_without_generating_a_new_one()
    {
        Assert.Equal("original", ComponentBootstrapper.ResolveInstallPassword(true, "original", true,
            () => throw new Exception("Must not rotate a credential during an upgrade")));
        Assert.Throws<AppException>(() => ComponentBootstrapper.ResolveInstallPassword(true, "", true, () => "wrong-new-password"));
        Assert.Equal("new", ComponentBootstrapper.ResolveInstallPassword(true, "", false, () => "new"));
    }

    public void Dispose() { if (Directory.Exists(area)) Directory.Delete(area, true); }
}
