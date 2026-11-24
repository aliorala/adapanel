using AdaPanel.Core.Entities;
using AdaPanel.Data.Extensions;
using AdaPanel.Data.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class SqliteConnectionIsolationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("Data Source=%ProgramData%\\AdaPanel\\data\\adapanel.db")]
    public void Default_database_uses_custom_data_root_without_pooling(string? configured)
    {
        using var fixture = new DatabaseFixture(configured, useDefaultPath: true);
        using var scope = fixture.Provider.CreateScope();
        var connection = scope.ServiceProvider.GetRequiredService<AdaPanelDbContext>().Database.GetDbConnection();
        var options = new SqliteConnectionStringBuilder(connection.ConnectionString);
        Assert.Equal(Path.Combine(fixture.Root, "data", "adapanel.db"), options.DataSource);
        Assert.False(options.Pooling);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public void Explicit_connection_preserves_all_flags_except_forced_pooling(bool? pooling)
    {
        using var fixture = new DatabaseFixture();
        var expected = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(fixture.Root, "explicit.db"), Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private, DefaultTimeout = 17, ForeignKeys = false,
            RecursiveTriggers = true, Password = "test-only-not-a-real-secret"
        };
        if (pooling.HasValue) expected.Pooling = pooling.Value;
        using var provider = CreateProvider(fixture.Root, expected.ToString());
        using var scope = provider.CreateScope();
        var connection = scope.ServiceProvider.GetRequiredService<AdaPanelDbContext>().Database.GetDbConnection();
        var actual = new SqliteConnectionStringBuilder(connection.ConnectionString);
        expected.Pooling = false;
        Assert.False(actual.Pooling);
        Assert.Equal(expected.ToString(), actual.ToString());
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task Concurrent_scopes_hold_distinct_native_handles_and_read_the_same_file()
    {
        using var fixture = new DatabaseFixture();
        await fixture.InitializeAsync();
        var scopes = Enumerable.Range(0, 8).Select(_ => fixture.Provider.CreateAsyncScope()).ToArray();
        try
        {
            var contexts = scopes.Select(scope => scope.ServiceProvider.GetRequiredService<AdaPanelDbContext>()).ToArray();
            await Task.WhenAll(contexts.Select(context => Task.Run(() => context.Database.OpenConnectionAsync())));
            var connections = contexts.Select(context => (SqliteConnection)context.Database.GetDbConnection()).ToArray();
            Assert.Equal(connections.Length, connections.Select(connection => connection.Handle!.DangerousGetHandle()).Distinct().Count());
            var values = await Task.WhenAll(contexts.Select(context => context.Settings.AsNoTracking()
                .Where(setting => setting.Key == "probe").Select(setting => setting.Value).SingleAsync()));
            Assert.All(values, value => Assert.Equal("original value: boşluk  ", value));
        }
        finally
        {
            foreach (var scope in scopes) await scope.DisposeAsync();
        }
    }

    [Fact]
    public async Task Rolled_back_write_preserves_committed_value_after_scope_reopens()
    {
        using var fixture = new DatabaseFixture();
        await fixture.InitializeAsync();
        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AdaPanelDbContext>();
            await using var transaction = await context.Database.BeginTransactionAsync();
            var setting = await context.Settings.SingleAsync(setting => setting.Key == "probe");
            setting.Value = "uncommitted";
            await context.SaveChangesAsync();
            Assert.Equal("uncommitted", await context.Settings.AsNoTracking().Select(setting => setting.Value).SingleAsync());
            await transaction.RollbackAsync();
        }
        await using var reopened = fixture.Provider.CreateAsyncScope();
        var reader = reopened.ServiceProvider.GetRequiredService<AdaPanelDbContext>();
        Assert.Equal("original value: boşluk  ", await reader.Settings.AsNoTracking().Select(setting => setting.Value).SingleAsync());
    }

    private static ServiceProvider CreateProvider(string root, string? connectionString)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Panel:DataRoot"] = root, ["ConnectionStrings:AdaPanel"] = connectionString
        }).Build();
        var services = new ServiceCollection();
        services.AddAdaPanelData(configuration);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class DatabaseFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"adapanel-sqlite-isolation-{Guid.NewGuid():N}");
        public ServiceProvider Provider { get; }
        public DatabaseFixture(string? configured = null, bool useDefaultPath = false)
        {
            Directory.CreateDirectory(Root);
            Provider = CreateProvider(Root, useDefaultPath ? configured : $"Data Source={Path.Combine(Root, "panel.db")};Pooling=True");
        }
        public async Task InitializeAsync()
        {
            await using var scope = Provider.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<AdaPanelDbContext>();
            await context.Database.EnsureCreatedAsync();
            context.Settings.Add(new PanelSetting { Key = "probe", Value = "original value: boşluk  " });
            await context.SaveChangesAsync();
        }
        public void Dispose()
        {
            Provider.Dispose();
            // Only this fixture's generated temporary directory is removed.
            Directory.Delete(Root, recursive: true);
        }
    }
}
