using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Enums;
using Microsoft.Data.SqlClient;

namespace AdaPanel.Infrastructure.Database;

internal sealed class MsSqlDatabaseProvider : IDatabaseEngineProvider
{
    public DbEngine Engine => DbEngine.MsSql;

    public async Task<DatabaseWorkspaceDto> GetWorkspaceAsync(
        DatabaseConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(profile, "master", cancellationToken);
        return new DatabaseWorkspaceDto
        {
            Engine = profile.EngineKey,
            DisplayName = profile.DisplayName,
            ComponentId = profile.ComponentId,
            ServiceName = profile.ServiceName,
            Host = profile.Host,
            Port = profile.Port,
            AdminUserName = profile.AdminUserName,
            AdminPassword = string.Empty,
            SupportsHostScopedUsers = false,
            Databases = await GetDatabasesAsync(connection, cancellationToken),
            Users = await GetUsersAsync(connection, cancellationToken)
        };
    }

    public async Task CreateDatabaseAsync(
        DatabaseConnectionProfile profile,
        string databaseName,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(profile, "master", cancellationToken);
        await ExecuteAsync(connection, $"CREATE DATABASE {QuoteIdentifier(databaseName)};", cancellationToken);
    }

    public async Task DeleteDatabaseAsync(
        DatabaseConnectionProfile profile,
        string databaseName,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(profile, "master", cancellationToken);
        await ExecuteAsync(
            connection,
            $"""
             IF DB_ID(N'{EscapeLiteral(databaseName)}') IS NULL
                 THROW 50001, 'Database not found.', 1;
             ALTER DATABASE {QuoteIdentifier(databaseName)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
             DROP DATABASE {QuoteIdentifier(databaseName)};
             """,
            cancellationToken);
    }

    public async Task CreateUserAsync(
        DatabaseConnectionProfile profile,
        string userName,
        string password,
        string allowedHost,
        CancellationToken cancellationToken)
    {
        _ = allowedHost;
        await using var connection = await OpenConnectionAsync(profile, "master", cancellationToken);
        await ExecuteAsync(
            connection,
            $"CREATE LOGIN {QuoteIdentifier(userName)} WITH PASSWORD = N'{EscapeLiteral(password)}', CHECK_POLICY = ON;",
            cancellationToken);
    }

    public async Task SetUserPasswordAsync(DatabaseConnectionProfile profile, string userName, string password, string allowedHost, CancellationToken cancellationToken)
    {
        _ = allowedHost;
        await using var connection = await OpenConnectionAsync(profile, "master", cancellationToken);
        await ExecuteAsync(
            connection,
            $"ALTER LOGIN {QuoteIdentifier(userName)} WITH PASSWORD = N'{EscapeLiteral(password)}';",
            cancellationToken);
    }

    public async Task DeleteUserAsync(DatabaseConnectionProfile profile, string userName, string host, CancellationToken cancellationToken)
    {
        _ = host;
        await using var connection = await OpenConnectionAsync(profile, "master", cancellationToken);
        var sql = $"""
            IF EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'{EscapeLiteral(userName)}')
                DROP LOGIN {QuoteIdentifier(userName)};
            """;
        await ExecuteAsync(connection, sql, cancellationToken);
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
        await using var connection = await OpenConnectionAsync(profile, databaseName, cancellationToken);
        var userIdentifier = QuoteIdentifier(userName);
        var sql = $"""
            IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'{EscapeLiteral(userName)}')
                CREATE USER {userIdentifier} FOR LOGIN {userIdentifier};
            """;

        if (privileges.Count == 0 || privileges.Contains("ALL PRIVILEGES", StringComparer.OrdinalIgnoreCase))
        {
            sql += $"\nALTER ROLE [db_owner] ADD MEMBER {userIdentifier};";
        }
        else
        {
            sql += "\n" + string.Join(
                "\n",
                privileges.Select(privilege => BuildGrant(privilege, userIdentifier)));
        }

        await ExecuteAsync(connection, sql, cancellationToken);
    }

    private static async Task<SqlConnection> OpenConnectionAsync(
        DatabaseConnectionProfile profile,
        string databaseName,
        CancellationToken cancellationToken)
    {
        try
        {
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = profile.Host,
                InitialCatalog = databaseName,
                IntegratedSecurity = string.IsNullOrWhiteSpace(profile.AdminPassword),
                UserID = string.IsNullOrWhiteSpace(profile.AdminPassword) ? string.Empty : profile.AdminUserName,
                Password = profile.AdminPassword,
                Encrypt = false,
                TrustServerCertificate = true,
                ConnectTimeout = 5
            };
            var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch (SqlException)
        {
            throw new AppException("database.connection_failed", 503, 503);
        }
    }

    private static async Task<IReadOnlyCollection<DatabaseRecordDto>> GetDatabasesAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT d.name, SUSER_SNAME(d.owner_sid), d.collation_name
            FROM sys.databases d
            WHERE d.database_id > 4 AND d.state_desc = 'ONLINE'
            ORDER BY d.name;
            """;
        var results = new List<DatabaseRecordDto>();
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new DatabaseRecordDto
            {
                Engine = "mssql",
                Name = reader.GetString(0),
                Owner = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                Encoding = "UTF-16",
                Collation = reader.IsDBNull(2) ? string.Empty : reader.GetString(2)
            });
        }

        return results;
    }

    private static async Task<IReadOnlyCollection<DatabaseUserDto>> GetUsersAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT name, is_disabled
            FROM sys.server_principals
            WHERE type IN ('S', 'U', 'G')
              AND name NOT LIKE '##%'
              AND name NOT LIKE 'NT SERVICE\%'
              AND name NOT LIKE 'NT AUTHORITY\%'
            ORDER BY name;
            """;
        var results = new List<DatabaseUserDto>();
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new DatabaseUserDto
            {
                Engine = "mssql",
                UserName = reader.GetString(0),
                Host = "Windows / SQL Login",
                CanLogin = !reader.GetBoolean(1)
            });
        }

        return results;
    }

    private static async Task ExecuteAsync(
        SqlConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = new SqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException exception) when (exception.Number is 1801)
        {
            throw new AppException("database.database_already_exists", 409, 409);
        }
        catch (SqlException exception) when (exception.Number is 15025)
        {
            throw new AppException("database.user_already_exists", 409, 409);
        }
        catch (SqlException exception) when (exception.Number is 4060 or 911 or 50001)
        {
            throw new AppException("database.database_not_found", 404, 404);
        }
        catch (SqlException)
        {
            throw new AppException("database.configuration_failed", 500, 500);
        }
    }

    private static string BuildGrant(string privilege, string userIdentifier)
    {
        return privilege.ToUpperInvariant() switch
        {
            "CREATE" => $"GRANT CREATE TABLE TO {userIdentifier};",
            "ALTER" => $"GRANT ALTER TO {userIdentifier};",
            _ => $"GRANT {privilege.ToUpperInvariant()} TO {userIdentifier};"
        };
    }

    private static string QuoteIdentifier(string value) => $"[{value.Replace("]", "]]")}]";

    private static string EscapeLiteral(string value) => value.Replace("'", "''");
}
