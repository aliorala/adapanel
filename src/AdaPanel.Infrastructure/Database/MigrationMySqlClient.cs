using AdaPanel.Infrastructure.Support;
using MySqlConnector;

namespace AdaPanel.Infrastructure.Database;

internal interface IMigrationMySqlClient
{
    Task<string> ReadSqlModeAsync(DatabaseConnectionProfile profile, string database, CancellationToken cancellationToken);
    Task<int> ImportAsync(string executable, DatabaseConnectionProfile profile, string database, string dump, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> VerifyAsync(DatabaseConnectionProfile profile, string database, CancellationToken cancellationToken);
}

internal sealed class MigrationMySqlClient : IMigrationMySqlClient
{
    internal static string Arguments(DatabaseConnectionProfile profile, string database) =>
        $"--no-defaults --protocol=TCP --host={profile.Host} --port={profile.Port} --user=\"{profile.AdminUserName}\" --binary-mode --local-infile=0 --batch --skip-reconnect --database=\"{database}\"";

    public async Task<int> ImportAsync(string executable, DatabaseConnectionProfile profile, string database, string dump, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(executable, Arguments(profile, database), cancellationToken: cancellationToken,
            environmentVariables: new Dictionary<string, string> { ["MYSQL_PWD"] = profile.AdminPassword }, standardInputFilePath: dump);
        // Native output may contain source SQL and secrets. Never return/log it.
        return result.ExitCode;
    }

    private static async Task<MySqlConnection> OpenAsync(DatabaseConnectionProfile profile, string database, CancellationToken cancellationToken)
    {
        var connection = new MySqlConnection(new MySqlConnectionStringBuilder
        {
            Server = profile.Host, Port = (uint)profile.Port, UserID = profile.AdminUserName,
            Password = profile.AdminPassword, Database = database, Pooling = false,
            AllowLoadLocalInfile = false, ConnectionTimeout = 10, DefaultCommandTimeout = 30
        }.ConnectionString);
        try { await connection.OpenAsync(cancellationToken); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async Task<string> ReadSqlModeAsync(DatabaseConnectionProfile profile, string database, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(profile, database, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT @@SESSION.sql_mode;";
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken)) ?? string.Empty;
    }

    public async Task<IReadOnlyList<string>> VerifyAsync(DatabaseConnectionProfile profile, string database, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(profile, database, cancellationToken);
        await using (var identity = connection.CreateCommand())
        {
            identity.CommandText = "SELECT DATABASE(), SUBSTRING_INDEX(CURRENT_USER(), '@', 1);";
            await using var reader = await identity.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.GetString(0) != database || reader.GetString(1) != profile.AdminUserName)
                throw new InvalidDataException("Migration identity verification failed.");
        }
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TABLE_TYPE, TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = @database
            UNION ALL SELECT ROUTINE_TYPE, ROUTINE_NAME FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = @database
            UNION ALL SELECT 'TRIGGER', TRIGGER_NAME FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA = @database
            UNION ALL SELECT 'EVENT', EVENT_NAME FROM information_schema.EVENTS WHERE EVENT_SCHEMA = @database;
            """;
        command.Parameters.AddWithValue("@database", database);
        var inventory = new List<string>();
        await using var objects = await command.ExecuteReaderAsync(cancellationToken);
        while (await objects.ReadAsync(cancellationToken)) inventory.Add(objects.GetString(0) + ":" + objects.GetString(1));
        return inventory;
    }
}
