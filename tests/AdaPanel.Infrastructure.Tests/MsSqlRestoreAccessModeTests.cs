using AdaPanel.Core.Enums;
using AdaPanel.Infrastructure.Database;
using Microsoft.Data.SqlClient;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class MsSqlRestoreAccessModeTests
{
    [Theory]
    [InlineData("MULTI_USER")]
    [InlineData("RESTRICTED_USER")]
    [InlineData("SINGLE_USER")]
    [InlineData(null)]
    public async Task Successful_restore_preserves_previous_access_and_verifies_online(string? previous)
    {
        var sql = new SqlFixture(previous);
        var result = await Import(sql);
        Assert.True(result.Success);
        Assert.Equal(previous ?? "MULTI_USER", sql.Mode);
        Assert.Contains(sql.Commands, command => command.StartsWith("RESTORE DATABASE [target]", StringComparison.Ordinal));
        Assert.True(sql.StateReads > 0);
        if (previous is "MULTI_USER" or "RESTRICTED_USER")
            Assert.True(sql.Commands.IndexOf("ALTER DATABASE [target] SET SINGLE_USER WITH ROLLBACK IMMEDIATE")
                < sql.Commands.FindIndex(command => command.StartsWith("RESTORE DATABASE", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("MULTI_USER")]
    [InlineData("RESTRICTED_USER")]
    [InlineData("SINGLE_USER")]
    public async Task Restore_error_preserves_original_exception_and_prior_access(string previous)
    {
        var original = new IOException("ORIGINAL_RESTORE_FAILURE");
        var sql = new SqlFixture(previous) { RestoreFailure = original };
        var actual = await Assert.ThrowsAsync<IOException>(() => Import(sql));
        Assert.Same(original, actual);
        Assert.Equal(previous, sql.Mode);
        Assert.True(sql.StateReads > 0);
    }

    [Fact]
    public async Task Partial_single_user_transition_is_reconciled_even_if_its_response_failed()
    {
        var original = new IOException("ALTER_RESPONSE_LOST");
        var sql = new SqlFixture("RESTRICTED_USER") { EnterFailure = original };
        Assert.Same(original, await Assert.ThrowsAsync<IOException>(() => Import(sql)));
        Assert.Equal("RESTRICTED_USER", sql.Mode);
        Assert.DoesNotContain(sql.Commands, command => command.StartsWith("RESTORE DATABASE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancellation_is_preserved_and_cleanup_uses_uncancelled_token()
    {
        using var cts = new CancellationTokenSource();
        var original = new OperationCanceledException(cts.Token);
        var sql = new SqlFixture("RESTRICTED_USER") { RestoreFailure = original, Cancel = cts };
        Assert.Same(original, await Assert.ThrowsAsync<OperationCanceledException>(() => Import(sql, cts.Token)));
        Assert.Equal("RESTRICTED_USER", sql.Mode);
        Assert.True(sql.StateReads > 0);
    }

    [Fact]
    public async Task Cleanup_failure_cannot_replace_original_restore_error()
    {
        var original = new IOException("ORIGINAL_RESTORE_FAILURE");
        var sql = new SqlFixture("MULTI_USER") { RestoreFailure = original, CleanupFailure = new IOException("CLEANUP_FAILED") };
        var actual = await Assert.ThrowsAsync<IOException>(() => Import(sql));
        Assert.Same(original, actual);
        Assert.Contains("CLEANUP_FAILED", actual.Data["MsSqlAccessModeCleanupFailure"]?.ToString());
    }

    [Fact]
    public async Task Cleanup_failure_cannot_replace_cancellation()
    {
        var original = new OperationCanceledException();
        var sql = new SqlFixture("MULTI_USER") { RestoreFailure = original, CleanupFailure = new IOException("CLEANUP_FAILED") };
        Assert.Same(original, await Assert.ThrowsAsync<OperationCanceledException>(() => Import(sql)));
        Assert.Contains("CLEANUP_FAILED", original.Data["MsSqlAccessModeCleanupFailure"]?.ToString());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("offline")]
    [InlineData("ignored-alter")]
    [InlineData("cleanup-error")]
    public async Task Unverified_missing_or_offline_database_never_returns_success(string scenario)
    {
        var sql = new SqlFixture("MULTI_USER") { MissingAfterRestore = scenario == "missing", Online = scenario != "offline",
            IgnoreCleanupAlter = scenario == "ignored-alter", CleanupFailure = scenario == "cleanup-error" ? new IOException("CLEANUP_FAILED") : null };
        var result = await Import(sql);
        Assert.False(result.Success);
        Assert.NotEmpty(result.StandardError);
    }

    [Fact]
    public async Task Unknown_initial_mode_never_changes_database()
    {
        var sql = new SqlFixture("UNRECOGNIZED");
        await Assert.ThrowsAsync<InvalidDataException>(() => Import(sql));
        Assert.Single(sql.Commands);
    }

    private static Task<AdaPanel.Infrastructure.Support.CommandResult> Import(SqlFixture sql, CancellationToken token = default)
        => DatabaseService.ImportMsSqlAsync(new DatabaseConnectionProfile(DbEngine.MsSql, "mssql", "SQL Server", "mssql",
            "MSSQLSERVER", "synthetic.invalid", 1433, "Windows", "", false, ""), "target", "C:\\synthetic\\archive.bak", token, sql.Execute);

    private sealed class SqlFixture(string? previous)
    {
        public string? Mode { get; private set; } = previous;
        private string? InitialMode { get; } = previous;
        public List<string> Commands { get; } = [];
        public Exception? RestoreFailure { get; init; }
        public Exception? EnterFailure { get; init; }
        public Exception? CleanupFailure { get; init; }
        public CancellationTokenSource? Cancel { get; init; }
        public bool MissingAfterRestore { get; init; }
        public bool Online { get; init; } = true;
        public bool IgnoreCleanupAlter { get; init; }
        public int StateReads { get; private set; }
        private bool entered;

        public Task<object?> Execute(string connectionString, string command, IReadOnlyDictionary<string, object> values, bool scalar, CancellationToken token)
        {
            Assert.Equal("master", new SqlConnectionStringBuilder(connectionString).InitialCatalog);
            Assert.Equal("synthetic.invalid", new SqlConnectionStringBuilder(connectionString).DataSource);
            Commands.Add(command);
            if (command.StartsWith("SELECT user_access_desc", StringComparison.Ordinal))
            {
                Assert.True(scalar);
                Assert.Equal("target", values["@database"]);
                return Task.FromResult<object?>(InitialMode);
            }
            if (command.StartsWith("SELECT CONCAT", StringComparison.Ordinal))
            {
                Assert.True(scalar);
                Assert.False(token.CanBeCanceled);
                StateReads++;
                return Task.FromResult<object?>(MissingAfterRestore ? null : Mode is null ? null : Mode + "|" + (Online ? "ONLINE" : "OFFLINE"));
            }
            Assert.False(scalar);
            if (command.StartsWith("RESTORE DATABASE", StringComparison.Ordinal))
            {
                Assert.Equal("C:\\synthetic\\archive.bak", values["@path"]);
                Assert.DoesNotContain("archive.bak", command);
                Cancel?.Cancel();
                if (RestoreFailure is not null) throw RestoreFailure;
                Mode = "SINGLE_USER";
                return Task.FromResult<object?>(null);
            }
            if (!entered && command.Contains(" SET SINGLE_USER ", StringComparison.Ordinal))
            {
                entered = true;
                Mode = "SINGLE_USER";
                if (EnterFailure is not null) throw EnterFailure;
            }
            else
            {
                Assert.False(token.CanBeCanceled);
                if (CleanupFailure is not null) throw CleanupFailure;
                if (!IgnoreCleanupAlter) Mode = command.Split(" SET ", StringSplitOptions.None)[1].Split(' ')[0];
            }
            return Task.FromResult<object?>(null);
        }
    }
}
