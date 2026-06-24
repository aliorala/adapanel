using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Enums;

namespace AdaPanel.Infrastructure.Database;

internal interface IDatabaseEngineProvider
{
    DbEngine Engine { get; }

    Task<DatabaseWorkspaceDto> GetWorkspaceAsync(DatabaseConnectionProfile profile, CancellationToken cancellationToken);

    Task CreateDatabaseAsync(DatabaseConnectionProfile profile, string databaseName, CancellationToken cancellationToken);

    Task DeleteDatabaseAsync(DatabaseConnectionProfile profile, string databaseName, CancellationToken cancellationToken);

    Task CreateUserAsync(DatabaseConnectionProfile profile, string userName, string password, string allowedHost, CancellationToken cancellationToken);

    Task SetUserPasswordAsync(DatabaseConnectionProfile profile, string userName, string password, string allowedHost, CancellationToken cancellationToken);

    Task DeleteUserAsync(DatabaseConnectionProfile profile, string userName, string host, CancellationToken cancellationToken);

    Task GrantAccessAsync(
        DatabaseConnectionProfile profile,
        string databaseName,
        string userName,
        string allowedHost,
        IReadOnlyCollection<string> privileges,
        CancellationToken cancellationToken);

    // K2: güvensiz yönetici hesaplarını (anonim kullanıcılar + uzak root@'%') kaldırır; root@localhost'a
    // DOKUNMAZ (panel onunla bağlanır). Yalnız MySQL/MariaDB anlamlı uygular; diğer motorlar no-op.
    Task<IReadOnlyList<string>> HardenAdminAccountsAsync(DatabaseConnectionProfile profile, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    // K2 (parolasız-root açığı): yerel root hesaplarına (localhost/127.0.0.1/::1) güçlü parola koyar, tam
    // yetki verir ve yeni parolayı DOĞRULAR. Atomik akışın "uygula" adımı (saklama/rollback servis katmanında).
    // Yalnız MySQL/MariaDB anlamlı uygular; diğer motorlar no-op (parolasız-root onlara özgü değil).
    Task SetRootPasswordAsync(DatabaseConnectionProfile profile, string newPassword, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
