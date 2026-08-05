using AdaPanel.Core.Dtos.Sites;

namespace AdaPanel.Core.Interfaces;

public interface ISiteHostingSettingsService
{
    Task<SiteHostingSettingsDto> GetAsync(string siteName, CancellationToken cancellationToken = default);

    Task<SiteHostingSettingsDto> UpdateAsync(
        string siteName,
        UpdateSiteHostingSettingsRequest request,
        CancellationToken cancellationToken = default);

    Task<ComposerRunResultDto> RunComposerInstallAsync(string siteName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Keyfi composer alt komutu çalıştırır (panel Composer komut kutusu gibi): update, require, dump-autoload vb.
    /// Komut composer'a argüman olarak geçer (shell yok).
    /// </summary>
    Task<ComposerRunResultDto> RunComposerCommandAsync(string siteName, string command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Laravel Artisan komutu çalıştırır (php artisan ...): migrate, cache:clear, key:generate, config:cache vb.
    /// Site kökünde 'artisan' dosyası olmalı (Laravel uygulaması). Komut argüman olarak geçer (shell yok).
    /// </summary>
    Task<ComposerRunResultDto> RunArtisanCommandAsync(string siteName, string command, CancellationToken cancellationToken = default);

    /// <summary>Node.js komutu çalıştırır (npm install / npm run &lt;script&gt; vb.) site kökünde.</summary>
    Task<ComposerRunResultDto> RunNodeCommandAsync(string siteName, string command, CancellationToken cancellationToken = default);

    /// <summary>WP-CLI komutu çalıştırır (plugin/tema/çekirdek yönetimi, search-replace vb.). Site WordPress olmalı.</summary>
    Task<ComposerRunResultDto> RunWpCliCommandAsync(string siteName, string command, CancellationToken cancellationToken = default);

    /// <summary>Site göçü: arşiv URL'sinden (http/https/ftp .zip) dosyaları indirip hedef siteye açar.</summary>
    Task<SiteImportResultDto> ImportSiteArchiveAsync(string siteName, string archiveUrl, bool cleanFirst, CancellationToken cancellationToken = default);

    /// <summary>Laravel queue worker durumu (çalışıyor mu).</summary>
    Task<QueueWorkerStatusDto> GetQueueWorkerAsync(string siteName, CancellationToken cancellationToken = default);

    /// <summary>queue:work worker'ını başlatır (kendini yenileyen zamanlanmış görev).</summary>
    Task<QueueWorkerStatusDto> StartQueueWorkerAsync(string siteName, CancellationToken cancellationToken = default);

    /// <summary>queue worker'ını durdurur (görevi siler + queue:restart sinyali).</summary>
    Task<QueueWorkerStatusDto> StopQueueWorkerAsync(string siteName, CancellationToken cancellationToken = default);

    /// <summary>Laravel scheduler (dakikalık `php artisan schedule:run`) durumu.</summary>
    Task<QueueWorkerStatusDto> GetLaravelSchedulerAsync(string siteName, CancellationToken cancellationToken = default);

    /// <summary>Laravel scheduler'ı başlatır (dakikalık zamanlanmış görev: schedule:run).</summary>
    Task<QueueWorkerStatusDto> StartLaravelSchedulerAsync(string siteName, CancellationToken cancellationToken = default);

    /// <summary>Laravel scheduler görevini siler.</summary>
    Task<QueueWorkerStatusDto> StopLaravelSchedulerAsync(string siteName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Site silindiğinde onun barındırma ayarlarını ve ARKA PLAN GÖREVLERİNİ kaldırır.
    /// <para>
    /// Yalnızca ayar kaydı değil: kuyruk işçisi ve Laravel zamanlayıcı birer Windows zamanlanmış
    /// görevidir. Site gittikten sonra da tetiklenmeye devam ederler — çalıştıracakları dosya
    /// artık yoktur, yani her turda hata üretip günlüğü kirletirler ve panelde "başarısız görev"
    /// olarak görünürler.
    /// </para>
    /// </summary>
    Task RemoveSiteAsync(string siteName, CancellationToken cancellationToken = default);
}
