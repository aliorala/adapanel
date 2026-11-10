using System.Security.Cryptography;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;

namespace AdaPanel.Infrastructure.Database;

internal sealed partial class DatabaseService : IMigrationDatabaseImporter
{
    internal IMigrationMySqlClient MigrationClient { get; set; } = new MigrationMySqlClient();
    private static readonly string[] MigrationPrivileges =
        ["SELECT", "INSERT", "UPDATE", "DELETE", "CREATE", "DROP", "ALTER", "INDEX", "REFERENCES", "CREATE VIEW", "SHOW VIEW", "TRIGGER", "CREATE ROUTINE", "ALTER ROUTINE", "EXECUTE", "EVENT", "LOCK TABLES", "CREATE TEMPORARY TABLES"];

    public async Task<MigrationDatabasePlan> PlanAsync(MigrationDatabaseImportRequest request, CancellationToken cancellationToken = default)
    {
        var engine = ParseEngine(request.Engine);
        if (engine is not (DbEngine.MySql or DbEngine.MariaDb))
            return new() { Supported = false, Blockers = ["migration.database_engine_unsupported"] };
        var database = NormalizeDatabaseName(request.TargetDatabase);
        var user = NormalizeUserName(request.TargetUserName, engine);
        // Planning uses manifest metadata; large immutable payloads are copied only on apply.
        // ImportAsync below always requires an existing, protected local dump.
        if (!string.IsNullOrEmpty(request.DumpPath)) ValidateMigrationDump(request.DumpPath);
        var profile = await ResolveConnectionProfileAsync(engine, cancellationToken);
        var workspace = await ResolveProvider(engine).GetWorkspaceAsync(profile, cancellationToken);
        return MigrationPlan(workspace, database, user);
    }

    private static MigrationDatabasePlan MigrationPlan(DatabaseWorkspaceDto workspace, string database, string user)
    {
        var blockers = new List<string>();
        // Conservative case-insensitive collision detection also covers case-sensitive servers.
        if (workspace.Databases.Any(item => string.Equals(item.Name, database, StringComparison.OrdinalIgnoreCase)))
            blockers.Add("migration.target_database_exists");
        if (workspace.Users.Any(item => string.Equals(item.UserName, user, StringComparison.OrdinalIgnoreCase)))
            blockers.Add("migration.target_user_exists");
        return new() { Supported = true, Blockers = blockers };
    }

    public async Task<MigrationDatabaseImportResult> ImportAsync(MigrationDatabaseImportRequest request, CancellationToken cancellationToken = default)
    {
        var resources = new List<string>();
        var attempted = new List<string>();
        MigrationDatabaseCredentials? credentials = null;
        string? transformed = null;
        var imported = false;
        try
        {
            var engine = ParseEngine(request.Engine);
            if (engine is not (DbEngine.MySql or DbEngine.MariaDb))
                return new() { ErrorCode = "migration.database_engine_unsupported" };
            var database = NormalizeDatabaseName(request.TargetDatabase);
            var user = NormalizeUserName(request.TargetUserName, engine);
            ValidateMigrationDump(request.DumpPath);
            var password = string.IsNullOrEmpty(request.TargetPassword)
                ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(36)) + "!aA1" : request.TargetPassword;
            using var mutation = await DatabaseLifecycle.EnterResourceAsync(request.Engine, database, cancellationToken);
            using var account = await DatabaseLifecycle.EnterAccountAsync(mutation, request.Engine, user, cancellationToken);
            var profile = await ResolveConnectionProfileAsync(engine, cancellationToken);
            var provider = ResolveProvider(engine);
            var plan = MigrationPlan(await provider.GetWorkspaceAsync(profile, cancellationToken), database, user);
            if (!plan.CanImport) return new() { ErrorCode = plan.Blockers[0] };

            // Native CREATE (without IF NOT EXISTS) is the final atomic guard against external writers.
            // Keep the new account unprivileged until strict CREATE DATABASE has succeeded.
            credentials = new() { UserName = user, Password = password, Database = database };
            attempted.Add("user:" + user + "@localhost");
            await provider.CreateUserAsync(profile, user, password, "localhost", cancellationToken);
            resources.Add("user:" + user + "@localhost");
            await credentialStore.RecordCreatedAsync(profile.EngineKey, user, "localhost", database, password, cancellationToken);
            attempted.Add("database:" + database);
            await provider.CreateDatabaseAsync(profile, database, cancellationToken);
            resources.Add("database:" + database);
            await provider.GrantAccessAsync(profile, database, user, "localhost", MigrationPrivileges, cancellationToken);
            await credentialStore.ConfirmReferenceAsync(profile.EngineKey, user, "localhost", database, cancellationToken);
            var scoped = profile with { AdminUserName = user, AdminPassword = password };
            var source = request.DumpPath;
            if (request.AdaptDefiners)
            {
                var mode = await MigrationClient.ReadSqlModeAsync(scoped, database, cancellationToken);
                transformed = AdaPanel.Infrastructure.Support.SecureTempFile.BuildPath("adapanel-migration", ".sql");
                await MySqlDefinerRewriter.RewriteAsync(source, transformed, cancellationToken, mode, secureOutput: true);
                source = transformed;
            }
            // The ONLY dump execution uses the newly-created account; no administrative fallback/retry.
            var exitCode = await MigrationClient.ImportAsync(FindExecutable(profile.InstallDir, "mysql.exe"), scoped, database, source, cancellationToken);
            if (exitCode != 0) return Result("migration.database_import_failed");
            imported = true;
            var inventory = await MigrationClient.VerifyAsync(scoped, database, cancellationToken);
            return new() { Imported = true, Verified = true, CreatedResources = resources.ToArray(), AttemptedResources = attempted.ToArray(), ObjectInventory = inventory, Credentials = credentials };
        }
        catch (OperationCanceledException) { return Result("migration.database_import_cancelled", cancelled: true); }
        catch (AppException exception) { return Result(exception.MessageKey); }
        catch (Exception) { return Result("migration.database_import_failed"); }
        finally
        {
            if (transformed != null)
            {
                try { File.Delete(transformed); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        MigrationDatabaseImportResult Result(string code, bool cancelled = false) => new()
        {
            Imported = imported, Cancelled = cancelled, ErrorCode = code,
            PartialStatePossible = attempted.Count > 0, AttemptedResources = attempted.ToArray(),
            CreatedResources = resources.ToArray(), Credentials = credentials
        };
    }

    private static void ValidateMigrationDump(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path) ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new AppException("migration.database_dump_invalid", 400, 400);
    }
}
