using AdaPanel.Core.Dtos.Sites;

namespace AdaPanel.Core.Interfaces;

public interface ISiteDefaultsService
{
    Task<SiteDefaultsResultDto> EnsureDefaultsAsync(SiteDto site, CancellationToken cancellationToken = default);
}
