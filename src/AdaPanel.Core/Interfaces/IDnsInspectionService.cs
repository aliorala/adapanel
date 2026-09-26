using AdaPanel.Core.Dtos.Sites;

namespace AdaPanel.Core.Interfaces;

/// <summary>Read-only bulk DNS inspection; never creates or updates zones.</summary>
public interface IDnsInspectionService
{
    Task<IReadOnlyDictionary<string, DnsInspectionDto>> InspectZonesAsync(
        IReadOnlyCollection<SiteDto> sites, CancellationToken cancellationToken = default);
}
