using AdaPanel.Api.Extensions;
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
    ICustomerScopeService customerScopes,
    IFileMigrationService migrationService,
    IAuditService auditService) : ControllerBase
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
        var settings = await service.UpdateAsync(siteName, request, cancellationToken);

        // Bu uç siteyi düşürebilecek ayarları (özel hata sayfaları, MIME eşlemeleri, HSTS) yazar.
        // HSTS özellikle geri alınması ZOR bir karardır: tarayıcı max-age boyunca siteyi zorla
        // HTTPS'e çevirir. "Kim, ne zaman açtı" sorusu cevapsız kalmamalı.
        await WriteAuditAsync(
            "sites.hosting_settings.updated",
            $"Hosting settings for '{siteName}' were updated "
            + $"(custom error pages: {settings.CustomErrorPages.Length}, MIME maps: {settings.MimeTypes.Length}, "
            + $"HSTS: {(settings.HstsEnabled ? $"on/max-age={settings.HstsMaxAgeSeconds}" : "off")}).",
            cancellationToken);
        return Ok(ApiResponse<SiteHostingSettingsDto>.Ok(settings, "sites.hosting_settings_saved"));
    }

    [HttpPost("composer-install")]
    public async Task<ActionResult<ApiResponse<ComposerRunResultDto>>> ComposerInstallAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var result = await service.RunComposerInstallAsync(siteName, cancellationToken);
        await WriteAuditAsync("sites.composer.install", $"composer install was run for '{siteName}' (exit code {result.ExitCode}).", cancellationToken);
        return Ok(ApiResponse<ComposerRunResultDto>.Ok(result, "composer.completed"));
    }

    // profesyonel paneller gibi keyfi Composer komutu (update, require, dump-autoload, vb.).
    [HttpPost("composer-command")]
    public async Task<ActionResult<ApiResponse<ComposerRunResultDto>>> ComposerCommandAsync(
        string siteName,
        [FromBody] ComposerCommandRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var result = await service.RunComposerCommandAsync(siteName, request.Command, cancellationToken);
        // ÇALIŞTIRILAN KOMUT kayda girer: yalnız çıkış kodunu yazmak, olay sonrası "sitede ne
        // yapıldı" sorusunu cevapsız bırakır (aynı gerekçe TerminalController'da da geçerli).
        await WriteAuditAsync("sites.composer.command", $"composer '{request.Command}' was run for '{siteName}' (exit code {result.ExitCode}).", cancellationToken);
        return Ok(ApiResponse<ComposerRunResultDto>.Ok(result, "composer.completed"));
    }

    // panel Laravel Toolkit gibi: keyfi Artisan komutu (migrate, cache:clear, key:generate, config:cache, vb.).
    [HttpPost("artisan-command")]
    public async Task<ActionResult<ApiResponse<ComposerRunResultDto>>> ArtisanCommandAsync(
        string siteName,
        [FromBody] ComposerCommandRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var result = await service.RunArtisanCommandAsync(siteName, request.Command, cancellationToken);
        await WriteAuditAsync("sites.artisan.command", $"artisan '{request.Command}' was run for '{siteName}' (exit code {result.ExitCode}).", cancellationToken);
        return Ok(ApiResponse<ComposerRunResultDto>.Ok(result, "artisan.completed"));
    }

    // panel Node.js sekmesi gibi: npm install / npm run <script> vb.
    [HttpPost("node-command")]
    public async Task<ActionResult<ApiResponse<ComposerRunResultDto>>> NodeCommandAsync(
        string siteName,
        [FromBody] ComposerCommandRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var result = await service.RunNodeCommandAsync(siteName, request.Command, cancellationToken);
        await WriteAuditAsync("sites.node.command", $"npm '{request.Command}' was run for '{siteName}' (exit code {result.ExitCode}).", cancellationToken);
        return Ok(ApiResponse<ComposerRunResultDto>.Ok(result, "node.completed"));
    }

    // panel WP Toolkit gibi: WP-CLI komutu (plugin/tema/çekirdek, search-replace, cache flush, user…).
    [HttpPost("wpcli-command")]
    public async Task<ActionResult<ApiResponse<ComposerRunResultDto>>> WpCliCommandAsync(
        string siteName,
        [FromBody] ComposerCommandRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var result = await service.RunWpCliCommandAsync(siteName, request.Command, cancellationToken);
        await WriteAuditAsync("sites.wpcli.command", $"wp '{request.Command}' was run for '{siteName}' (exit code {result.ExitCode}).", cancellationToken);
        return Ok(ApiResponse<ComposerRunResultDto>.Ok(result, "wpcli.completed"));
    }

    // profesyonel goc araci gibi: kaynak sitenin dosya arşivini (http/https/ftp .zip) indirip hedef siteye açar.
    [HttpPost("import-archive")]
    public async Task<ActionResult<ApiResponse<SiteImportResultDto>>> ImportArchiveAsync(
        string siteName,
        [FromBody] ImportSiteArchiveRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var result = await service.ImportSiteArchiveAsync(siteName, request.ArchiveUrl, request.CleanFirst, cancellationToken);
        await WriteAuditAsync(
            "sites.archive.imported",
            $"An archive was imported into '{siteName}' ({result.FilesCopied} files, clean first: {request.CleanFirst}).",
            cancellationToken);
        return Ok(ApiResponse<SiteImportResultDto>.Ok(result, "migration.completed"));
    }

    // profesyonel goc araci gibi (dosya tarafı): kaynak sunucudan FTP/FTPS/SFTP ile site dosyalarını hedef sitenin köküne indirir.
    [HttpPost("migrate-files")]
    public async Task<ActionResult<ApiResponse<RemoteFileMigrationResult>>> MigrateFilesAsync(
        string siteName,
        [FromBody] RemoteFileMigrationRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        request.SiteName = siteName;
        var result = await migrationService.PullAsync(request, cancellationToken);
        // İstek nesnesi uzak sunucunun PAROLASINI taşır; kayda yalnız protokol/host ve sonuç girer.
        await WriteAuditAsync(
            "sites.files.migrated",
            $"Files were pulled into '{siteName}' over {result.Protocol} from '{request.Host}' ({result.FilesDownloaded} files).",
            cancellationToken);
        return Ok(ApiResponse<RemoteFileMigrationResult>.Ok(result, "migration.completed"));
    }

    // panel Kuyruk sekmesi gibi: Laravel queue worker durum / başlat / durdur.
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
        var status = await service.StartQueueWorkerAsync(siteName, cancellationToken);
        await WriteAuditAsync("sites.queue.started", $"The queue worker for '{siteName}' was started (task '{status.TaskName}').", cancellationToken);
        return Ok(ApiResponse<QueueWorkerStatusDto>.Ok(status, "queue.started"));
    }

    [HttpPost("queue/stop")]
    public async Task<ActionResult<ApiResponse<QueueWorkerStatusDto>>> StopQueueAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var status = await service.StopQueueWorkerAsync(siteName, cancellationToken);
        await WriteAuditAsync("sites.queue.stopped", $"The queue worker for '{siteName}' was stopped (task '{status.TaskName}').", cancellationToken);
        return Ok(ApiResponse<QueueWorkerStatusDto>.Ok(status, "queue.stopped"));
    }

    // panel Laravel Toolkit "Scheduler": dakikalık `php artisan schedule:run` (Laravel'in tek cron girdisi).
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
        var status = await service.StartLaravelSchedulerAsync(siteName, cancellationToken);
        await WriteAuditAsync("sites.scheduler.started", $"The Laravel scheduler for '{siteName}' was started (task '{status.TaskName}').", cancellationToken);
        return Ok(ApiResponse<QueueWorkerStatusDto>.Ok(status, "scheduler.started"));
    }

    [HttpPost("scheduler/stop")]
    public async Task<ActionResult<ApiResponse<QueueWorkerStatusDto>>> StopSchedulerAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var status = await service.StopLaravelSchedulerAsync(siteName, cancellationToken);
        await WriteAuditAsync("sites.scheduler.stopped", $"The Laravel scheduler for '{siteName}' was stopped (task '{status.TaskName}').", cancellationToken);
        return Ok(ApiResponse<QueueWorkerStatusDto>.Ok(status, "scheduler.stopped"));
    }

    // Komşu denetleyicilerle (AspController, PhpController) AYNI kalıp: kim / nereden / neye / ne yaptı.
    private Task WriteAuditAsync(string action, string description, CancellationToken cancellationToken)
    {
        return auditService.WriteAsync(
            action,
            "sites",
            description,
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
    }
}
