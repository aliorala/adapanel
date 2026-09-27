using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Bootstrap;
using AdaPanel.Infrastructure.Options;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class InstalledComponentVersionRecoveryTests
{
    [Theory]
    [InlineData("5.0.14.1", "5.0.14.1")]
    [InlineData("2.2.9.1701+3f6d83bb", "2.2.9.1701")]
    [InlineData(" 2.2.9.1701 ", "2.2.9.1701")]
    [InlineData("2.2.9-beta", null)]
    [InlineData("latest", null)]
    [InlineData("0.0.0.0", null)]
    [InlineData("error 2.2.9.1701", null)]
    [InlineData("999999999999.2.3", null)]
    public void Resource_is_strict_stable_version(string resource, string? expected) =>
        Assert.Equal(expected, InstalledComponentVersionReader.Parse(resource));

    [Theory]
    [InlineData("2.2.9.1701+abc", "2.2.9.1701", "2.2.9.1701")]
    [InlineData(null, "2.2.9.1701", "2.2.9.1701")]
    [InlineData("", "5.0.14.1", "5.0.14.1")]
    [InlineData("2.2.9-beta", "2.2.9.0", null)]
    [InlineData("unknown", "2.2.9.0", null)]
    public void File_version_cannot_disguise_a_nonstable_product_version(string? product, string file, string? expected) =>
        Assert.Equal(expected, InstalledComponentVersionReader.FromResources(product, file));

    [Theory]
    [InlineData("redis", "redis-server.exe")]
    [InlineData("win-acme", "wacs.exe")]
    public void Reads_actual_PE_version_and_does_not_search_backup_subdirectories(string id, string filename)
    {
        var root = Path.Combine(Path.GetTempPath(), "AdaPanel-version-resource-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "old"));
        try
        {
            var source = typeof(ComponentBootstrapper).Assembly.Location;
            File.Copy(source, Path.Combine(root, "old", filename));
            Assert.Null(InstalledComponentVersionReader.Read(id, root));
            File.Copy(source, Path.Combine(root, filename));
            var expected = InstalledComponentVersionReader.Parse(System.Diagnostics.FileVersionInfo.GetVersionInfo(source).ProductVersion);
            Assert.NotNull(expected);
            Assert.Equal(expected, InstalledComponentVersionReader.Read(id, root));
            Assert.Null(InstalledComponentVersionReader.Read("unknown", root));
            Assert.Null(InstalledComponentVersionReader.Read(id, "relative"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("redis", "latest", ComponentInstallStatus.Installed, "5.0.14.1", "5.0.14.1", 1)]
    [InlineData("win-acme", "latest", ComponentInstallStatus.Installed, "2.2.9.1701", "2.2.9.1701", 1)]
    [InlineData("redis", "latest", ComponentInstallStatus.Installed, null, "latest", 1)]
    [InlineData("redis", "latest", ComponentInstallStatus.Installed, "invalid", "latest", 1)]
    [InlineData("redis", "5.0.14.1", ComponentInstallStatus.Installed, "6.0.0", "5.0.14.1", 0)]
    [InlineData("redis", "latest", ComponentInstallStatus.Failed, "6.0.0", "latest", 0)]
    [InlineData("composer", "latest", ComponentInstallStatus.Installed, "2.0.0", "latest", 0)]
    [InlineData("redis", "latest", ComponentInstallStatus.Installed, "5.0.14.1", "latest", 0, true)]
    public async Task Recovery_persists_only_measured_missing_versions(string id, string version,
        ComponentInstallStatus status, string? measured, string expected, int expectedReads, bool external = false)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var installedAt = DateTime.UtcNow.AddYears(-1);
        var row = new InstalledComponent { ComponentId = id, Version = version, Status = status,
            InstallDir = Path.GetTempPath(), InstalledAtUtc = installedAt, LastError = "preserve",
            ProtectedConfigurationJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
                { ["systemDetected"] = external.ToString(), ["generatedPassword"] = "test-keep" }) };
        db.InstalledComponents.Add(row);
        await db.SaveChangesAsync();
        var service = new ComponentBootstrapper(db, null!, new IdentityProtector(), null!,
            Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions()), null!, null!, NullLogger<ComponentBootstrapper>.Instance);
        var reads = 0;
        await service.RecoverInstalledVersionAsync(row, CancellationToken.None, (_, path) =>
        {
            reads++;
            Assert.Equal(row.InstallDir, path);
            return measured;
        });
        db.ChangeTracker.Clear();
        var saved = await db.InstalledComponents.SingleAsync();
        Assert.Equal(expected, saved.Version);
        Assert.Equal(expectedReads, reads);
        Assert.Equal(installedAt, saved.InstalledAtUtc);
        Assert.Equal(status, saved.Status);
        Assert.Equal("preserve", saved.LastError);
        Assert.Equal(row.ProtectedConfigurationJson, saved.ProtectedConfigurationJson);
        await service.RecoverInstalledVersionAsync(saved, CancellationToken.None, (_, _) => throw new IOException("locked"));
        Assert.Equal(expected, saved.Version);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RecoverInstalledVersionAsync(saved, new CancellationToken(true)));
    }

    private sealed class IdentityProtector : ISecretProtector
    {
        public string Protect(string value) => value;
        public string Unprotect(string value) => value;
    }
}
