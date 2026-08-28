using AdaPanel.Api.Extensions;
using AdaPanel.Api.Security;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Files;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using AdaPanel.Infrastructure.Files;
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

        // Yüklemenin gelen baytı KESİNDİR (istek gövdesinin uzunluğu); ölçmek için diske bakmaya
        // gerek yok, dolayısıyla kesilme sebebi de boştur.
        await EnsureDiskQuotaAsync(path, _ => Task.FromResult((file.Length, string.Empty)), cancellationToken);

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

        // KOTA: yazma da diski büyütür. Ölçülen şey DELTA'dır (yeni içerik − hedefte hâlihazırda
        // duran dosya), gelen içeriğin tamamı değil — aksi hâlde kotası dolmuş bir kullanıcı,
        // sitesini onarmak için bir dosyayı KÜÇÜLTMEK istediğinde bile reddedilirdi ve panelden
        // çıkışı olmayan bir kilide girerdi. Hedef yoksa ölçüm 0 döner (yeni dosya).
        await EnsureDiskQuotaAsync(
            request.Path,
            async ct =>
            {
                var yeniBayt = System.Text.Encoding.UTF8.GetByteCount(request.Content ?? string.Empty);
                var mevcut = await fileService.MeasureAsync([request.Path], ct);
                return (Math.Max(0, yeniBayt - mevcut.TotalBytes), string.Empty);
            },
            cancellationToken);

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

        // KOTA, İŞTEN ÖNCE ÖNGÖREREK. Kopyalama diski kaynağın boyutu kadar büyütür ve bu, kotanın
        // en kolay delindiği yoldu: 400 MB'lık klasörünü tekrar tekrar kopyalayan 500 MB kotalı bir
        // müşteri birimi doldurup KOMŞU KİRACILARI da yazamaz hâle getiriyordu.
        //
        // Ölçüm işten ÖNCE yapılır ve aşan istek HİÇ BAŞLATILMAZ: yarıya kadar kopyalayıp "kota
        // doldu" demek, kullanıcıya hedef klasörde ne olduğu belirsiz bir kalıntı bırakırdı.
        // KOTA SAHİBİ HEDEFE göre çözülür — diski büyüyen site hedeftir.
        await EnsureDiskQuotaAsync(
            request.DestinationPath,
            async ct =>
            {
                var olcum = await fileService.MeasureAsync(request.Paths ?? [], ct);
                return (olcum.TotalBytes, olcum.TruncatedReasonKey);
            },
            cancellationToken);

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

        // KOTA: arşiv açmak, sıkıştırılmış boyutun KATLARI kadar yer açabilir — kotanın en sessiz
        // deliniş yolu buydu. Ölçüm arşivin AÇILMIŞ boyutu üzerinden yapılır (bkz.
        // IFileService.MeasureArchiveContentBytesAsync) ve iş başlamadan önce reddedilir.
        await EnsureDiskQuotaAsync(
            string.IsNullOrWhiteSpace(request.DestinationPath) ? request.Path : request.DestinationPath,
            async ct => (await fileService.MeasureArchiveContentBytesAsync(request.Path, ct), string.Empty),
            cancellationToken);

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
    /// Bir klasörün özyinelemeli boyutunu hesaplar (Plesk File Manager'daki "Calculate size").
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>DENETİM KAYDI YAZILMAZ ve bu bilinçli bir karardır.</b> Uç hiçbir şeyi değiştirmez;
    /// dosyayı okumaz bile, yalnız meta veriden boyut toplar — <c>search</c> ve <c>download</c> ile
    /// aynı sınıftadır ve bu denetleyicinin başındaki kural tam olarak bunu söyler: her okumaya
    /// satır yazmak, kaydı gürültüye boğup asıl değişiklikleri görünmez kılar. Boyut hesabı
    /// arayüzde klasör başına tıklanan bir düğmedir; bir kullanıcı tek oturumda onlarca kez
    /// tetikleyebilir. (Kararın nöbetçisi <c>AuditCoverageTests.Muaflar</c> listesinde
    /// gerekçesiyle yazılıdır — sessizce muaf kalmaz.)
    /// </para>
    /// <para>
    /// <b>Kapsam denetimi yine de ZORUNLU:</b> okuma da bir sızıntı yoludur. Kapsam sorulmasaydı
    /// bir kiracı, komşusunun sitesinin boyutunu ve dosya sayısını ölçebilirdi.
    /// </para>
    /// </remarks>
    [HttpPost("size")]
    public async Task<ActionResult<ApiResponse<DirectorySizeDto>>> CalculateSizeAsync([FromBody] DirectorySizeRequest request, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureFilesPathAsync(customerScopes, User, request.Path, allowRoot: false, cancellationToken);
        var result = await fileService.CalculateSizeAsync(request.Path, cancellationToken);
        return Ok(ApiResponse<DirectorySizeDto>.Ok(result));
    }

    /// <summary>
    /// Bu denetleyicide DİSK KOTASI sormanın TEK yolu — diski büyüten her uç buradan geçer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Kapatılan açık:</b> kota denetimi <c>UploadAsync</c>'in gövdesine gömülüydü ve yalnız
    /// orada vardı. Diski büyüten diğer üç uç (<c>copy</c>, <c>PUT /files</c>, <c>extract</c>) hiç
    /// sormuyordu; kopyalama üzerinden kota tümüyle atlanabiliyor ve dolan birim KOMŞU KİRACILARI
    /// da yazamaz hâle getiriyordu. Denetimin bir uca gömülü olması, yeni bir uç eklendiğinde onun
    /// korumasız doğması demektir — nitekim öyle oldu.
    /// </para>
    /// <para>
    /// <b>ÖLÇÜM GEÇ ÇAĞRILIR (<paramref name="olcum"/> bir fonksiyon, hazır bir sayı değil):</b>
    /// kotası olmayan bir kullanıcı (yönetici, ya da <c>MaxDiskMb = 0</c> olan sınırsız abonelik)
    /// için ölçüm HİÇ YAPILMAZ. Kopyalamada ölçüm bir AĞAÇ YÜRÜYÜŞÜDÜR; onu koşulsuz çağırmak,
    /// kotasız kullanıcıların her kopyalamasına bedava bir tam ağaç taraması eklemek olurdu.
    /// </para>
    /// <para>
    /// <b>Kota SAHİBİ kim:</b> Customer kendi ABONELİĞİNİN kotasına tabidir — alt-kullanıcı ise
    /// bağlı olduğu parent'ın (<c>GetSubscriptionUserIdAsync</c>). Eski kod burada
    /// <c>User.GetRequiredUserId()</c> kullanıyordu ve bu, alt-kullanıcı için YANLIŞ kimlik
    /// döndürüyordu: alt-kullanıcının kendi kapsam kaydında <c>MaxDiskMb = 0</c> (sınırsız) olduğu
    /// için abonelik kotası onun yüklemelerinde HİÇ uygulanmıyordu. Bayi ise dosyanın gittiği
    /// SİTENİN sahibi müşterinin kotasına tabidir; yönetici kotasızdır.
    /// </para>
    /// </remarks>
    private async Task EnsureDiskQuotaAsync(
        string? scopePath,
        Func<CancellationToken, Task<(long Bayt, string KesilmeSebebi)>> olcum,
        CancellationToken cancellationToken)
    {
        Guid? quotaOwner = null;
        if (CustomerAccess.IsCustomer(User))
        {
            quotaOwner = await CustomerAccess.GetSubscriptionUserIdAsync(customerScopes, User, cancellationToken);
        }
        else if (CustomerAccess.IsReseller(User))
        {
            quotaOwner = await ResolveSiteOwnerAsync(scopePath, cancellationToken);
        }

        if (quotaOwner is not { } ownerId)
        {
            return;
        }

        var scope = await customerScopes.GetScopeAsync(ownerId, cancellationToken);
        if (scope.MaxDiskMb <= 0)
        {
            return;
        }

        var (gelenBayt, kesilmeSebebi) = await olcum(cancellationToken);
        var usageMb = await customerScopes.GetDiskUsageMbAsync(ownerId, cancellationToken);

        // KURALIN KENDİSİ ortak yerde ve bağımlılıksız (bkz. FileQuotaGuard): burada yalnız "kimin
        // kotası" sorusu cevaplanır, "sığıyor mu" sorusu değil.
        FileQuotaGuard.EnsureRoom(scope.MaxDiskMb, usageMb, gelenBayt, kesilmeSebebi);
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
