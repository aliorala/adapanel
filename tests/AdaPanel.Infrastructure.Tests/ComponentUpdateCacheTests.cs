using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Bootstrap;
using AdaPanel.Infrastructure.Options;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class ComponentUpdateCacheTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "AdaPanel-update-cache-" + Guid.NewGuid().ToString("N"));
    private string CachePath => Path.Combine(root, "config", "component-updates.json");

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"php-8.3\":null}")]
    [InlineData("{\"php-8.3\":{}}")]
    public async Task Invalid_cache_does_not_report_zero_updates(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        await File.WriteAllTextAsync(CachePath, json);
        var service = Create(null!);
        var error = await Record.ExceptionAsync(() => service.GetPendingUpdateCountAsync());
        Assert.True(error is System.Text.Json.JsonException or InvalidDataException, error?.ToString());
        Assert.Equal(json, await File.ReadAllTextAsync(CachePath));
    }

    [Theory]
    [InlineData("8.3.35", true, 0)]
    [InlineData("8.3.33", false, 1)]
    public async Task Counter_uses_current_installed_version_not_old_cached_flag(string installedVersion, bool oldFlag, int expected)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.InstalledComponents.Add(new InstalledComponent { ComponentId = "php-8.3", Version = installedVersion, Status = ComponentInstallStatus.Installed });
        db.InstalledComponents.Add(new InstalledComponent { ComponentId = "adminer", Version = "1.0", Status = ComponentInstallStatus.Failed });
        await db.SaveChangesAsync();
        ComponentUpdateCache.Write(CachePath, new() { ["PHP-8.3"] = new() { AvailableVersion = "8.3.35", UpdateAvailable = oldFlag } });
        Assert.Equal(expected, await Create(db).GetPendingUpdateCountAsync());
    }

    [Fact]
    public async Task Missing_results_for_installed_software_are_unknown_not_current()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        Assert.Equal(0, await Create(db).GetPendingUpdateCountAsync());
        db.InstalledComponents.Add(new InstalledComponent { ComponentId = "php-8.3", Version = "8.3.33", Status = ComponentInstallStatus.Installed });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => Create(db).GetPendingUpdateCountAsync());
        ComponentUpdateCache.Write(CachePath, new() { ["php-8.3"] = new() { AvailableVersion = "8.3.35", VersionCheckFailed = true } });
        await Assert.ThrowsAsync<InvalidDataException>(() => Create(db).GetPendingUpdateCountAsync());
    }

    [Fact]
    public async Task Readers_see_whole_generations_during_replacement()
    {
        Dictionary<string, ComponentUpdateInfo> Generation(int n) => Enumerable.Range(0, 250).ToDictionary(
            i => "component-" + i, _ => new ComponentUpdateInfo { AvailableVersion = "1.0." + n });
        ComponentUpdateCache.Write(CachePath, Generation(0));
        var writer = Task.Run(() => { for (var n = 1; n <= 25; n++) ComponentUpdateCache.Write(CachePath, Generation(n)); });
        for (var i = 0; i < 100; i++)
        {
            var read = ComponentUpdateCache.Read(CachePath);
            Assert.Equal(250, read.Count);
            Assert.Single(read.Values.Select(info => info.AvailableVersion).Distinct());
        }
        await writer;
        Assert.All(ComponentUpdateCache.Read(CachePath).Values, item => Assert.Equal("1.0.25", item.AvailableVersion));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(CachePath)!));
    }

    private ComponentBootstrapper Create(AdaPanelDbContext db) => new(db, null!, null!, null!,
        Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }), null!, null!, NullLogger<ComponentBootstrapper>.Instance);

    public void Dispose()
    {
        var directory = Path.GetDirectoryName(CachePath)!;
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
        Directory.Delete(directory);
        Directory.Delete(root);
    }
}
