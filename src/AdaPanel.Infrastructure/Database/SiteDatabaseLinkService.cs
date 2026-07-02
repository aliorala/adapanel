using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Database;

/// <summary>
/// Site↔veritabanı bağını <c>config/site-databases.json</c>'da ATOMİK tutar (UpdateAsync = dosya-başına kilit,
/// eşzamanlı oluşturmada kayıp güncelleme olmaz). Migration'a gerek kalmasın diye JSON.
/// </summary>
public sealed class SiteDatabaseLinkService(JsonFileStore fileStore) : ISiteDatabaseLinkService
{
    private const string StatePath = "config/site-databases.json";

    private static bool SameDb(SiteDatabaseLinkDto link, string engine, string databaseName) =>
        string.Equals(link.Engine, engine, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(link.DatabaseName, databaseName, StringComparison.OrdinalIgnoreCase);

    public async Task LinkAsync(string engine, string databaseName, string siteName, CancellationToken cancellationToken = default)
    {
        var normalizedEngine = (engine ?? string.Empty).Trim().ToLowerInvariant();
        var db = (databaseName ?? string.Empty).Trim();
        var site = (siteName ?? string.Empty).Trim();
        if (normalizedEngine.Length == 0 || db.Length == 0 || site.Length == 0)
        {
            return;
        }

        await fileStore.UpdateAsync(StatePath, new List<SiteDatabaseLinkDto>(), existing =>
        {
            var list = existing.Where(link => !SameDb(link, normalizedEngine, db)).ToList();
            list.Add(new SiteDatabaseLinkDto { Engine = normalizedEngine, DatabaseName = db, SiteName = site });
            return list;
        }, cancellationToken);
    }

    public async Task UnlinkAsync(string engine, string databaseName, CancellationToken cancellationToken = default)
    {
        var normalizedEngine = (engine ?? string.Empty).Trim();
        var db = (databaseName ?? string.Empty).Trim();
        await fileStore.UpdateAsync(StatePath, new List<SiteDatabaseLinkDto>(),
            existing => existing.Where(link => !SameDb(link, normalizedEngine, db)).ToList(),
            cancellationToken);
    }

    public async Task<IReadOnlyList<SiteDatabaseLinkDto>> GetForSiteAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var all = await fileStore.ReadAsync(StatePath, new List<SiteDatabaseLinkDto>(), cancellationToken);
        return all.Where(link => string.Equals(link.SiteName, siteName, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public async Task<IReadOnlyList<SiteDatabaseLinkDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await fileStore.ReadAsync(StatePath, new List<SiteDatabaseLinkDto>(), cancellationToken);
    }

    public async Task RemoveSiteAsync(string siteName, CancellationToken cancellationToken = default)
    {
        await fileStore.UpdateAsync(StatePath, new List<SiteDatabaseLinkDto>(),
            existing => existing.Where(link => !string.Equals(link.SiteName, siteName, StringComparison.OrdinalIgnoreCase)).ToList(),
            cancellationToken);
    }
}
