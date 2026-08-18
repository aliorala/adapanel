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

    /// <summary>
    /// Uzak erişimin bu motordaki mekanizması: <c>mysqlHost</c>, <c>pgHba</c> ya da <c>none</c>.
    /// </summary>
    /// <remarks>
    /// Motor listesi HİÇBİR YERDE elle yazılmaz; hangi motorun neyi desteklediği motorun kendi
    /// sağlayıcısında durur. Desteklemeyen motor <c>none</c> döner ve üst katman onu "desteklenmiyor"
    /// diye gösterir — sessizce başarılı saymaz.
    /// </remarks>
    string RemoteAccessMechanism => "none";

    /// <summary>
    /// Veritabanı kullanıcılarının uzak erişim GERÇEĞİNİ motordan okur (kullanıcı → gerçek).
    /// </summary>
    /// <remarks>
    /// Toplu okur: liste ekranı kullanıcı başına ayrı bağlantı açsaydı, elli hesaplı bir sunucuda
    /// sayfa açılışı elli bağlantı demek olurdu. Sorgu tek seferde çalışır, süzme bellekte yapılır.
    /// </remarks>
    Task<IReadOnlyDictionary<string, RemoteAccessFacts>> ReadRemoteAccessAsync(
        DatabaseConnectionProfile profile,
        IReadOnlyCollection<string> userNames,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyDictionary<string, RemoteAccessFacts>>(
            new Dictionary<string, RemoteAccessFacts>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Bir veritabanı kullanıcısının uzak erişimini <paramref name="origins"/> ile EŞİTLER.
    /// </summary>
    /// <param name="password">
    /// Yeni bir hesap kaydı oluşturmak gerekirse kullanılır (MySQL'de her host ayrı hesaptır).
    /// Gerekmiyorsa boş olabilir.
    /// </param>
    /// <remarks>
    /// Boş <paramref name="origins"/> = KAPAT. Eşitleme mantığı: listede olmayan uzak kayıt kalmaz.
    /// </remarks>
    Task ApplyRemoteAccessAsync(
        DatabaseConnectionProfile profile,
        string userName,
        IReadOnlyCollection<string> databaseNames,
        IReadOnlyCollection<RemoteAccessOrigin> origins,
        string password,
        CancellationToken cancellationToken)
        => throw new AdaPanel.Core.Common.AppException("database.remote_not_supported", 400, 400);
}

/// <summary>Motordan OKUNAN uzak erişim gerçeği (panelin kaydı değil).</summary>
/// <param name="Hosts">Kullanıcının motorda geçerli host/adres kayıtları.</param>
/// <param name="Databases">Kullanıcının yetkili olduğu veritabanları (kapsam denetimi buradan beslenir).</param>
/// <param name="WarningKey">Motor düzeyinde ek engel varsa i18n anahtarı; yoksa boş.</param>
internal sealed record RemoteAccessFacts(
    IReadOnlyCollection<string> Hosts,
    IReadOnlyCollection<string> Databases,
    string WarningKey)
{
    public static readonly RemoteAccessFacts Empty = new([], [], string.Empty);
}
