using AdaPanel.Core.Dtos.Sites;

namespace AdaPanel.Core.Interfaces;

/// <summary>
/// panel WordPress Toolkit benzeri temel yönetim: kurulum algılama, sürüm okuma ve
/// bakım modu (WP-CLI gerektirmeden, dosya tabanlı).
/// </summary>
public interface IWordPressToolkitService
{
    /// <summary>Verilen sitedeki WordPress kurulumunu algılar (yoksa IsWordPress=false).</summary>
    Task<WordPressSiteDto> DetectAsync(string siteName, CancellationToken cancellationToken = default);

    /// <summary>Tüm yönetilen sitelerdeki WordPress kurulumlarını listeler.</summary>
    Task<IReadOnlyCollection<WordPressSiteDto>> DetectAllAsync(CancellationToken cancellationToken = default);

    Task<WordPressSiteDto> SetMaintenanceAsync(string siteName, bool enabled, CancellationToken cancellationToken = default);
}
