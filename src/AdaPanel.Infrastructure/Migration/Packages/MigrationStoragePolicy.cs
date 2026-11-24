using AdaPanel.Core.Interfaces;

namespace AdaPanel.Infrastructure.Migration.Packages;

internal static class MigrationStoragePolicy
{
    internal static async Task<string[]> PublicRootsAsync(IIisSiteService? sites, IEnumerable<string> configured,
        CancellationToken cancellationToken)
    {
        var roots = configured.Where(path => !string.IsNullOrWhiteSpace(path)).ToList();
        if (sites is not null)
        {
            // Includes internal webmail, database tools and default IIS sites, not only visible panel sites.
            // An unreadable native inventory must not become an empty allowlist.
            var inventory = await sites.GetMigrationTargetSitesAsync(cancellationToken);
            if (inventory.Any(site => string.IsNullOrWhiteSpace(site.PhysicalPath) && string.IsNullOrWhiteSpace(site.ApplicationRootPath)))
                throw new InvalidDataException("migration.acquisition_storage_unverified");
            roots.AddRange(inventory.SelectMany(site => new[] { site.PhysicalPath, site.ApplicationRootPath })
                .Where(path => !string.IsNullOrWhiteSpace(path)));
        }
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
