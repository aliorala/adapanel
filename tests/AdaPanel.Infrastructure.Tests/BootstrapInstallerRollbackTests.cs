using AdaPanel.Core.Dtos.Components;
using AdaPanel.Infrastructure.Bootstrap;
using AdaPanel.Infrastructure.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class BootstrapInstallerRollbackTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-installer-rollback-" + Guid.NewGuid().ToString("N"));
    private string InstallRoot => Path.Combine(root, "runtimes", "postgresql", "16");
    private ComponentBootstrapper CreateService() => new(null!, null!, null!, null!,
        Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }), null!, null!,
        NullLogger<ComponentBootstrapper>.Instance);
    private void Write(string relative, string value)
    {
        var path = Path.Combine(InstallRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Failed_postgresql_install_keeps_existing_data_through_uninstall_and_directory_cleanup(bool uninstallDeletesRoot, bool uninstallFails)
    {
        Write("data/customer.db", "existing customer data");
        Write("bin/postgres.exe", "failed package marker - never execute");
        var component = new ComponentManifestItemDto { Id = "postgresql-16", Type = "installer" };
        var calls = 0;
        var failure = await Record.ExceptionAsync(() => CreateService().RollbackInstallerFilesAsync(component, InstallRoot, () => {
            calls++;
            if (uninstallDeletesRoot) Directory.Delete(InstallRoot, recursive: true);
            if (uninstallFails) throw new IOException("fake uninstaller failed after deleting files");
            return Task.CompletedTask;
        }));

        if (uninstallFails) Assert.IsType<IOException>(failure); else Assert.Null(failure);
        Assert.Equal(1, calls);
        Assert.Equal("existing customer data", File.ReadAllText(Path.Combine(InstallRoot, "data", "customer.db")));
        Assert.False(File.Exists(Path.Combine(InstallRoot, "bin", "postgres.exe")));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(InstallRoot)!, ".16-preserved-*"));
    }

    [Fact]
    public async Task Stateless_installer_rollback_still_removes_failed_package_files()
    {
        Write("python.exe", "failed package marker - never execute");
        var calls = 0;
        await CreateService().RollbackInstallerFilesAsync(new() { Id = "python-3", Type = "installer" }, InstallRoot,
            () => { calls++; return Task.CompletedTask; });
        Assert.Equal(1, calls);
        Assert.False(Directory.Exists(InstallRoot));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
