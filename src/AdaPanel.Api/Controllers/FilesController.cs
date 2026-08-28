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
            quotaOwner = await ResolveSiteOwnerAsync(path, cancellationToken);
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
        await WriteAuditAsync("files.uploaded", savedPath, $"File uploaded ({file.Length} bytes).", cancellationToken);
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
        await WriteAuditAsync(
            "files.written",
            request.Path,
            $"File content saved ({(request.Content ?? string.Empty).Length} characters).",
            cancellationToken);

        return Ok(ApiResponse<FileWorkspaceDto>.Ok(workspace, "files.saved"));
    }

    [HttpDelete]
    public async Task<ActionResult<ApiResponse<FileWorkspaceDto>>> DeleteAsync([FromBody] DeleteFileRequest request, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureFilesPathAsync(customerScopes, User, request.Path, allowRoot: false, cancellationToken);
        var workspace = await fileService.DeleteAsync(request, cancellationToken);
        await WriteAuditAsync("files.deleted", request.Path, "File or directory deleted.", cancellationToken);
        return Ok(ApiResponse<FileWorkspaceDto>.Ok(workspace, "files.deleted"));
    }

    [HttpPost("mkdir")]
    public async Task<ActionResult<ApiResponse<FileWorkspaceDto>>> CreateDirectoryAsync([FromBody] CreateDirectoryRequest request, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureFilesPathAsync(customerScopes, User, request.Path, allowRoot: false, cancellationToken);
        var workspace = await fileService.CreateDirectoryAsync(request, cancellationToken);
        await WriteAuditAsync("files.directory_created", request.Path, "Directory created.", cancellationToken);
        return Ok(ApiResponse<FileWorkspaceDto>.Ok(workspace, "files.directory_created"));
    }

    /// <summary>
    /// Bir ya da birden çok ögeyi başka klasöre taşır; tek öge + <c>NewName</c> ile yeniden adlandırır.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Kapsam denetimi HER YOL İÇİN AYRI yapılır.</b> Yalnız hedefi doğrulamak, kiracının
    /// göremeyeceği bir kaynağı kendi klasörüne çekmesine; yalnız kaynağı doğrulamak ise dosyayı
    /// başka kiracının klasörüne bırakmasına izin verirdi. İki yön de birer izolasyon açığıdır,
    /// bu yüzden ikisi de sorulur.
    /// </para>
    /// </remarks>
    [HttpPost("move")]
    public async Task<ActionResult<ApiResponse<MoveFilesResultDto>>> MoveAsync([FromBody] MoveFilesRequest request, CancellationToken cancellationToken)
    {
        foreach (var source in request.Paths ?? [])
        {
            await CustomerAccess.EnsureFilesPathAsync(customerScopes, User, source, allowRoot: false, cancellationToken);
        }

        await CustomerAccess.EnsureFilesPathAsync(customerScopes, User, request.DestinationPath, allowRoot: false, cancellationToken);

        var result = await fileService.MoveAsync(request, cancellationToken);

        // Kayıt, kısmi başarısızlığı da yazar: "hepsi taşındı" diyen bir iz, taşınmayan dosyanın
        // nereye gittiğini soran kişiyi yanlış yönlendirirdi.
        var failures = result.Items
            .Where(item => !item.Moved)
            .Select(item => $"{item.SourcePath} ({item.ErrorKey})")
            .ToArray();

        await WriteAuditAsync(
            "files.moved",
            request.DestinationPath,
            $"{result.MovedCount} of {result.MovedCount + result.FailedCount} item(s) moved."
                + (failures.Length > 0 ? $" Not moved: {string.Join("; ", failures)}." : string.Empty),
            cancellationToken);

        return Ok(ApiResponse<MoveFilesResultDto>.Ok(result, "files.moved"));
    }

    /// <summary>
    /// Bir ya da birden çok ögeyi başka klasöre KOPYALAR; kaynak yerinde kalır.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Kapsam denetimi taşımadakiyle AYNIDIR ve aynı sebeple HER YOL İÇİN AYRI yapılır.</b>
    /// Kopyalama silmediği için "daha zararsız" görünür, ama izolasyon açısından farkı yoktur:
    /// yalnız hedefi doğrulamak, kiracının göremeyeceği bir dosyanın İÇERİĞİNİ kendi klasörüne
    /// çekmesi (yani doğrudan veri sızması) demektir; yalnız kaynağı doğrulamak ise başka kiracının
    /// sitesine dosya bırakmaktır.
    /// </para>
    /// </remarks>
    [HttpPost("copy")]
    public async Task<ActionResult<ApiResponse<CopyFilesResultDto>>> CopyAsync([FromBody] CopyFilesRequest request, CancellationToken cancellationToken)
    {
        foreach (var source in request.Paths ?? [])
        {
            await CustomerAccess.EnsureFilesPathAsync(customerScopes, User, source, allowRoot: false, cancellationToken);
        }

        await CustomerAccess.EnsureFilesPathAsync(customerScopes, User, request.DestinationPath, allowRoot: false, cancellationToken);

        var result = await fileService.CopyAsync(request, cancellationToken);

        // Kayıt, kısmi başarısızlığı da yazar. İZ YALNIZCA YOL VE SEBEP tutar — dosya İÇERİĞİ asla:
        // denetim kaydı, kopyalanan dosyanın gizli verisinin ikinci bir kopyası olamaz.
        var failures = result.Items
            .Where(item => !item.Copied)
            .Select(item => $"{item.SourcePath} ({item.ErrorKey})")
            .ToArray();

        await WriteAuditAsync(
            "files.copied",
            request.DestinationPath,
            $"{result.CopiedCount} of {result.CopiedCount + result.FailedCount} item(s) copied."
                + (failures.Length > 0 ? $" Not copied: {string.Join("; ", failures)}." : string.Empty),
            cancellationToken);

        return Ok(ApiResponse<CopyFilesResultDto>.Ok(result, "files.copied"));
    }

    // ACL değişikliği sistem genelini etkileyebilir (örn. Everyone'a izin); yalnız yönetici.
    [HttpPost("acl")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<FileWorkspaceDto>>> SetPermissionsAsync([FromBody] SetFilePermissionsRequest request, CancellationToken cancellationToken)
    {
        var workspace = await fileService.SetPermissionsAsync(request, cancellationToken);

        // ACL değişikliği bir yetkilendirme olayıdır: kimin neye erişebildiğini değiştirir.
        // Kimin, hangi kullanıcıya, hangi hakkı verdiği tam olarak yazılır.
        await WriteAuditAsync(
            "files.acl_updated",
            request.Path,
            $"Permission '{request.Rights}' granted to '{request.Principal}'.",
            cancellationToken);

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
        await WriteAuditAsync(
            "files.compressed",
            request.Path,
            $"Archived to '{(string.IsNullOrWhiteSpace(request.DestinationPath) ? "(default)" : request.DestinationPath)}'.",
            cancellationToken);
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
        await WriteAuditAsync(
            "files.extracted",
            request.Path,
            $"Extracted to '{(string.IsNullOrWhiteSpace(request.DestinationPath) ? "(default)" : request.DestinationPath)}'.",
            cancellationToken);

        return Ok(ApiResponse<FileWorkspaceDto>.Ok(workspace, "files.extracted"));
    }

    [HttpPost("search")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<FileEntryDto>>>> SearchAsync([FromBody] SearchFilesRequest request, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureFilesPathAsync(customerScopes, User, request.Path, allowRoot: false, cancellationToken);
        var results = await fileService.SearchAsync(request, cancellationToken);
        return Ok(ApiResponse<IReadOnlyCollection<FileEntryDto>>.Ok(results));
    }

    /// <summary>
    /// Bu denetleyicideki denetim kaydı yazmanın TEK yolu.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Aktörün yanına eylemin KONUSU olan aboneliği de koyar. Gerekçe: kayıtlar yalnız aktöre göre
    /// kapsamlandığında, bir YÖNETİCİNİN kiracının sitesinde yaptığı silme/yazma işlemi kiracının
    /// kendi denetim görünümünde hiç çıkmıyordu; "benim siteme kim dokundu" sorusu cevapsız
    /// kalıyordu. Sahibi her çağrı yerinde elle çözmek yerine burada bir kez çözülür — kopyalanmış
    /// bir kalıpta birini doldurup diğerini unutmak an meselesidir.
    /// </para>
    /// </remarks>
    private async Task WriteAuditAsync(string action, string? path, string details, CancellationToken cancellationToken)
    {
        await auditService.AuditAsync(
            HttpContext,
            action,
            path ?? string.Empty,
            details,
            await ResolveSiteOwnerAsync(path, cancellationToken));
    }

    /// <summary>
    /// Sanal dosya yolundan (<c>sites/&lt;site&gt;/...</c>) o sitenin sahibi müşteriyi çözer.
    /// Yol bir siteye işaret etmiyorsa ya da site kimseye atanmamışsa <c>null</c> döner.
    /// </summary>
    private async Task<Guid?> ResolveSiteOwnerAsync(string? path, CancellationToken cancellationToken)
    {
        var segments = (path ?? string.Empty).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || !string.Equals(segments[0], "sites", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return await customerScopes.GetSiteOwnerUserIdAsync(segments[1], cancellationToken);
    }
}
