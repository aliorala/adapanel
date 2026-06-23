using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Enums;
using MySqlConnector;

namespace AdaPanel.Infrastructure.Database;

internal sealed class MySqlDatabaseProvider : IDatabaseEngineProvider
{
    public DbEngine Engine => DbEngine.MySql;

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
        catch (MySqlException exception) when (exception.Number == 1007)
        {
            throw new AppException("database.database_already_exists", 409, 409);
        }
        catch (MySqlException exception)
        {
            throw MapUnexpectedMySqlException(exception);
        }
    }

    public async Task DeleteDatabaseAsync(DatabaseConnectionProfile profile, string databaseName, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(profile, cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP DATABASE {QuoteIdentifier(databaseName)};";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (MySqlException exception) when (exception.Number is 1008 or 1049)
        {
            throw new AppException("database.database_not_found", 404, 404);
        }
        catch (MySqlException exception)
        {
            throw MapUnexpectedMySqlException(exception);
        }
    }

    public async Task CreateUserAsync(
        DatabaseConnectionProfile profile,
        string userName,
        string password,
        string allowedHost,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(profile, cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE USER '{userName}'@'{allowedHost}' IDENTIFIED BY @password;";
            command.Parameters.AddWithValue("@password", password);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (MySqlException exception) when (exception.Number == 1396)
        {
            throw new AppException("database.user_already_exists", 409, 409);
        }
        catch (MySqlException exception)
        {
            throw MapUnexpectedMySqlException(exception);
        }
    }

    public async Task SetUserPasswordAsync(DatabaseConnectionProfile profile, string userName, string password, string allowedHost, CancellationToken cancellationToken)
    {
        var host = string.IsNullOrWhiteSpace(allowedHost) ? "%" : allowedHost;
        await using var connection = await OpenConnectionAsync(profile, cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"ALTER USER '{userName}'@'{host}' IDENTIFIED BY @password;";
            command.Parameters.AddWithValue("@password", password);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (MySqlException exception) when (exception.Number is 1396 or 1133)
        {
            throw new AppException("database.user_not_found", 404, 404);
        }
        catch (MySqlException exception)
        {
            throw MapUnexpectedMySqlException(exception);
        }
    }

    public async Task DeleteUserAsync(DatabaseConnectionProfile profile, string userName, string host, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(profile, cancellationToken);

        try
        {
            var allowedHost = string.IsNullOrWhiteSpace(host) ? "%" : host;
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP USER '{userName}'@'{allowedHost}'; FLUSH PRIVILEGES;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (MySqlException exception) when (exception.Number is 1396)
        {
            throw new AppException("database.user_not_found", 404, 404);
        }
        catch (MySqlException exception)
        {
            throw MapUnexpectedMySqlException(exception);
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
        await using var connection = await OpenConnectionAsync(profile, cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                GRANT {BuildPrivilegeClause(privileges)} ON {QuoteIdentifier(databaseName)}.* TO '{userName}'@'{allowedHost}';
                FLUSH PRIVILEGES;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (MySqlException exception) when (exception.Number is 1049)
        {
            throw new AppException("database.database_not_found", 404, 404);
        }
        catch (MySqlException exception) when (exception.Number is 1133 or 1141 or 1396)
        {
            throw new AppException("database.user_not_found", 404, 404);
        }
        catch (MySqlException exception)
        {
            throw MapUnexpectedMySqlException(exception);
        }
    }

    // K2 (mysql_secure_installation eşdeğeri, GÜVENLİ): anonim kullanıcıları ve uzak root@'%' hesabını
    // kaldır. root@localhost ve loopback'e DOKUNMA — panel onlarla bağlanıyor, kilitlenme olmaz. İdempotent.
    public async Task<IReadOnlyList<string>> HardenAdminAccountsAsync(DatabaseConnectionProfile profile, CancellationToken cancellationToken)
    {
        var removed = new List<string>();
        await using var connection = await OpenConnectionAsync(profile, cancellationToken);

        var targets = new List<(string User, string Host)>();
        await using (var query = connection.CreateCommand())
        {
            // Güvensiz hesaplar: anonim (User='') veya uzak root (root@'%'). localhost/loopback root KORUNUR.
            query.CommandText = "SELECT User, Host FROM mysql.user WHERE User = '' OR (User = 'root' AND Host = '%');";
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                targets.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        foreach (var (user, host) in targets)
        {
            try
            {
                await using var drop = connection.CreateCommand();
                drop.CommandText = $"DROP USER IF EXISTS '{user.Replace("'", "''")}'@'{host.Replace("'", "''")}';";
                await drop.ExecuteNonQueryAsync(cancellationToken);
                removed.Add(string.IsNullOrEmpty(user) ? $"(anonim)@{host}" : $"{user}@{host}");
            }
            catch (MySqlException)
            {
                // en iyi çaba — tek hesabın hatası diğerlerini engellemesin
            }
        }

        if (removed.Count > 0)
        {
            await using var flush = connection.CreateCommand();
            flush.CommandText = "FLUSH PRIVILEGES;";
            await flush.ExecuteNonQueryAsync(cancellationToken);
        }

        return removed;
    }

    private static async Task<MySqlConnection> OpenConnectionAsync(DatabaseConnectionProfile profile, CancellationToken cancellationToken)
    {
        try
        {
            var builder = new MySqlConnectionStringBuilder
            {
                Server = profile.Host,
                Port = Convert.ToUInt32(profile.Port),
                UserID = profile.AdminUserName,
                Password = profile.AdminPassword,
                Database = "mysql",
                SslMode = MySqlSslMode.None,
                ConnectionTimeout = 5,
                DefaultCommandTimeout = 5
            };

            var connection = new MySqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch (MySqlException exception)
        {
            throw MapUnexpectedMySqlException(exception);
        }
        catch (Exception)
        {
            throw new AppException("database.connection_failed", 503, 503);
        }
    }

    private static async Task<IReadOnlyCollection<DatabaseRecordDto>> GetDatabasesAsync(
        MySqlConnection connection,
        string engineKey,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                SCHEMA_NAME,
                DEFAULT_CHARACTER_SET_NAME,
                DEFAULT_COLLATION_NAME
            FROM INFORMATION_SCHEMA.SCHEMATA
            WHERE SCHEMA_NAME NOT IN
                ('information_schema', 'performance_schema', 'mysql', 'sys', 'phpmyadmin')
            ORDER BY SCHEMA_NAME;
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
                Owner = string.Empty,
                Encoding = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                Collation = reader.IsDBNull(2) ? string.Empty : reader.GetString(2)
            });
        }

        return results;
    }

    private static async Task<IReadOnlyCollection<DatabaseUserDto>> GetUsersAsync(
        MySqlConnection connection,
        string engineKey,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT User, Host
            FROM mysql.user
            WHERE User <> ''
            ORDER BY User, Host;
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
                Host = reader.GetString(1),
                CanLogin = true
            });
        }

        return results;
    }

    private static string QuoteIdentifier(string value)
    {
        return $"`{value}`";
    }

    private static string BuildPrivilegeClause(IReadOnlyCollection<string> privileges)
    {
        return privileges.Count == 0 || privileges.Contains("ALL PRIVILEGES", StringComparer.OrdinalIgnoreCase)
            ? "ALL PRIVILEGES"
            : string.Join(", ", privileges);
    }

    private static AppException MapUnexpectedMySqlException(MySqlException exception)
    {
        return exception.Number == 1045
            ? new AppException("database.connection_failed", 503, 503)
            : new AppException("database.configuration_failed", 500, 500);
    }
}
