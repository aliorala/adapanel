using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AdaPanel.Data.Persistence;

/// <summary>
/// Her bağlantı açılışında <c>PRAGMA busy_timeout</c> ayarlar. Microsoft.Data.Sqlite kendi yeniden
/// deneme döngüsüne güvenir ve SQLite'ın busy_timeout'unu AYARLAMAZ (okununca 0); eşzamanlı yazarlar
/// bu yüzden çekirdek düzeyinde beklemeden "database is locked" alabilir. Havuz kapalı olduğundan
/// (bkz. AddAdaPanelData) her bağlantı ayrı açılır ve pragma bağlantı başınadır.
/// </summary>
public sealed class SqliteBusyTimeoutInterceptor(int milliseconds) : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        Apply(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA busy_timeout={milliseconds};";
        await command.ExecuteNonQueryAsync(cancellationToken);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    private void Apply(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA busy_timeout={milliseconds};";
        command.ExecuteNonQuery();
    }
}
