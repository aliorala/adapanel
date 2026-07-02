using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Enums;
using Npgsql;

namespace AdaPanel.Infrastructure.Database;

internal sealed class PostgreSqlDatabaseProvider : IDatabaseEngineProvider
{
    public DbEngine Engine => DbEngine.PostgreSql;

    public async Task<DatabaseWorkspaceDto> GetWorkspaceAsync(DatabaseConnectionProfile profile, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(profile, cancellationToken);
        var databases = await GetDatabasesAsync(connection, profile.EngineKey, cancellationToken);
        var users = await GetUsersAsync(connection, profile.EngineKey, cancellationToken);

        return new DatabaseWorkspaceDto
        {
            Engine = profile.EngineKey,
            DisplayName = profile.DisplayName,
            ComponentId = profile.ComponentId,
            ServiceName = profile.ServiceName,
            Host = profile.Host,
            Port = profile.Port,
            AdminUserName = profile.AdminUserName,
            AdminPassword = profile.AdminPassword,
            SupportsHostScopedUsers = profile.SupportsHostScopedUsers,
            Databases = databases,
            Users = users
        };
    }

    public async Task CreateDatabaseAsync(DatabaseConnectionProfile profile, string databaseName, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(profile, cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE {QuoteIdentifier(databaseName)};";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.DuplicateDatabase)
        {
            throw new AppException("database.database_already_exists", 409, 409);
        }
        catch (PostgresException exception)
        {
            throw MapUnexpectedPostgresException(exception);
        }
    }

    public async Task DeleteDatabaseAsync(DatabaseConnectionProfile profile, string databaseName, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(profile, cancellationToken);

        try
        {
            await using (var terminateCommand = connection.CreateCommand())
            {
                terminateCommand.CommandText = """
                    SELECT pg_terminate_backend(pid)
                    FROM pg_stat_activity
                    WHERE datname = @databaseName
                      AND pid <> pg_backend_pid();
                    """;
                terminateCommand.Parameters.AddWithValue("@databaseName", databaseName);
                await terminateCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var dropCommand = connection.CreateCommand();
            dropCommand.CommandText = $"DROP DATABASE {QuoteIdentifier(databaseName)};";
            await dropCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InvalidCatalogName)
        {
            throw new AppException("database.database_not_found", 404, 404);
        }
        catch (PostgresException exception)
        {
            throw MapUnexpectedPostgresException(exception);
        }
    }

    public async Task CreateUserAsync(
        DatabaseConnectionProfile profile,
        string userName,
        string password,
        string allowedHost,
        CancellationToken cancellationToken)
    {
        _ = allowedHost;
        await using var connection = await OpenConnectionAsync(profile, cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE ROLE {QuoteIdentifier(userName)} WITH LOGIN PASSWORD @password;";
            command.Parameters.AddWithValue("@password", password);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.DuplicateObject)
        {
            throw new AppException("database.user_already_exists", 409, 409);
        }
        catch (PostgresException exception)
        {
            throw MapUnexpectedPostgresException(exception);
        }
    }

    public async Task SetUserPasswordAsync(DatabaseConnectionProfile profile, string userName, string password, string allowedHost, CancellationToken cancellationToken)
    {
        _ = allowedHost;
        await using var connection = await OpenConnectionAsync(profile, cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"ALTER ROLE {QuoteIdentifier(userName)} WITH LOGIN PASSWORD @password;";
            command.Parameters.AddWithValue("@password", password);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedObject)
        {
            throw new AppException("database.user_not_found", 404, 404);
        }
        catch (PostgresException exception)
        {
            throw MapUnexpectedPostgresException(exception);
        }
    }

    public async Task DeleteUserAsync(DatabaseConnectionProfile profile, string userName, string host, CancellationToken cancellationToken)
    {
        _ = host;
        await using var connection = await OpenConnectionAsync(profile, cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP ROLE {QuoteIdentifier(userName)};";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedObject)
        {
            throw new AppException("database.user_not_found", 404, 404);
        }
        catch (PostgresException exception)
        {
            throw MapUnexpectedPostgresException(exception);
        }
    }

    public async Task GrantAccessAsync(
        DatabaseConnectionProfile profile,
        string databaseName,
        string userName,
        string allowedHost,
        IReadOnlyCollection<string> privileges,
        CancellationToken cancellationToken)
    {
        _ = allowedHost;
        await using var connection = await OpenConnectionAsync(profile, cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"GRANT {BuildPrivilegeClause(privileges)} ON DATABASE {QuoteIdentifier(databaseName)} TO {QuoteIdentifier(userName)};";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InvalidCatalogName)
        {
            throw new AppException("database.database_not_found", 404, 404);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedObject)
        {
            throw new AppException("database.user_not_found", 404, 404);
        }
        catch (PostgresException exception)
        {
            throw MapUnexpectedPostgresException(exception);
        }
    }

    private static async Task<NpgsqlConnection> OpenConnectionAsync(DatabaseConnectionProfile profile, CancellationToken cancellationToken)
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = profile.Host,
                Port = profile.Port,
                Username = profile.AdminUserName,
                Password = profile.AdminPassword,
                Database = "postgres",
                SslMode = SslMode.Disable,
                Timeout = 5,
                CommandTimeout = 5
            };

            var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch (PostgresException exception)
        {
            throw MapUnexpectedPostgresException(exception);
        }
        catch (Exception)
        {
            throw new AppException("database.connection_failed", 503, 503);
        }
    }

    private static async Task<IReadOnlyCollection<DatabaseRecordDto>> GetDatabasesAsync(
        NpgsqlConnection connection,
        string engineKey,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                d.datname,
                pg_catalog.pg_get_userbyid(d.datdba) AS owner_name,
                pg_encoding_to_char(d.encoding) AS encoding_name,
                d.datcollate
            FROM pg_database d
            WHERE d.datistemplate = FALSE
              AND d.datname <> 'postgres'
            ORDER BY d.datname;
            """;

        var results = new List<DatabaseRecordDto>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new DatabaseRecordDto
            {
                Engine = engineKey,
                Name = reader.GetString(0),
                Owner = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                Encoding = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                Collation = reader.IsDBNull(3) ? string.Empty : reader.GetString(3)
            });
        }

        return results;
    }

    private static async Task<IReadOnlyCollection<DatabaseUserDto>> GetUsersAsync(
        NpgsqlConnection connection,
        string engineKey,
        CancellationToken cancellationToken)
    {
        // Sistem rollerini (pg_*) ve süper-kullanıcı 'postgres'u GİZLE (motor admin — silinirse panel DB erişimini kaybeder).
        const string sql = """
            SELECT rolname, rolcanlogin
            FROM pg_roles
            WHERE rolname NOT LIKE 'pg_%'
              AND rolname <> 'postgres'
            ORDER BY rolname;
            """;

        var results = new List<DatabaseUserDto>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new DatabaseUserDto
            {
                Engine = engineKey,
                UserName = reader.GetString(0),
                Host = "*",
                CanLogin = !reader.IsDBNull(1) && reader.GetBoolean(1)
            });
        }

        return results;
    }

    private static string QuoteIdentifier(string value)
    {
        return $"\"{value}\"";
    }

    private static string BuildPrivilegeClause(IReadOnlyCollection<string> privileges)
    {
        if (privileges.Count == 0 || privileges.Contains("ALL PRIVILEGES", StringComparer.OrdinalIgnoreCase))
        {
            return "ALL PRIVILEGES";
        }

        return string.Join(", ", privileges.Select(value =>
            string.Equals(value, "TEMP", StringComparison.OrdinalIgnoreCase) ? "TEMPORARY" : value));
    }

    private static AppException MapUnexpectedPostgresException(PostgresException exception)
    {
        return exception.SqlState == PostgresErrorCodes.InvalidPassword
            ? new AppException("database.connection_failed", 503, 503)
            : new AppException("database.configuration_failed", 500, 500);
    }
}
