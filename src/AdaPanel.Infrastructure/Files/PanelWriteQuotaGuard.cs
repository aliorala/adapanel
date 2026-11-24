using AdaPanel.Core.Interfaces;

namespace AdaPanel.Infrastructure.Files;

/// <inheritdoc />
public sealed class PanelWriteQuotaGuard(
    ICustomerScopeService customerScopes,
    IIisSiteService iisSiteService) : IPanelWriteQuotaGuard
{
    // Klon boyutu ölçümü bütçesi: dosya yöneticisindeki kopya ölçümüyle aynı mertebe.
    private const int OgeButcesi = 200_000;
    private const int SureButcesiMs = 10_000;

    /// <inheritdoc />
    public async Task EnsureRoomForSiteAsync(string siteName, long incomingBytes, CancellationToken cancellationToken = default)
    {
        var owner = await customerScopes.GetSiteOwnerUserIdAsync(siteName, cancellationToken);
        if (owner is { } ownerId)
        {
            await EnsureRoomForSubscriptionAsync(ownerId, incomingBytes, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task EnsureRoomForSubscriptionAsync(Guid userId, long incomingBytes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scope = await customerScopes.GetScopeAsync(userId, cancellationToken);
        if (scope.MaxDiskMb <= 0)
        {
            return; // 0 = sınırsız (panelin her yerinde aynı sözleşme)
        }

        var usage = await customerScopes.GetDiskUsageMbAsync(userId, cancellationToken);
        FileQuotaGuard.EnsureRoom(scope.MaxDiskMb, usage, incomingBytes, string.Empty);
    }

    /// <inheritdoc />
    public async Task EnsureRoomForCopyOfSiteAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var owner = await customerScopes.GetSiteOwnerUserIdAsync(siteName, cancellationToken);
        if (owner is not { } ownerId)
        {
            return;
        }

        var scope = await customerScopes.GetScopeAsync(ownerId, cancellationToken);
        if (scope.MaxDiskMb <= 0)
        {
            return;
        }

        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var yol = string.IsNullOrWhiteSpace(site.ApplicationRootPath) ? site.PhysicalPath : site.ApplicationRootPath;
        var olcum = SafeDirectoryWalk.Yuru(yol, new SafeDirectoryWalkBudget(OgeButcesi, SureButcesiMs), cancellationToken);
        var usage = await customerScopes.GetDiskUsageMbAsync(ownerId, cancellationToken);
        FileQuotaGuard.EnsureRoom(scope.MaxDiskMb, usage, olcum.TotalBytes, olcum.Truncated ? olcum.TruncatedReasonKey : string.Empty);
    }
}
