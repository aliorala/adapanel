using AdaPanel.Core.Dtos.Databases;

namespace AdaPanel.Core.Interfaces;

public interface IDatabaseService
{
    Task<IReadOnlyCollection<DatabaseEngineStatusDto>> GetEnginesAsync(CancellationToken cancellationToken = default);

    Task<DatabaseEngineStatusDto> RegisterCredentialsAsync(RegisterDatabaseCredentialsRequest request, CancellationToken cancellationToken = default);

    Task<DatabaseWorkspaceDto> GetWorkspaceAsync(string engine, CancellationToken cancellationToken = default);

    Task<DatabaseWorkspaceDto> CreateDatabaseAsync(CreateDatabaseRequest request, CancellationToken cancellationToken = default);

    /// <summary>Var olan DB+kullanıcıyı panel yönetimine tanıtır: GRANT ALL (eksikse) + kullanıcı parolasını saklar.</summary>
    Task<DatabaseWorkspaceDto> ImportDatabaseAsync(ImportDatabaseRequest request, CancellationToken cancellationToken = default);

    Task DeleteDatabaseAsync(string engine, string name, CancellationToken cancellationToken = default);

    Task<DatabaseWorkspaceDto> CreateUserAsync(CreateDatabaseUserRequest request, CancellationToken cancellationToken = default);

    Task DeleteUserAsync(string engine, string userName, string host, CancellationToken cancellationToken = default);

    Task<DatabaseWorkspaceDto> SetUserPasswordAsync(SetDatabaseUserPasswordRequest request, CancellationToken cancellationToken = default);

    Task<DatabaseWorkspaceDto> GrantAccessAsync(GrantDatabaseAccessRequest request, CancellationToken cancellationToken = default);

    Task<DatabaseDumpResultDto> ExportDumpAsync(DatabaseDumpRequest request, CancellationToken cancellationToken = default);

    Task<DatabaseDumpResultDto> ImportDumpAsync(DatabaseDumpRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sunucu taraflı taşıma: kaynak (uzak olabilir) MySQL/MariaDB veritabanını mysqldump→mysql ile
    /// yerel hedef veritabanına aktarır. Tarayıcı upload'ı olmadığından 413/timeout yaşanmaz.
    /// </summary>
    Task<DatabaseTransferResultDto> TransferDatabaseAsync(DatabaseTransferRequest request, CancellationToken cancellationToken = default);

    /// <summary>K2: güvensiz DB yönetici hesaplarını (anonim + uzak root) kaldırır. Kaldırılanların listesini döner.</summary>
    /// <summary>Tek veritabaninin detay karti: baglanti ucu, kullanicilari, boyutu.</summary>
    Task<DatabaseDetailDto> GetDetailAsync(string engine, string databaseName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Veritabanini denetler; <paramref name="repairTables"/> bos degilse SECILEN tablolari onarir.
    /// Iki fazli: once yalnizca okuyan denetim, sonra kullanicinin sectigi tablolarda onarim.
    /// </summary>
    Task<DatabaseCheckResultDto> CheckAndRepairAsync(string engine, string databaseName, IReadOnlyCollection<string> repairTables, CancellationToken cancellationToken = default);

    /// <remarks>
    /// Dönüş tipi liste DEĞİL rapor: yalnız başarılanların listesi, kaldırılamayan güvensiz
    /// hesapları görünmez kılıyor ve uç "güvensiz yönetici hesabı bulunamadı" yazıyordu.
    /// Gerekçe: <see cref="DatabaseHardeningReport"/>.
    /// </remarks>
    Task<DatabaseHardeningReport> HardenAdminAccountsAsync(string engine, CancellationToken cancellationToken = default);
}
