using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Iis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using IisAccess = AdaPanel.Infrastructure.AspNet.IisConfigurationAccess;

namespace AdaPanel.Infrastructure.Php;

/// <summary>PHP sürümünü kaldırma: kullanan site varsa REDDEDER (adlarıyla), yoksa FastCGI kaydı + bileşen kaldırılır.</summary>
public sealed partial class PhpService
{
    public async Task<IReadOnlyCollection<string>> FindSitesUsingVersionAsync(string componentId, CancellationToken cancellationToken = default)
    {
        EnsurePhpComponentId(componentId);
        var component = await dbContext.InstalledComponents.AsNoTracking()
            .FirstOrDefaultAsync(item => item.ComponentId == componentId, cancellationToken);
        var executablePath = component is null ? string.Empty : ResolvePhpExecutablePath(component.InstallDir);
        return await FindSitesUsingAsync(componentId, executablePath, cancellationToken);
    }

    private async Task<IReadOnlyCollection<string>> FindSitesUsingAsync(string componentId, string executablePath, CancellationToken cancellationToken)
    {
        var handlerName = BuildHandlerName(componentId);
        var sites = await iisSiteService.GetSitesAsync(cancellationToken);
        var users = new List<string>();
        var unreadable = new List<string>();
        using var manager = SiteAssignmentManagerFactory();
        foreach (var site in sites)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var handlers = IisAccess.GetHandlersCollection(manager, site.Name);
                var uses = Enumerate(handlers).Any(item =>
                    string.Equals(IisAccess.TryGetAttributeString(item, "name"), handlerName, StringComparison.OrdinalIgnoreCase) ||
                    (executablePath.Length > 0 &&
                     string.Equals(IisAccess.TryGetAttributeString(item, "scriptProcessor"), executablePath, StringComparison.OrdinalIgnoreCase)));
                if (uses) users.Add(site.Name);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "PHP handler usage could not be read for site {SiteName}.", site.Name);
                unreadable.Add(site.Name);
            }
        }

        // SESSİZ DEĞİL, KAPALI BAŞARISIZ: bir sitenin kullanımı ölçülemediyse "kullanan yok" denemez.
        if (unreadable.Count > 0)
            throw new AppException("php.usage_unverified", 503, 503, "Sites that could not be checked: " + string.Join(", ", unreadable));
        return users;
    }

    public async Task<IReadOnlyCollection<string>> RemoveVersionAsync(
        string componentId, Guid? actorUserId, string? ipAddress, CancellationToken cancellationToken = default)
    {
        EnsurePhpComponentId(componentId);
        // Atama (SetSitePhpVersionAsync) ile AYNI kilit: ölçüm ile kaldırma arasında yeni bir site atanamaz.
        return await concurrencyLock.RunExclusiveAsync(async () =>
        {
            var component = await dbContext.InstalledComponents
                .FirstOrDefaultAsync(item => item.ComponentId == componentId, cancellationToken)
                ?? throw new AppException("php.version_not_installed", 404, 404);
            var executablePath = ResolvePhpExecutablePath(component.InstallDir);

            var users = await FindSitesUsingAsync(componentId, executablePath, cancellationToken);
            if (users.Count > 0)
                throw new AppException("php.version_in_use", 409, 409, "Sites using this version: " + string.Join(", ", users));

            // Önce FastCGI kaydı: silinemezse HİÇBİR ŞEY değişmeden hata döner (bileşen kalır, kayıt bozulmaz).
            try
            {
                using var serverManager = SiteAssignmentManagerFactory();
                var collection = GetFastCgiCollection(serverManager);
                var registered = Enumerate(collection).FirstOrDefault(item =>
                    string.Equals(GetAttributeString(item, "fullPath"), executablePath, StringComparison.OrdinalIgnoreCase));
                if (registered is not null)
                {
                    InvokeMethod(collection, "Remove", registered);
                    InvokeMethod(serverManager, "CommitChanges");
                }
            }
            catch (Exception exception) when (exception is not (AppException or OperationCanceledException))
            {
                logger.LogError(exception, "FastCGI registration of {ComponentId} could not be removed.", componentId);
                throw MapPhpConfigurationException(exception);
            }

            return await componentBootstrapper.RemoveAsync(componentId, actorUserId, ipAddress, cancellationToken);
        }, cancellationToken);
    }
}
