using System.Text.Json;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Database;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class DatabaseDetailSiteMappingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"adapanel-detail-mapping-{Guid.NewGuid():N}");
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly AdaPanelDbContext context;
    private readonly JsonFileStore files;
    private readonly DatabaseService service;
    private string MappingPath => Path.Combine(root, "config", "site-databases.json");

    public DatabaseDetailSiteMappingTests()
    {
        connection.Open();
        context = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
        context.Database.EnsureCreated();
        context.InstalledComponents.Add(new InstalledComponent
        {
            ComponentId = "postgresql-16", Status = ComponentInstallStatus.Installed,
            ProtectedConfigurationJson = "{\"generatedPassword\":\"fixture-only\"}"
        });
        context.SaveChanges();
        files = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
        var protector = new FixtureProtector();
        service = new DatabaseService(context, protector, new DatabaseCredentialStore(files, protector), new SiteDatabaseLinkService(files), []);
        Directory.CreateDirectory(Path.GetDirectoryName(MappingPath)!);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    public async Task Confirmed_absence_is_unlinked(string state)
    {
        if (state == "empty") await files.WriteAsync("config/site-databases.json", Array.Empty<SiteDatabaseLinkDto>());
        var detail = await service.GetDetailAsync("postgresql", "Foo");
        Assert.Equal(string.Empty, detail.SiteName);
        Assert.Equal("Foo", detail.Name);
    }

    [Fact]
    public async Task Known_link_uses_exact_database_identity()
    {
        await files.WriteAsync("config/site-databases.json", new[]
        {
            new SiteDatabaseLinkDto { Engine = "mysql", DatabaseName = "Foo", SiteName = "other-engine.test" },
            new SiteDatabaseLinkDto { Engine = "postgres", DatabaseName = "foo", SiteName = "other-case.test" },
            new SiteDatabaseLinkDto { Engine = "postgres", DatabaseName = "Foo", SiteName = "matched.test" }
        });
        var detail = await service.GetDetailAsync("postgresql", "Foo");
        Assert.Equal("matched.test", detail.SiteName);
        Assert.Equal("postgresql", detail.Engine);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("locked")]
    public async Task Mapping_failure_returns_unknown_without_failing_the_detail(string failure)
    {
        await File.WriteAllTextAsync(MappingPath, failure == "malformed" ? "{ invalid JSON" : "[]");
        using var locked = failure == "locked" ? new FileStream(MappingPath, FileMode.Open, FileAccess.Read, FileShare.None) : null;
        var detail = await service.GetDetailAsync("postgresql", "Foo");
        Assert.Null(detail.SiteName);
        Assert.Equal("Foo", detail.Name);
        Assert.Equal(5432, detail.Port);
        Assert.Equal("PostgreSQL", detail.EngineName);
        var json = JsonSerializer.SerializeToElement(detail, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(JsonValueKind.Null, json.GetProperty("siteName").ValueKind);
    }

    [Fact]
    public async Task Cancelled_request_is_not_reported_as_a_successful_detail()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetDetailAsync("postgresql", "Foo", cancellation.Token));
    }

    public void Dispose()
    {
        context.Dispose(); connection.Dispose(); Directory.Delete(root, recursive: true);
    }
    private sealed class FixtureProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }
}
