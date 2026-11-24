using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Database;

/// <summary>Shared restore/transfer execution; conversion finishes before the first SQL is sent.</summary>
internal static partial class MySqlRestoreExecutor
{
    internal static async Task ExecuteAsync(
        MySqlRestoreJournal journal, string engine, string database, string source, bool adaptDefiners,
        Func<string, CancellationToken, Task<CommandResult>> execute,
        CancellationToken ct, bool engineWide = true, Func<CancellationToken, Task>? afterImport = null,
        Func<CancellationToken, Task<string>>? readInitialSqlMode = null)
    {
        // An arbitrary dump may USE another database (including --all-databases backups).
        // Its requested initial database is not its write scope. Only controlled single-DB
        // transfer dumps without --databases may opt into a narrower recovery scope.
        var scope = engineWide ? string.Empty : database;
        await journal.EnsureCanStartAsync(engine, scope, ct);
        var converted = adaptDefiners ? Path.Combine(Path.GetTempPath(), $"adapanel-definer-{Guid.NewGuid():N}.sql") : null;
        try
        {
            if (converted is not null)
            {
                try
                {
                    var mode = readInitialSqlMode is null ? null : await readInitialSqlMode(ct);
                    await MySqlDefinerRewriter.RewriteAsync(source, converted, ct, mode);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) when (error is InvalidDataException or NotSupportedException or System.Text.DecoderFallbackException)
                {
                    throw new AppException("database.definer_conversion_failed", 422, 422,
                        "No SQL was executed. " + error.Message);
                }
            }

            var input = converted ?? source;
            var attemptId = await journal.BeginAsync(engine, scope, ct);
            // Any exception, process termination or failed publication retains the durable intent.
            // Conservatively treat an unknown outcome as partial, even if the failure was at login.
            CommandResult result;
            try { result = await execute(input, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                throw new AppException("database.restore_failed", 500, 500,
                    "Import completion is unconfirmed; partial changes may remain. " + error.Message +
                    " Do not replay the full dump; inspect the database before recovering only the failed object.");
            }
            if (!result.Success)
                throw new AppException("database.restore_failed", 500, 500, await DescribeFailureAsync(input, result, ct));
            if (afterImport is not null) await afterImport(ct);
            ct.ThrowIfCancellationRequested();
            using var publication = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await journal.CompleteAsync(attemptId, publication.Token);
        }
        finally
        {
            if (converted is not null)
            {
                try { File.Delete(converted); }
                catch (IOException) { /* Do not hide the original SQL failure. */ }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    internal static async Task<string> DescribeFailureAsync(string input, CommandResult result, CancellationToken ct)
    {
        // mysql --batch stops on the first SQL error. It reports that statement's starting line.
        var error = result.StandardError ?? string.Empty;
        var match = ErrorLine().Match(error);
        var context = string.Empty;
        if (match.Success && long.TryParse(match.Groups[1].Value, out var line))
        {
            try { context = await MySqlDefinerRewriter.FindObjectAtLineAsync(input, line, ct) ?? "SQL object unknown"; }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { context = "Object could not be identified; use the SQL line reported below."; }
        }
        // Diagnostics can contain a quoted data value. Central AppException redaction still applies.
        if (error.Length > 8192) error = error[..8192] + " [truncated]";
        return $"Import incomplete; changes before the error may already be committed. ExitCode={result.ExitCode}. " +
            $"{context}\n{error}\nDo not replay the full dump: it may DROP existing tables or repeat INSERT data. " +
            "Inspect the indicated object and restore that definition only. " +
            "A dump containing USE may affect multiple databases; full replay remains blocked pending recovery review.";
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\bat line\s+(\d+)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex ErrorLine();
}
