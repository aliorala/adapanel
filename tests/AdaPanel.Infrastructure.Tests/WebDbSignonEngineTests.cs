using System.Reflection;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Database;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using AdaPanel.Infrastructure.WebDbClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class WebDbSignonEngineTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-webdb-engine-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection connection;
    private readonly AdaPanelDbContext db;
    private readonly FixtureProtector protector = new();
    private readonly DatabaseCredentialStore credentials;
    private readonly WebDbClientService service;

    public WebDbSignonEngineTests()
    {
        Directory.CreateDirectory(root);
        connection = new SqliteConnection("Data Source=:memory:"); connection.Open();
        db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        var files = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
        credentials = new DatabaseCredentialStore(files, protector);
        // Test edilen profil/JSON yolu IIS, Windows ACL veya bir DB süreci çağırmaz.
        service = new WebDbClientService(db, null!, null!, null!, protector, credentials, null!, files, NullLogger<WebDbClientService>.Instance);
    }

    [Theory]
    [InlineData("mariadb", "mariadb-11", "server", true)]
    [InlineData("mysql", "mysql-8", "server", true)]
    [InlineData("mariadb", "mariadb-11", "server", false)]
    [InlineData("mysql", "mysql-8", "server", false)]
    [InlineData("postgresql", "postgresql-16", "pgsql", false)]
    public async Task Signon_uses_the_registered_admin_port_user_and_protected_password(string engine, string component, string driver, bool phpMyAdmin)
    {
        await Install(component, "custom_admin", "fixture-admin-password", "15432");
        var target = await service.ResolveSignonConnectionAsync(engine, " app_db ", phpMyAdmin);
        Assert.Equal("custom_admin", target.User); Assert.Equal("fixture-admin-password", target.Password);
        Assert.Equal(15432, target.Port); Assert.Equal("127.0.0.1", target.Host);
        Assert.Equal("app_db", target.Database); Assert.Equal(driver, target.Driver); Assert.True(target.UsesAdminCredential);
        using var payload = JsonDocument.Parse(WebDbClientService.SerializeSignonPayload(target));
        Assert.Equal("15432", payload.RootElement.GetProperty("port").GetString());
        Assert.Equal(driver, payload.RootElement.GetProperty("driver").GetString());
        Assert.Equal("custom_admin", payload.RootElement.GetProperty("user").GetString());
    }

    [Theory]
    [InlineData("mariadb", "mariadb-11", "root", 3306, "server")]
    [InlineData("mysql", "mysql-8", "root", 3307, "server")]
    [InlineData("postgresql", "postgresql-16", "postgres", 5432, "pgsql")]
    public async Task Default_profiles_remain_compatible(string engine, string component, string admin, int port, string driver)
    {
        await Install(component);
        var target = await service.ResolveSignonConnectionAsync(engine, null, false);
        Assert.Equal(admin, target.User); Assert.Equal(port, target.Port); Assert.Equal(driver, target.Driver);
        Assert.Equal(string.Empty, target.Database); Assert.True(target.UsesAdminCredential);
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData(" PostgreSQL ")]
    public async Task PostgreSql_aliases_use_the_canonical_database_credential_key(string engine)
    {
        await Install("postgresql-16", "pg_admin", "fixture-admin-password", "5544");
        await credentials.UpsertAsync("postgresql", "site_user", "%", "site_db", "fixture-site-password", default);
        var target = await service.ResolveSignonConnectionAsync(engine, "site_db", false);
        Assert.Equal("site_user", target.User); Assert.Equal("fixture-site-password", target.Password);
        Assert.Equal(5544, target.Port); Assert.Equal("pgsql", target.Driver); Assert.False(target.UsesAdminCredential);
    }

    [Theory]
    [InlineData("mysql", "mysql-8", true)]
    [InlineData("postgresql", "postgresql-16", false)]
    public async Task A_database_password_works_without_a_stored_admin_password(string engine, string component, bool phpMyAdmin)
    {
        await Install(component, "admin", "", "15555");
        await credentials.UpsertAsync(engine, "site_user", "%", "site_db", "fixture-site-password", default);
        var target = await service.ResolveSignonConnectionAsync(engine, "site_db", phpMyAdmin);
        Assert.Equal("site_user", target.User); Assert.Equal("fixture-site-password", target.Password);
        Assert.Equal(15555, target.Port); Assert.False(target.UsesAdminCredential);
    }

    [Fact]
    public async Task A_custom_admin_saved_as_a_database_credential_still_requires_admin_token_protection()
    {
        await Install("mysql-8", "custom_admin");
        await credentials.UpsertAsync("mysql", "custom_admin", "%", "site_db", "fixture-site-password", default);
        Assert.True((await service.ResolveSignonConnectionAsync("mysql", "site_db", true)).UsesAdminCredential);
    }

    [Fact]
    public async Task A_missing_password_is_reported_before_writing_a_token()
    {
        await Install("postgresql-16", password: "");
        var error = await Assert.ThrowsAsync<AppException>(() => service.ResolveSignonConnectionAsync("postgresql", "site_db", false));
        Assert.Equal("database.admin_password_missing", error.MessageKey);
        Assert.Empty(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("postgresql", true)]
    [InlineData("postgres", true)]
    [InlineData("mssql", true)]
    [InlineData("mssql", false)]
    [InlineData("sqlserver", false)]
    [InlineData("sqlite", false)]
    [InlineData("", false)]
    public async Task Unsupported_tool_engine_is_rejected_before_reading_or_writing_deployment_state(string engine, bool phpMyAdmin)
    {
        Directory.CreateDirectory(Path.Combine(root, "config"));
        var file = Path.Combine(root, "config", "webdb.json");
        await File.WriteAllTextAsync(file, "invalid deployment JSON must never be read");
        var error = await Assert.ThrowsAsync<AppException>(() => phpMyAdmin
            ? service.CreatePhpMyAdminSignonAsync(engine, "site_db")
            : service.CreateAdminerSignonAsync(engine, "site_db"));
        Assert.Equal("database.engine_not_supported", error.MessageKey);
        Assert.Equal("invalid deployment JSON must never be read", await File.ReadAllTextAsync(file));
        Assert.Single(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Generated_Adminer_wrapper_uses_the_token_driver_for_both_session_and_redirect()
    {
        var generate = typeof(WebDbClientService).GetMethod("EnsureAdminerSignonAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        await (Task)generate.Invoke(null, [root, CancellationToken.None])!;
        var php = await File.ReadAllTextAsync(Path.Combine(root, "adapanel-signon.php"));
        Assert.Contains("$adaData['driver']", php, StringComparison.Ordinal);
        Assert.Contains("array('server', 'pgsql')", php, StringComparison.Ordinal);
        Assert.Contains("$_SESSION['pwds'][$adaSurucu][$adaSunucu][$adaKullanici]", php, StringComparison.Ordinal);
        Assert.Contains("$adaSurucu => $adaSunucu", php, StringComparison.Ordinal);
        Assert.DoesNotContain("'server' => $adaSunucu", php, StringComparison.Ordinal);
    }

    private async Task Install(string componentId, string? admin = null, string password = "fixture-admin-password", string? port = null)
    {
        var configuration = new Dictionary<string, string> { [DatabaseProfiles.GeneratedPasswordKey] = password };
        if (admin is not null) configuration[DatabaseProfiles.AdminUserKey] = admin;
        if (port is not null) configuration[DatabaseProfiles.AdminPortKey] = port;
        db.InstalledComponents.Add(new InstalledComponent { ComponentId = componentId, Status = ComponentInstallStatus.Installed, ProtectedConfigurationJson = protector.Protect(JsonSerializer.Serialize(configuration)) });
        await db.SaveChangesAsync();
    }

    public void Dispose() { db.Dispose(); connection.Dispose(); Directory.Delete(root, true); }
    private sealed class FixtureProtector : ISecretProtector
    {
        public string Protect(string value) => "protected:" + value;
        public string Unprotect(string value) => value.StartsWith("protected:", StringComparison.Ordinal) ? value[10..] : throw new InvalidOperationException("Unprotected fixture");
    }
}
