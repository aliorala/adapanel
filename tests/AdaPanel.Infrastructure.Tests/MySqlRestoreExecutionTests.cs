using System.Text;
using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Database;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

// No database server is involved. The fake CLI captures the exact stdin file and supplies real
// mysql-style diagnostics; persistent recovery state is read back through a new journal instance.
public sealed class MySqlRestoreExecutionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-sql-execution-" + Guid.NewGuid().ToString("N"));
    private readonly JsonFileStore store;
    private const string Sql = "CREATE TABLE t (id INT);\nINSERT INTO t VALUES (1);\n" +
        "/*!50001 CREATE ALGORITHM=UNDEFINED */\n/*!50013 DEFINER=`rsmc_alarmm`@`%` SQL SECURITY DEFINER */\n" +
        "/*!50001 VIEW `v_incidents_full` AS SELECT id FROM t */;\n";

    public MySqlRestoreExecutionTests()
    {
        Directory.CreateDirectory(root);
        store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulImportUsesChosenOwnershipWithoutChangingSourceAndCleansTemporaryCopy(bool adapt)
    {
        var source = Path.Combine(root, "backup.sql");
        await File.WriteAllTextAsync(source, Sql, new UTF8Encoding(true));
        var bytes = await File.ReadAllBytesAsync(source);
        string? supplied = null;
        await MySqlRestoreExecutor.ExecuteAsync(new(store), "mariadb", "alarm", source, adapt, async (path, _) =>
        {
            supplied = path;
            Assert.Equal(adapt ? Sql.Replace("`rsmc_alarmm`@`%`", "CURRENT_USER", StringComparison.Ordinal) : Sql,
                await File.ReadAllTextAsync(path));
            return new CommandResult { ExitCode = 0 };
        }, default);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(source));
        Assert.Equal(!adapt, supplied == source);
        if (adapt) Assert.False(File.Exists(supplied));
        await new MySqlRestoreJournal(store).EnsureCanStartAsync("mariadb", "alarm", default);
    }

    [Fact]
    public async Task PartialImportReportsActualErrorAndFailedViewThenBlocksReplayAcrossNewInstances()
    {
        var source = Path.Combine(root, "backup.sql");
        await File.WriteAllTextAsync(source, Sql);
        string? converted = null;
        var failure = await Assert.ThrowsAsync<AppException>(() => MySqlRestoreExecutor.ExecuteAsync(
            new(store), "mariadb", "alarm", source, true, (path, _) =>
            {
                converted = path;
                return Task.FromResult(new CommandResult { ExitCode = 1, StandardError =
                    "ERROR 1227 (42000) at line 3: Access denied; you need (at least one of) the SET USER privilege(s) for this operation" });
            }, default, engineWide: false));
        Assert.Equal("database.restore_failed", failure.MessageKey);
        Assert.Contains("1227", failure.Details);
        Assert.Contains("SET USER", failure.Details);
        Assert.Contains("CREATE VIEW `v_incidents_full`", failure.Details);
        Assert.Contains("may already be committed", failure.Details);
        Assert.False(File.Exists(converted));
        Assert.Equal(Sql, await File.ReadAllTextAsync(source));

        var calls = 0;
        var replay = await Assert.ThrowsAsync<AppException>(() => MySqlRestoreExecutor.ExecuteAsync(
            new(store), "mariadb", "alarm", source, false, (_, _) =>
            { calls++; return Task.FromResult(new CommandResult()); }, default, engineWide: false));
        Assert.Equal("database.restore_partial_retry_blocked", replay.MessageKey);
        Assert.Equal(0, calls);
        // A different target is available; no automatic clearing or destructive cleanup is done.
        await new MySqlRestoreJournal(store).EnsureCanStartAsync("mariadb", "new_alarm", default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessExceptionOrCancellationRetainsUncertainIntent(bool cancel)
    {
        var source = Path.Combine(root, "backup.sql");
        await File.WriteAllTextAsync(source, Sql);
        var error = await Record.ExceptionAsync(() => MySqlRestoreExecutor.ExecuteAsync(new(store), "mysql", "alarm",
            source, false, (_, _) => cancel ? throw new OperationCanceledException() : throw new IOException("pipe closed"), default));
        if (cancel) Assert.IsType<OperationCanceledException>(error);
        else Assert.Contains("pipe closed", Assert.IsType<AppException>(error).Details);
        await Assert.ThrowsAsync<AppException>(() => new MySqlRestoreJournal(store).EnsureCanStartAsync("mysql", "alarm", default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureAfterSuccessfulSqlDoesNotReleaseReplayGuard(bool cancelled)
    {
        var source = Path.Combine(root, "backup.sql");
        await File.WriteAllTextAsync(source, Sql);
        var error = await Record.ExceptionAsync(() => MySqlRestoreExecutor.ExecuteAsync(new(store), "mysql", "alarm",
            source, false, (_, _) => Task.FromResult(new CommandResult()), default, engineWide: false,
            afterImport: _ => cancelled ? throw new OperationCanceledException() : throw new AppException("database.user_create_cleanup_incomplete")));
        Assert.NotNull(error);
        if (cancelled) Assert.IsType<OperationCanceledException>(error);
        else Assert.Equal("database.user_create_cleanup_incomplete", Assert.IsType<AppException>(error).MessageKey);
        await Assert.ThrowsAsync<AppException>(() => new MySqlRestoreJournal(store).EnsureCanStartAsync("mysql", "alarm", default));
    }

    [Fact]
    public async Task FailedConversionExecutesNothingAndLeavesNoPartialImportIntent()
    {
        var source = Path.Combine(root, "unsupported.sql");
        await File.WriteAllTextAsync(source, "SET sql_mode='NO_BACKSLASH_ESCAPES'; CREATE DEFINER=x@y VIEW v AS SELECT 1;");
        var calls = 0;
        var error = await Assert.ThrowsAsync<AppException>(() => MySqlRestoreExecutor.ExecuteAsync(new(store), "mysql", "alarm",
            source, true, (_, _) => { calls++; return Task.FromResult(new CommandResult()); }, default));
        Assert.Equal("database.definer_conversion_failed", error.MessageKey);
        Assert.Contains("No SQL was executed", error.Details);
        Assert.Equal(0, calls);
        await new MySqlRestoreJournal(store).EnsureCanStartAsync("mysql", "alarm", default);
    }

    [Fact]
    public async Task UnsupportedTargetSessionModeFailsBeforeAnyImportOrJournalIntent()
    {
        var source = Path.Combine(root, "backup.sql");
        await File.WriteAllTextAsync(source, Sql);
        var calls = 0;
        var error = await Assert.ThrowsAsync<AppException>(() => MySqlRestoreExecutor.ExecuteAsync(new(store), "mysql", "alarm",
            source, true, (_, _) => { calls++; return Task.FromResult(new CommandResult()); }, default,
            readInitialSqlMode: _ => Task.FromResult("STRICT_TRANS_TABLES,ANSI_QUOTES")));
        Assert.Equal("database.definer_conversion_failed", error.MessageKey);
        Assert.Equal(0, calls);
        Assert.Equal(Sql, await File.ReadAllTextAsync(source));
        await new MySqlRestoreJournal(store).EnsureCanStartAsync("mysql", "alarm", default);
    }

    [Fact]
    public async Task PreserveOwnersDoesNotNeedSessionInspectionOrConversion()
    {
        var source = Path.Combine(root, "backup.sql");
        await File.WriteAllTextAsync(source, Sql);
        await MySqlRestoreExecutor.ExecuteAsync(new(store), "mysql", "alarm", source, false,
            (path, _) => { Assert.Equal(source, path); return Task.FromResult(new CommandResult()); }, default,
            readInitialSqlMode: _ => throw new InvalidOperationException("Must not inspect modes in preserve mode."));
    }

    [Fact]
    public async Task PendingWholeEngineAndNamedAttemptsCannotBypassEachOther()
    {
        var journal = new MySqlRestoreJournal(store);
        var id = await journal.BeginAsync("mysql", "Alarm", default);
        await Assert.ThrowsAsync<AppException>(() => journal.BeginAsync("MYSQL", "alarm", default));
        await Assert.ThrowsAsync<AppException>(() => journal.BeginAsync("mysql", "", default));
        await journal.CompleteAsync(id, default);
        await journal.BeginAsync("mysql", "", default);
        await Assert.ThrowsAsync<AppException>(() => journal.BeginAsync("mysql", "other", default));
    }

    [Fact]
    public async Task ArbitraryDumpCannotBypassPartialGuardByChangingInitialTargetDatabase()
    {
        var source = Path.Combine(root, "switches-database.sql");
        await File.WriteAllTextAsync(source, "USE alarm; INSERT INTO t VALUES (1); SELECT broken;");
        await Assert.ThrowsAsync<AppException>(() => MySqlRestoreExecutor.ExecuteAsync(new(store), "mysql", "alarm", source,
            false, (_, _) => Task.FromResult(new CommandResult { ExitCode = 1, StandardError = "ERROR 1064 at line 1" }), default));
        var calls = 0;
        await Assert.ThrowsAsync<AppException>(() => MySqlRestoreExecutor.ExecuteAsync(new(store), "mysql", "different", source,
            true, (_, _) => { calls++; return Task.FromResult(new CommandResult()); }, default));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task OnlyVerifiedNewDatabaseClearsThatTargetAndNotOtherUncertainImports()
    {
        var journal = new MySqlRestoreJournal(store);
        await journal.BeginAsync("mysql", "alarm", default);
        await journal.BeginAsync("mysql", "other", default);
        await journal.ClearForNewDatabaseAsync("mysql", "alarm", default);
        await journal.EnsureCanStartAsync("mysql", "alarm", default);
        await Assert.ThrowsAsync<AppException>(() => journal.EnsureCanStartAsync("mysql", "other", default));
    }

    [Fact]
    public async Task UnreadableRecoveryStateFailsBeforeExecution()
    {
        await File.WriteAllTextAsync(store.ResolvePath("broken.json"), "{");
        Directory.CreateDirectory(Path.GetDirectoryName(store.ResolvePath(MySqlRestoreJournal.StatePath))!);
        File.Move(store.ResolvePath("broken.json"), store.ResolvePath(MySqlRestoreJournal.StatePath));
        var source = Path.Combine(root, "backup.sql");
        await File.WriteAllTextAsync(source, Sql);
        var calls = 0;
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => MySqlRestoreExecutor.ExecuteAsync(new(store), "mysql", "alarm",
            source, false, (_, _) => { calls++; return Task.FromResult(new CommandResult()); }, default));
        Assert.Equal(0, calls);
    }

    public void Dispose()
    {
        var resolved = Path.GetFullPath(root);
        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "adapanel-sql-execution-"), resolved, StringComparison.OrdinalIgnoreCase);
        Directory.Delete(resolved, recursive: true);
    }
}
