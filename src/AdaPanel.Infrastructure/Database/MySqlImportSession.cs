using AdaPanel.Core.Common;
using MySqlConnector;

namespace AdaPanel.Infrastructure.Database;

internal static class MySqlImportSession
{
    // Inspect the same local administrator login used by mysql stdin. Never modify server or
    // session modes to make a dump fit; unsupported lexical modes are rejected by the converter.
    internal static async Task<string> ReadSqlModeAsync(DatabaseConnectionProfile profile, CancellationToken ct)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = profile.Host, Port = (uint)profile.Port, UserID = profile.AdminUserName,
            Password = profile.AdminPassword, SslMode = MySqlSslMode.None,
            Pooling = false, ConnectionTimeout = 5, DefaultCommandTimeout = 5
        };
        try
        {
            await using var connection = new MySqlConnection(builder.ConnectionString);
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT @@SESSION.sql_mode;";
            return (await command.ExecuteScalarAsync(ct))?.ToString() ?? string.Empty;
        }
        catch (MySqlException error)
        {
            throw new AppException("database.connection_failed", 503, 503,
                "No SQL import was started. Could not inspect the import session SQL_MODE. " + error.Message);
        }
    }
}
