using AdaPanel.Api.Security;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

// Reseller (Bayi) yalnız kendi müşterilerinin sitelerinin hosting ayarlarını yönetebilir.
[ApiController]
[Authorize(Roles = "Administrator,Reseller")]
[Route("api/v1/sites/{siteName}/hosting-settings")]
public sealed class SiteHostingSettingsController(
    ISiteHostingSettingsService service,
    ICustomerScopeService customerScopes) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<SiteHostingSettingsDto>>> GetAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        return Ok(ApiResponse<SiteHostingSettingsDto>.Ok(await service.GetAsync(siteName, cancellationToken)));
    }

    [HttpPut]
    public async Task<ActionResult<ApiResponse<SiteHostingSettingsDto>>> UpdateAsync(
        string siteName,
        [FromBody] UpdateSiteHostingSettingsRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        return Ok(ApiResponse<SiteHostingSettingsDto>.Ok(await service.UpdateAsync(siteName, request, cancellationToken), "sites.hosting_settings_saved"));
    }

    [HttpPost("composer-install")]
    public async Task<ActionResult<ApiResponse<ComposerRunResultDto>>> ComposerInstallAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        return Ok(ApiResponse<ComposerRunResultDto>.Ok(await service.RunComposerInstallAsync(siteName, cancellationToken), "composer.completed"));
    }

    // Plesk gibi keyfi Composer komutu (update, require, dump-autoload, vb.).
    [HttpPost("composer-command")]
    public async Task<ActionResult<ApiResponse<ComposerRunResultDto>>> ComposerCommandAsync(
        string siteName,
        [FromBody] ComposerCommandRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        return Ok(ApiResponse<ComposerRunResultDto>.Ok(await service.RunComposerCommandAsync(siteName, request.Command, cancellationToken), "composer.completed"));
    }

    // Plesk Laravel Toolkit gibi: keyfi Artisan komutu (migrate, cache:clear, key:generate, config:cache, vb.).
    [HttpPost("artisan-command")]
    public async Task<ActionResult<ApiResponse<ComposerRunResultDto>>> ArtisanCommandAsync(
        string siteName,
        [FromBody] ComposerCommandRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        return Ok(ApiResponse<ComposerRunResultDto>.Ok(await service.RunArtisanCommandAsync(siteName, request.Command, cancellationToken), "artisan.completed"));
    }

    // Plesk Node.js sekmesi gibi: npm install / npm run <script> vb.
    [HttpPost("node-command")]
    public async Task<ActionResult<ApiResponse<ComposerRunResultDto>>> NodeCommandAsync(
        string siteName,
        [FromBody] ComposerCommandRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        return Ok(ApiResponse<ComposerRunResultDto>.Ok(await service.RunNodeCommandAsync(siteName, request.Command, cancellationToken), "node.completed"));
    }

    // Plesk WP Toolkit gibi: WP-CLI komutu (plugin/tema/çekirdek, search-replace, cache flush, user…).
    [HttpPost("wpcli-command")]
    public async Task<ActionResult<ApiResponse<ComposerRunResultDto>>> WpCliCommandAsync(
        string siteName,
        [FromBody] ComposerCommandRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        return Ok(ApiResponse<ComposerRunResultDto>.Ok(await service.RunWpCliCommandAsync(siteName, request.Command, cancellationToken), "wpcli.completed"));
    }

    // Plesk Migrator gibi: kaynak sitenin dosya arşivini (http/https/ftp .zip) indirip hedef siteye açar.
    [HttpPost("import-archive")]
    public async Task<ActionResult<ApiResponse<SiteImportResultDto>>> ImportArchiveAsync(
        string siteName,
        [FromBody] ImportSiteArchiveRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        return Ok(ApiResponse<SiteImportResultDto>.Ok(await service.ImportSiteArchiveAsync(siteName, request.ArchiveUrl, request.CleanFirst, cancellationToken), "migration.completed"));
    }

    // Plesk Kuyruk sekmesi gibi: Laravel queue worker durum / başlat / durdur.
    [HttpGet("queue")]
    public async Task<ActionResult<ApiResponse<QueueWorkerStatusDto>>> GetQueueAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        return Ok(ApiResponse<QueueWorkerStatusDto>.Ok(await service.GetQueueWorkerAsync(siteName, cancellationToken)));
    }

    [HttpPost("queue/start")]
    public async Task<ActionResult<ApiResponse<QueueWorkerStatusDto>>> StartQueueAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        return Ok(ApiResponse<QueueWorkerStatusDto>.Ok(await service.StartQueueWorkerAsync(siteName, cancellationToken), "queue.started"));
    }

    [HttpPost("queue/stop")]
    public async Task<ActionResult<ApiResponse<QueueWorkerStatusDto>>> StopQueueAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        return Ok(ApiResponse<QueueWorkerStatusDto>.Ok(await service.StopQueueWorkerAsync(siteName, cancellationToken), "queue.stopped"));
    }

    // Plesk Laravel Toolkit "Scheduler": dakikalık `php artisan schedule:run` (Laravel'in tek cron girdisi).
    [HttpGet("scheduler")]
    public async Task<ActionResult<ApiResponse<QueueWorkerStatusDto>>> GetSchedulerAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        return Ok(ApiResponse<QueueWorkerStatusDto>.Ok(await service.GetLaravelSchedulerAsync(siteName, cancellationToken)));
    }

    [HttpPost("scheduler/start")]
    public async Task<ActionResult<ApiResponse<QueueWorkerStatusDto>>> StartSchedulerAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        return Ok(ApiResponse<QueueWorkerStatusDto>.Ok(await service.StartLaravelSchedulerAsync(siteName, cancellationToken), "scheduler.started"));
    }

    [HttpPost("scheduler/stop")]
    public async Task<ActionResult<ApiResponse<QueueWorkerStatusDto>>> StopSchedulerAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        return Ok(ApiResponse<QueueWorkerStatusDto>.Ok(await service.StopLaravelSchedulerAsync(siteName, cancellationToken), "scheduler.stopped"));
    }
}
