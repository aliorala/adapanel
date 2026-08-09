using AdaPanel.Api.Extensions;
using AdaPanel.Api.Security;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Files;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

// DENETİM KAYDI: bu denetleyicinin DURUM DEĞİŞTİREN her ucu iz bırakır (yükleme, yazma, silme,
// klasör açma, ACL, arşivleme, çıkarma). Gerekçe: dosya yöneticisi bir kiracının sitesinin
// içeriğini tümüyle değiştirebilir — hangi dosyanın kim tarafından silindiği kayıtlı değilse
// bir uyuşmazlıkta panelin söyleyecek hiçbir şeyi olmaz. Okuma uçları (listeleme/indirme/arama)
// bilinçli olarak yazılmaz: her listelemede satır üretmek kaydı gürültüye boğar ve asıl
// değişiklikleri görünmez kılardı.
[ApiController]
[Authorize(Roles = "Administrator,Customer,Reseller")]
[RequireSubUserSection("files")]
[Route("api/v1/files")]
public sealed class FilesController(
    IFileService fileService,
    ICustomerScopeService customerScopes,
    IAuditService auditService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<FileWorkspaceDto>>> GetWorkspaceAsync([FromQuery] string? path, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureFilesPathAsync(customerScopes, User, path, allowRoot: true, cancellationToken);

        // Customer/Reseller için kök listesi yalnızca etkin sitelerini gösterir (diğer site adları sızmaz).
        var normalized = (path ?? string.Empty).Replace('\\', '/').Trim('/');
        var scopedSites = await CustomerAccess.GetEffectiveSitesAsync(customerScopes, User, cancellationToken);
        if (scopedSites is not null
            && (normalized.Length == 0 || string.Equals(normalized, "sites", StringComparison.OrdinalIgnoreCase)))
        {
            var workspace = new FileWorkspaceDto
            {
                Path = "sites",
                ParentPath = string.Empty,
                DisplayName = "sites",
                IsDirectory = true,
                Entries = scopedSites
                    .OrderBy(site => site, StringComparer.OrdinalIgnoreCase)
                    .Select(site => new FileEntryDto
                    {
                        Name = site,
                        Path = $"sites/{site}",
                        IsDirectory = true
                    })
                    .ToArray()
            };
            return Ok(ApiResponse<FileWorkspaceDto>.Ok(workspace));
        }

        var result = await fileService.GetWorkspaceAsync(path ?? string.Empty, cancellationToken);
        return Ok(ApiResponse<FileWorkspaceDto>.Ok(result));
    }

    [HttpGet("download")]
    public async Task<IActionResult> DownloadAsync([FromQuery] string path, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureFilesPathAsync(customerScopes, User, path, allowRoot: false, cancellationToken);
        var (fileName, contentType, physicalPath) = await fileService.DownloadAsync(path, cancellationToken);

        // PhysicalFile: dosya AKIŞ hâlinde verilir, belleğe alınmaz. Gerekçe IFileService'te.
        return PhysicalFile(physicalPath, contentType, fileName);
    }

    [HttpPost("upload")]
    [RequestSizeLimit(1024L * 1024L * 200L)]
    public async Task<ActionResult<ApiResponse<object>>> UploadAsync([FromQuery] string path, IFormFile file, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureFilesPathAsync(customerScopes, User, path, allowRoot: false, cancellationToken);

        // Disk kotası: limiti olan müşterinin kotası uygulanır. Customer kendi kotasına, Reseller
        // ise yüklediği sitenin SAHİBİ müşterinin kotasına tabidir; admin kotasız.
        Guid? quotaOwner = null;
        if (CustomerAccess.IsCustomer(User))
        {
            quotaOwner = User.GetRequiredUserId();
        }
        else if (CustomerAccess.IsReseller(User))
        {
            var segments = (path ?? string.Empty).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2 && string.Equals(segments[0], "sites", StringComparison.OrdinalIgnoreCase))
            {
                quotaOwner = await customerScopes.GetSiteOwnerUserIdAsync(segments[1], cancellationToken);
            }
        }

        if (quotaOwner is { } ownerId)
        {
            var scope = await customerScopes.GetScopeAsync(ownerId, cancellationToken);
            if (scope.MaxDiskMb > 0)
            {
                var usageMb = await customerScopes.GetDiskUsageMbAsync(ownerId, cancellationToken);
                var incomingMb = file.Length / (1024 * 1024);
                if (usageMb + incomingMb > scope.MaxDiskMb)
                {
                    throw new Core.Common.AppException("files.quota_exceeded", 403, 403);
                }
            }
        }

        await using var stream = file.OpenReadStream();
        var virtualPath = $"{(path ?? string.Empty).TrimEnd('/', '\\')}/{file.FileName}".TrimStart('/', '\\');
        var savedPath = await fileService.SaveUploadAsync(virtualPath, stream, cancellationToken);
        await auditService.AuditAsync(HttpContext, "files.uploaded", savedPath, $"File uploaded ({file.Length} bytes).");
        return Ok(ApiResponse<object>.Ok(new { path = savedPath }, "files.uploaded"));
    }

    [HttpPut]
    public async Task<ActionResult<ApiResponse<FileWorkspaceDto>>> WriteAsync([FromBody] WriteFileRequest request, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureFilesPathAsync(customerScopes, User, request.Path, allowRoot: false, cancellationToken);
        var workspace = await fileService.WriteAsync(request, cancellationToken);

        // İÇERİK YAZILMAZ, yalnız boyutu: bir dosyanın gövdesi (yapılandırma, bağlantı dizesi,
        // müşteri verisi) denetim kaydına kopyalanırsa kayıt, korumaya çalıştığı sırların ikinci
        // bir deposuna dönüşür.
        await auditService.AuditAsync(
            HttpContext,
            "files.written",
            request.Path,
            $"File content saved ({(request.Content ?? string.Empty).Length} characters).");

        return Ok(ApiResponse<FileWorkspaceDto>.Ok(workspace, "files.saved"));
    }

    [HttpDelete]
    public async Task<ActionResult<ApiResponse<FileWorkspaceDto>>> DeleteAsync([FromBody] DeleteFileRequest request, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureFilesPathAsync(customerScopes, User, request.Path, allowRoot: false, cancellationToken);
        var workspace = await fileService.DeleteAsync(request, cancellationToken);
        await auditService.AuditAsync(HttpContext, "files.deleted", request.Path, "File or directory deleted.");
        return Ok(ApiResponse<FileWorkspaceDto>.Ok(workspace, "files.deleted"));
    }

    [HttpPost("mkdir")]
    public async Task<ActionResult<ApiResponse<FileWorkspaceDto>>> CreateDirectoryAsync([FromBody] CreateDirectoryRequest request, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureFilesPathAsync(customerScopes, User, request.Path, allowRoot: false, cancellationToken);
        var workspace = await fileService.CreateDirectoryAsync(request, cancellationToken);
        await auditService.AuditAsync(HttpContext, "files.directory_created", request.Path, "Directory created.");
        return Ok(ApiResponse<FileWorkspaceDto>.Ok(workspace, "files.directory_created"));
    }

    // ACL değişikliği sistem genelini etkileyebilir (örn. Everyone'a izin); yalnız yönetici.
    [HttpPost("acl")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<FileWorkspaceDto>>> SetPermissionsAsync([FromBody] SetFilePermissionsRequest request, CancellationToken cancellationToken)
    {
        var workspace = await fileService.SetPermissionsAsync(request, cancellationToken);

        // ACL değişikliği bir yetkilendirme olayıdır: kimin neye erişebildiğini değiştirir.
        // Kimin, hangi kullanıcıya, hangi hakkı verdiği tam olarak yazılır.
        await auditService.AuditAsync(
            HttpContext,
            "files.acl_updated",
            request.Path,
            $"Permission '{request.Rights}' granted to '{request.Principal}'.");

        return Ok(ApiResponse<FileWorkspaceDto>.Ok(workspace, "files.acl_updated"));
    }

    [HttpPost("compress")]
    public async Task<ActionResult<ApiResponse<FileWorkspaceDto>>> CompressAsync([FromBody] ArchiveFileRequest request, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureFilesPathAsync(customerScopes, User, request.Path, allowRoot: false, cancellationToken);
        if (!string.IsNullOrWhiteSpace(request.DestinationPath))
        {
            await CustomerAccess.EnsureFilesPathAsync(customerScopes, User, request.DestinationPath, allowRoot: false, cancellationToken);
        }
        var workspace = await fileService.CompressAsync(request, cancellationToken);
        await auditService.AuditAsync(
            HttpContext,
            "files.compressed",
            request.Path,
            $"Archived to '{(string.IsNullOrWhiteSpace(request.DestinationPath) ? "(default)" : request.DestinationPath)}'.");
        return Ok(ApiResponse<FileWorkspaceDto>.Ok(workspace, "files.compressed"));
    }

    [HttpPost("extract")]
    public async Task<ActionResult<ApiResponse<FileWorkspaceDto>>> ExtractAsync([FromBody] ArchiveFileRequest request, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureFilesPathAsync(customerScopes, User, request.Path, allowRoot: false, cancellationToken);
        if (!string.IsNullOrWhiteSpace(request.DestinationPath))
        {
            await CustomerAccess.EnsureFilesPathAsync(customerScopes, User, request.DestinationPath, allowRoot: false, cancellationToken);
        }
        var workspace = await fileService.ExtractAsync(request, cancellationToken);

        // Çıkarma, arşivin içeriğiyle var olan dosyaların ÜZERİNE yazabilir; yani içerik kaybının
        // sessiz yollarından biridir. Hedef de kayda geçer.
        await auditService.AuditAsync(
            HttpContext,
            "files.extracted",
            request.Path,
            $"Extracted to '{(string.IsNullOrWhiteSpace(request.DestinationPath) ? "(default)" : request.DestinationPath)}'.");

        return Ok(ApiResponse<FileWorkspaceDto>.Ok(workspace, "files.extracted"));
    }

    [HttpPost("search")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<FileEntryDto>>>> SearchAsync([FromBody] SearchFilesRequest request, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureFilesPathAsync(customerScopes, User, request.Path, allowRoot: false, cancellationToken);
        var results = await fileService.SearchAsync(request, cancellationToken);
        return Ok(ApiResponse<IReadOnlyCollection<FileEntryDto>>.Ok(results));
    }
}
