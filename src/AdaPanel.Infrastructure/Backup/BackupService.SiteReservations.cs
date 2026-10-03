using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Iis;

namespace AdaPanel.Infrastructure.Backup;

public sealed partial class BackupService
{
    /// <summary>Subscription-wide generation pins; the IIS gate is held only for capture and validation.</summary>
    private sealed class SubscriptionSiteReservations(IIisSiteService sites, IConcurrencyLock gates) : IDisposable
    {
        private readonly List<IDisposable> reservations = [];

        public async Task<SiteDto> CaptureAsync(string name, CancellationToken token)
        {
            var target = await SiteMutationGuard.CaptureAsync(sites, gates, name, token);
            try
            {
                var use = await target.ReserveAsync(token);
                reservations.Add(target);
                reservations.Add(use);
                return target.Site;
            }
            catch { target.Dispose(); throw; }
        }

        public void Dispose()
        {
            for (var index = reservations.Count - 1; index >= 0; index--) reservations[index].Dispose();
            reservations.Clear();
        }
    }
}
