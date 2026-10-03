using System.Reflection;
using AdaPanel.Core.Dtos.Php;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Php;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class PhpSettingsPreservationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-php-settings-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection connection;
    private readonly AdaPanelDbContext db;
    private readonly PhpService service;

    public PhpSettingsPreservationTests()
    {
        Directory.CreateDirectory(root);
        connection = new SqliteConnection("Data Source=:memory:"); connection.Open();
        db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        var sites = DispatchProxy.Create<IIisSiteService, Proxy>();
        ((Proxy)(object)sites).Handler = (method, _) => method.Name == nameof(IIisSiteService.GetSiteAsync)
            ? Task.FromResult(new SiteDto { Name = "fixture.test", PhysicalPath = root })
            : throw new NotSupportedException(method.Name);
        // IIS işlemi çalıştırılmaz: burada gerçek dosya dönüşümü ve DTO okunması sınanır.
        var gate = DispatchProxy.Create<IConcurrencyLock, Proxy>();
        ((Proxy)(object)gate).Handler = (_, _) => Task.CompletedTask;
        service = new PhpService(db, null!, gate, sites, NullLogger<PhpService>.Instance);
    }

    [Fact]
    public async Task Site_limits_preserve_unrelated_user_ini_directives_and_comments()
    {
        var ini = Path.Combine(root, ".user.ini");
        await File.WriteAllLinesAsync(ini, ["; Application settings", "session.name = CUSTOMSESSION", "auto_prepend_file = bootstrap.php", "memory_limit = 128M"]);
        var result = await service.UpdateSitePhpSettingsAsync("fixture.test", new UpdateSitePhpSettingsRequest
        {
            MemoryLimit = "512M", PostMaxSize = "0", DisplayErrors = true
        });
        var lines = await File.ReadAllLinesAsync(ini);
        Assert.Contains("; Application settings", lines);
        Assert.Contains("session.name = CUSTOMSESSION", lines);
        Assert.Contains("auto_prepend_file = bootstrap.php", lines);
        Assert.Equal("512M", result.MemoryLimit); Assert.True(result.DisplayErrors);
    }

    [Theory]
    [InlineData("On", true)]
    [InlineData("1", true)]
    [InlineData("Off", false)]
    public async Task Global_settings_report_display_errors_from_the_global_section(string value, bool expected)
    {
        db.InstalledComponents.Add(new InstalledComponent { ComponentId = "php-8.3", InstallDir = root, Status = ComponentInstallStatus.Installed });
        await db.SaveChangesAsync();
        await File.WriteAllLinesAsync(Path.Combine(root, "php.ini"), [$"display_errors = {value}", "[PATH=C:/other-site]", "display_errors = Off"]);
        Assert.Equal(expected, (await service.GetGlobalPhpSettingsAsync()).DisplayErrors);
    }

    [Fact]
    public void Changing_a_global_directive_updates_duplicate_active_entries_without_changing_site_overrides()
    {
        var lines = new List<string> { "memory_limit = 128M", "; user comment", "memory_limit = 256M", "[PATH=C:/site]", "memory_limit = 64M" };
        typeof(PhpService).GetMethod("SetIniDirective", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [lines, "memory_limit", "512M"]);
        var global = PhpIniSiteIsolation.GlobalLines(lines).Where(line => line.StartsWith("memory_limit")).ToArray();
        Assert.All(global, line => Assert.Equal("memory_limit = 512M", line));
        Assert.Equal("memory_limit = 64M", lines[^1]); Assert.Contains("; user comment", lines);
    }

    [Theory]
    [InlineData(true, "On")]
    [InlineData(false, "Off")]
    public void Editable_settings_include_display_errors_and_preserve_site_overrides(bool enabled, string expected)
    {
        var lines = PhpService.UpdateIniSettings(["[PHP]", "display_errors = Off", "[PATH=C:/site]", "display_errors = On"],
            new UpdateSitePhpSettingsRequest { DisplayErrors = enabled });
        Assert.Contains($"display_errors = {expected}", PhpIniSiteIsolation.GlobalLines(lines));
        Assert.Equal("display_errors = On", lines[^1]);
    }

    public void Dispose()
    {
        db.Dispose(); connection.Dispose();
        Directory.Delete(root, true);
    }
    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    }
}
