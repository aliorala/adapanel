using AdaPanel.Api.Security;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator,Customer,Reseller")]
[Route("api/v1/backup")]
public sealed class BackupController(
    IBackupService backupService,
    ICustomerScopeService customerScopes) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<BackupRecordDto>>>> GetBackupsAsync(CancellationToken cancellationToken)
    {
        var backups = await backupService.GetBackupsAsync(cancellationToken);

        // Customer yalnızca kendi sitelerinin yedeklerini görür.
        var scope = await CustomerAccess.GetSiteScopeAsync(customerScopes, User, cancellationToken);
        if (scope is not null)
        {
            backups = backups
                .Where(record => string.Equals(record.Type, "site", StringComparison.OrdinalIgnoreCase)
                    && scope.Contains(record.Source, StringComparer.OrdinalIgnoreCase))
                .ToArray();
        }

        return Ok(ApiResponse<IReadOnlyCollection<BackupRecordDto>>.Ok(backups));
    }

    [HttpPost("site")]
    public async Task<ActionResult<ApiResponse<BackupRecordDto>>> BackupSiteAsync([FromBody] BackupSiteRequest request, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, request.SiteName, cancellationToken);
        var backup = await backupService.BackupSiteAsync(request, cancellationToken);
        return Ok(ApiResponse<BackupRecordDto>.Ok(backup, "backup.created"));
    }

    /// <summary>Yerel yedek dosyasını .zip olarak indirir (Plesk benzeri "indir").</summary>
    [HttpGet("{backupId}/download")]
    public async Task<IActionResult> DownloadAsync(string backupId, CancellationToken cancellationToken)
    {
        var backups = await backupService.GetBackupsAsync(cancellationToken);
        var record = backups.FirstOrDefault(item => string.Equals(item.Id, backupId, StringComparison.OrdinalIgnoreCase));
        if (record is null)
        {
            return NotFound(ApiResponse<object>.Fail("backup.not_found"));
        }

        // Customer yalnızca kendi sitelerinin yedeğini indirebilir.
        var scope = await CustomerAccess.GetSiteScopeAsync(customerScopes, User, cancellationToken);
        if (scope is not null &&
            !(string.Equals(record.Type, "site", StringComparison.OrdinalIgnoreCase) &&
              scope.Contains(record.Source, StringComparer.OrdinalIgnoreCase)))
        {
            return NotFound(ApiResponse<object>.Fail("backup.not_found"));
        }

        if (string.IsNullOrWhiteSpace(record.Path) || !System.IO.File.Exists(record.Path))
        {
            return NotFound(ApiResponse<object>.Fail("backup.not_found"));
        }

        var contentType = record.Format.Equals("zip", StringComparison.OrdinalIgnoreCase)
            ? "application/zip"
            : "application/octet-stream";
        return PhysicalFile(record.Path, contentType, record.Name);
    }

    [HttpPost("db")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<BackupRecordDto>>> BackupDatabaseAsync([FromBody] BackupDatabaseRequest request, CancellationToken cancellationToken)
    {
        var backup = await backupService.BackupDatabaseAsync(request, cancellationToken);
        return Ok(ApiResponse<BackupRecordDto>.Ok(backup, "backup.created"));
    }

    /// <summary>Mail (Stalwart) verisini yedekler; mail servisi kısa süreliğine durdurulur.</summary>
    [HttpPost("mail")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<BackupRecordDto>>> BackupMailAsync([FromBody] BackupMailRequest request)
    {
        // Servisi durdurup başlatan akış istemci iptaline bağlanmaz.
        var backup = await backupService.BackupMailAsync(request, CancellationToken.None);
        return Ok(ApiResponse<BackupRecordDto>.Ok(backup, "backup.created"));
    }

    // Tam geri yükleme (hedef yol seçimi dahil) yalnız yöneticidedir; müşteriler
    // kendi sitelerine "restore-items" ile granüler geri yükleme yapar.
    [HttpPost("restore")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<BackupRecordDto>>> RestoreAsync([FromBody] RestoreBackupRequest request, CancellationToken cancellationToken)
    {
        var backup = await backupService.RestoreAsync(request, cancellationToken);
        return Ok(ApiResponse<BackupRecordDto>.Ok(backup, "backup.restored"));
    }

    /// <summary>Zip yedeğinin içeriğine dizin bazlı gözatma (granüler geri yükleme).</summary>
    [HttpGet("{backupId}/entries")]
    public async Task<ActionResult<ApiResponse<BackupEntriesDto>>> GetEntriesAsync(
        string backupId,
        [FromQuery] string? prefix,
        CancellationToken cancellationToken)
    {
        await EnsureBackupOwnershipAsync(backupId, cancellationToken);
        var entries = await backupService.GetEntriesAsync(backupId, prefix, cancellationToken);
        return Ok(ApiResponse<BackupEntriesDto>.Ok(entries));
    }

    [HttpPost("restore-items")]
    public async Task<ActionResult<ApiResponse<RestoreBackupItemsResultDto>>> RestoreItemsAsync(
        [FromBody] RestoreBackupItemsRequest request,
        CancellationToken cancellationToken)
    {
        await EnsureBackupOwnershipAsync(request.BackupId, cancellationToken);

        // Customer hedef yol seçemez: her zaman sitenin orijinal konumuna yüklenir.
        if (CustomerAccess.IsCustomer(User))
        {
            request.TargetPath = string.Empty;
        }

        var result = await backupService.RestoreItemsAsync(request, cancellationToken);
        return Ok(ApiResponse<RestoreBackupItemsResultDto>.Ok(result, "backup.items_restored"));
    }

    /// <summary>Customer/Reseller için yedek kaydının etkin kapsamındaki bir siteye ait olduğunu doğrular.</summary>
    private async Task EnsureBackupOwnershipAsync(string backupId, CancellationToken cancellationToken)
    {
        if (!CustomerAccess.IsScoped(User))
        {
            return;
        }

        var record = (await backupService.GetBackupsAsync(cancellationToken))
            .FirstOrDefault(item => item.Id == backupId)
            ?? throw new AdaPanel.Core.Common.AppException("backup.not_found", 404, 404);

        if (!string.Equals(record.Type, "site", StringComparison.OrdinalIgnoreCase))
        {
            throw new AdaPanel.Core.Common.AppException("auth.site_forbidden", 403, 403);
        }

        await CustomerAccess.EnsureSiteAsync(customerScopes, User, record.Source, cancellationToken);
    }
}
