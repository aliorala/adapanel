using AdaPanel.Api.Extensions;
using AdaPanel.Api.Security;
using System.Security.Claims;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Files;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Audit;
using AdaPanel.Infrastructure.Files;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Security;
using AdaPanel.Infrastructure.Support;
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
    IAuditService auditService,
    ConcurrentWorkGate agirIsKapisi) : ControllerBase
{
    /// <summary>
    /// Bir kiracının AYNI ANDA koşturabileceği ağır dosya işi sayısı.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>KAPATILAN AÇIK:</b> sıkıştırma, arşiv açma, kopyalama ve boyut hesabı <c>Customer</c>
    /// rolüne açık, LocalSystem olarak ve istek iş parçacığında koşuyor. Öge/süre bütçeleri TEK
    /// isteğin ne kadar süreceğini sınırlıyordu; kaç isteğin aynı anda koşacağını sınırlayan
    /// hiçbir şey yoktu. Boyut hesabı en ucuz yoldu: kimlik doğrulamalı bir <c>POST</c>, hız
    /// sınırsız ve her çağrı tam hızda 15 saniyeye kadar ağaç yürüyüşü — N istek = N tarama.
    /// </para>
    /// <para>
    /// <b>DEĞER NEDEN 2:</b> panelin MEŞRU eşzamanlı kullanımı ölçüldü — kullanıcı bir klasörü
    /// sıkıştırırken başka bir sekmede boyut hesaplatabilir ya da bir yedeği açarken dosya
    /// kopyalayabilir. 1 bunu kırardı ("çalışmıyor" gibi görünürdü). 2, gerçek kullanımı bozmadan
    /// çarpanı sabitler. Tavan KİRACI BAŞINADIR: komşu kiracı bundan etkilenmez.
    /// </para>
    /// </remarks>
    private const int AgirIsSiniri = 2;

    /// <summary>Ağır dosya işleri TEK bütçeyi paylaşır: kiracının aynı anda 2 ağır işi olabilir, türü ne olursa olsun.</summary>
    /// <remarks>
    /// Uç başına ayrı bütçe verilseydi tavan uç sayısıyla ÇARPILIRDI (4 uç × 2 = 8) ve sınır fiilen
    /// kalkardı. Aynı karar <c>FileService</c>'in yürüyüş bütçelerinde de yazılı ("TEK BÜTÇE,
    /// BÜTÜN ÖGELER").
    /// </remarks>
    private const string AgirIsAilesi = "files.heavy";

    [HttpGet]
    public async Task<ActionResult<ApiResponse<FileWorkspaceDto>>> GetWorkspaceAsync([FromQuery] string? path, CancellationToken cancellationToken)
    {
        var requestedPath = path ?? string.Empty;
        using var read = await CaptureFileUseAsync([requestedPath], readOnly: true, cancellationToken);

        // Customer/Reseller için kök listesi yalnızca etkin sitelerini gösterir (diğer site adları sızmaz).
        var normalized = requestedPath.Replace('\\', '/').Trim('/');
        var scopedSites = await CustomerAccess.GetEffectiveSitesAsync(customerScopes, read.Principal, cancellationToken);
        if (scopedSites is not null
            && (normalized.Length == 0 || string.Equals(normalized, "sites", StringComparison.OrdinalIgnoreCase)))
        {
            // Re-read the scope after initial actor/scope waits. The first snapshot must not
            // disclose site names removed while this request was queued.
            scopedSites = await CustomerAccess.GetEffectiveSitesAsync(customerScopes, read.Principal, cancellationToken) ?? [];
            await read.RevalidateAsync(cancellationToken);
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

        var result = await fileService.GetWorkspaceAsync(requestedPath, read.ApproveAsync, cancellationToken);
        await read.RevalidateAsync(cancellationToken);
        return Ok(ApiResponse<FileWorkspaceDto>.Ok(ProjectWorkspace(result, read.Principal)));
    }

    [HttpGet("download")]
    public async Task<IActionResult> DownloadAsync([FromQuery] string path, CancellationToken cancellationToken)
    {
        using var read = await CaptureFileUseAsync([path], readOnly: true, cancellationToken);
        var (fileName, contentType, content) = await fileService.OpenDownloadAsync(path, read.ApproveAsync, cancellationToken);
        try
        {
            await read.RevalidateAsync(cancellationToken);
            // MVC owns the verified open handle, so response execution cannot reopen a different
            // object at this path. An interrupted response also disposes the stream.
            return File(content, contentType, fileName);
        }
        catch { await content.DisposeAsync(); throw; }
    }

    [HttpPost("upload")]
    // SINIR TEK KAYNAKTAN. Buradaki sayi elle yazildigi surece FORM AYRISTIRICISININ ayri tavani
    // (FormOptions.MultipartBodyLengthLimit, varsayilan 128 MiB) ile hizasiz kaliyordu: uc
    // "200 MiB serbest" derken 128 MiB ustu her yukleme ayristiricida InvalidDataException ile
    // oluyordu. Ikisi artik ayni sabiti okuyor (bkz. PanelServerLimits.FileUploadMaxBytes).
    [RequestSizeLimit(PanelServerLimits.FileUploadMaxBytes)]
    public async Task<ActionResult<ApiResponse<object>>> UploadAsync([FromQuery] string path, IFormFile file, CancellationToken cancellationToken)
    {
        var virtualPath = $"{(path ?? string.Empty).TrimEnd('/', '\\')}/{file.FileName}".TrimStart('/', '\\');
        var length = file.Length;
        using var publication = await CapturePublicationAsync([path ?? string.Empty, virtualPath], cancellationToken);
        // Üzerine yükleme mevcut dosyayı değiştirir: kota yalnız net büyümeyi sayar.
        // Aksi halde kotası dolmuş müşteri aynı dosyayı küçülterek bile onaramaz.
        await EnsureDiskQuotaAsync(path, async ct =>
        {
            var existing = await fileService.MeasureAsync([virtualPath], ct);
            return (Math.Max(0, length - existing.TotalBytes), string.Empty);
        }, cancellationToken, publication.Principal);

        await using var stream = file.OpenReadStream();
        var savedPath = await fileService.SaveUploadAsync(virtualPath, stream, publication.ApproveAsync, cancellationToken);
        await publication.RevalidateAsync(cancellationToken);
        await WriteAuditAsync("files.uploaded", savedPath, $"File uploaded ({length} bytes).", cancellationToken);
        await publication.RevalidateAsync(cancellationToken);
        return Ok(ApiResponse<object>.Ok(new { path = savedPath }, "files.uploaded"));
    }

    [HttpPut]
    public async Task<ActionResult<ApiResponse<FileWorkspaceDto>>> WriteAsync([FromBody] WriteFileRequest request, CancellationToken cancellationToken)
    {
        request = new WriteFileRequest { Path = request.Path, Content = request.Content, FailIfExists = request.FailIfExists };
        using var publication = await CapturePublicationAsync([request.Path], cancellationToken);

        // KOTA: yazma da diski büyütür. Ölçülen şey DELTA'dır (yeni içerik − hedefte hâlihazırda
        // duran dosya), gelen içeriğin tamamı değil — aksi hâlde kotası dolmuş bir kullanıcı,
        // sitesini onarmak için bir dosyayı KÜÇÜLTMEK istediğinde bile reddedilirdi ve panelden
        // çıkışı olmayan bir kilide girerdi. Hedef yoksa ölçüm 0 döner (yeni dosya).
        var workspace = await fileService.WriteAsync(request, publication.ApproveAsync,
            (growth, token) => EnsureDiskQuotaAsync(request.Path,
                _ => Task.FromResult((growth, string.Empty)), token, publication.Principal), cancellationToken);

        await publication.RevalidateAsync(cancellationToken);
        // İÇERİK YAZILMAZ, yalnız boyutu: bir dosyanın gövdesi (yapılandırma, bağlantı dizesi,
        // müşteri verisi) denetim kaydına kopyalanırsa kayıt, korumaya çalıştığı sırların ikinci
        // bir deposuna dönüşür.
        await WriteAuditAsync(
            "files.written",
            request.Path,
            $"File content saved ({(request.Content ?? string.Empty).Length} characters).",
            cancellationToken);
        await publication.RevalidateAsync(cancellationToken);

        return Ok(ApiResponse<FileWorkspaceDto>.Ok(ProjectWorkspace(workspace, publication.Principal), "files.saved"));
    }

    [HttpDelete]
    public async Task<ActionResult<ApiResponse<FileWorkspaceDto>>> DeleteAsync([FromBody] DeleteFileRequest request, CancellationToken cancellationToken)
    {
        request = new DeleteFileRequest { Path = request.Path };
        using var publication = await CapturePublicationAsync([request.Path], cancellationToken);
        var workspace = await fileService.DeleteAsync(request, publication.ApproveAsync, cancellationToken);
        await publication.RevalidateAsync(cancellationToken);
        await WriteAuditAsync("files.deleted", request.Path, "File or directory deleted.", cancellationToken);
        await publication.RevalidateAsync(cancellationToken);
        return Ok(ApiResponse<FileWorkspaceDto>.Ok(ProjectWorkspace(workspace, publication.Principal), "files.deleted"));
    }

    [HttpPost("mkdir")]
    public async Task<ActionResult<ApiResponse<FileWorkspaceDto>>> CreateDirectoryAsync([FromBody] CreateDirectoryRequest request, CancellationToken cancellationToken)
    {
        request = new CreateDirectoryRequest { Path = request.Path };
        using var publication = await CapturePublicationAsync([request.Path], cancellationToken);
        var workspace = await fileService.CreateDirectoryAsync(request, publication.ApproveAsync, cancellationToken);
        await publication.RevalidateAsync(cancellationToken);
        await WriteAuditAsync("files.directory_created", request.Path, "Directory created.", cancellationToken);
        await publication.RevalidateAsync(cancellationToken);
        return Ok(ApiResponse<FileWorkspaceDto>.Ok(ProjectWorkspace(workspace, publication.Principal), "files.directory_created"));
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
        request = new MoveFilesRequest { Paths = (request.Paths ?? []).ToArray(), DestinationPath = request.DestinationPath,
            NewName = request.NewName, Overwrite = request.Overwrite };
        using var publication = await CapturePublicationAsync([.. request.Paths, request.DestinationPath], cancellationToken);
        var result = await fileService.MoveAsync(request, publication.ApproveAsync, async (source, target, measure, token) =>
        {
            if (!CustomerAccess.IsScoped(publication.Principal)) return;
            var sourceOwner = await ResolveSiteOwnerAsync(source, token);
            var targetOwner = await ResolveSiteOwnerAsync(target, token);
            // A rename or a move within one subscription does not add usage to that subscription.
            if (sourceOwner is not null && sourceOwner == targetOwner) return;
            await EnsureDiskQuotaAsync(target, async ct => (await measure(ct), string.Empty), token, publication.Principal);
        }, cancellationToken);
        await publication.RevalidateAsync(cancellationToken);

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

        await publication.RevalidateAsync(cancellationToken);
        result.Workspace = ProjectWorkspace(result.Workspace, publication.Principal);
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
        request = new CopyFilesRequest { Paths = (request.Paths ?? []).ToArray(), DestinationPath = request.DestinationPath,
            NewName = request.NewName, Overwrite = request.Overwrite };
        using var publication = await CapturePublicationAsync([.. request.Paths, request.DestinationPath], cancellationToken);

        // Tavan ÖLÇÜMDEN de önce alınır: kota ölçümü bir AĞAÇ YÜRÜYÜŞÜDÜR, yani kendisi de ağır
        // bir iştir ve sınırsız bırakılırsa kopyalama hiç başlamadan sunucu tüketilebilirdi.
        using var kapsam = await AgirIseGirisAsync(cancellationToken, publication.Principal);

        // Measure the immutable bytes that this item will publish, subtracting a protected
        // overwrite target. An earlier Measure(path) can describe a different source file.
        var result = await fileService.CopyAsync(request, publication.ApproveAsync,
            (_, target, measure, token) => EnsureDiskQuotaAsync(target,
                async ct => (await measure(ct), string.Empty), token, publication.Principal), cancellationToken);
        await publication.RevalidateAsync(cancellationToken);

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

        await publication.RevalidateAsync(cancellationToken);
        result.Workspace = ProjectWorkspace(result.Workspace, publication.Principal);
        return Ok(ApiResponse<CopyFilesResultDto>.Ok(result, "files.copied"));
    }

    // ACL değişikliği sistem genelini etkileyebilir (örn. Everyone'a izin); yalnız yönetici.
    [HttpPost("acl")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<FileWorkspaceDto>>> SetPermissionsAsync([FromBody] SetFilePermissionsRequest request, CancellationToken cancellationToken)
    {
        request = new SetFilePermissionsRequest { Path = request.Path, Principal = request.Principal, Rights = request.Rights };
        // The attribute protects the initial HTTP boundary; retain that policy for the
        // immutable actor that is revalidated after site/native/metadata waits as well.
        if (!User.IsInRole("Administrator"))
            throw new AdaPanel.Core.Common.AppException("auth.forbidden", 403, 403);
        using var publication = await CapturePublicationAsync([request.Path], cancellationToken);
        var workspace = await fileService.SetPermissionsAsync(request, publication.ApproveAsync, cancellationToken);
        await publication.RevalidateAsync(cancellationToken);
        var outcomeKey = workspace.PermissionsVerified == true ? "files.acl_updated" : "files.acl_unverified";

        // ACL değişikliği bir yetkilendirme olayıdır: kimin neye erişebildiğini değiştirir.
        // Kimin, hangi kullanıcıya, hangi hakkı verdiği tam olarak yazılır.
        await WriteAuditAsync(
            outcomeKey,
            request.Path,
            $"ACL command for '{request.Principal}:{request.Rights}'. {workspace.PermissionsDetail}",
            cancellationToken);
        await publication.RevalidateAsync(cancellationToken);
        return Ok(ApiResponse<FileWorkspaceDto>.Ok(workspace, outcomeKey));
    }

    [HttpPost("compress")]
    public async Task<ActionResult<ApiResponse<FileWorkspaceDto>>> CompressAsync([FromBody] ArchiveFileRequest request, CancellationToken cancellationToken)
    {
        request = new ArchiveFileRequest { Path = request.Path, DestinationPath = request.DestinationPath, Overwrite = request.Overwrite };
        using var publication = await CapturePublicationAsync(string.IsNullOrWhiteSpace(request.DestinationPath)
            ? [request.Path] : [request.Path, request.DestinationPath], cancellationToken);

        // EŞZAMANLILIK TAVANI KAPSAM DENETİMİNDEN SONRA: yer ayırmadan önce isteğin meşru olduğu
        // bilinmeli, yoksa kapsam dışı bir istek de kiracının bütçesini tüketirdi.
        using var kapsam = await AgirIseGirisAsync(cancellationToken, publication.Principal);
        var workspace = await fileService.CompressAsync(request, publication.ApproveAsync,
            (bytes, token) => EnsureDiskQuotaAsync(
                string.IsNullOrWhiteSpace(request.DestinationPath) ? request.Path : request.DestinationPath,
                _ => Task.FromResult((bytes, string.Empty)), token, publication.Principal), cancellationToken);
        await publication.RevalidateAsync(cancellationToken);
        await WriteAuditAsync(
            "files.compressed",
            request.Path,
            $"Archived to '{(string.IsNullOrWhiteSpace(request.DestinationPath) ? "(default)" : request.DestinationPath)}'.",
            cancellationToken);
        await publication.RevalidateAsync(cancellationToken);
        return Ok(ApiResponse<FileWorkspaceDto>.Ok(ProjectWorkspace(workspace, publication.Principal), "files.compressed"));
    }

    [HttpPost("extract")]
    public async Task<ActionResult<ApiResponse<FileWorkspaceDto>>> ExtractAsync([FromBody] ArchiveFileRequest request, CancellationToken cancellationToken)
    {
        request = new ArchiveFileRequest { Path = request.Path, DestinationPath = request.DestinationPath };
        using var publication = await CapturePublicationAsync(string.IsNullOrWhiteSpace(request.DestinationPath)
            ? [request.Path] : [request.Path, request.DestinationPath], cancellationToken);

        // Tavan ÖLÇÜMDEN de önce alınır: kota ölçümü arşivin girdi listesini gezer, yani kendisi
        // de ağır bir iştir.
        using var kapsam = await AgirIseGirisAsync(cancellationToken, publication.Principal);

        // Ölçülen ZIP, kota beklemesi ve çıkarma boyunca hizmette aynı açık dosya olarak tutulur.
        var workspace = await fileService.ExtractAsync(request, publication.ApproveAsync,
            (bytes, token) => EnsureDiskQuotaAsync(
                string.IsNullOrWhiteSpace(request.DestinationPath) ? request.Path : request.DestinationPath,
                _ => Task.FromResult((bytes, string.Empty)), token, publication.Principal), cancellationToken);
        await publication.RevalidateAsync(cancellationToken);

        // Çıkarma, arşivin içeriğiyle var olan dosyaların ÜZERİNE yazabilir; yani içerik kaybının
        // sessiz yollarından biridir. Hedef de kayda geçer.
        await WriteAuditAsync(
            "files.extracted",
            request.Path,
            $"Extracted to '{(string.IsNullOrWhiteSpace(request.DestinationPath) ? "(default)" : request.DestinationPath)}'.",
            cancellationToken);

        await publication.RevalidateAsync(cancellationToken);
        return Ok(ApiResponse<FileWorkspaceDto>.Ok(ProjectWorkspace(workspace, publication.Principal), "files.extracted"));
    }

    [HttpPost("search")]
    public async Task<ActionResult<ApiResponse<FileSearchResultDto>>> SearchAsync([FromBody] SearchFilesRequest request, CancellationToken cancellationToken)
    {
        request = new SearchFilesRequest { Path = request.Path, Query = request.Query };
        using var read = await CaptureFileUseAsync([request.Path], readOnly: true, cancellationToken, allowVirtualRoot: false);

        // ARAMA DA AĞIR İŞ AİLESİNDEDİR ve bu ölçülerek eklendi: uç 5.000 dosyaya kadar geziyor ve
        // her uygun dosyanın 2 MB'a kadarını İÇERİK OLARAK okuyor. Ailenin dışında bırakmak, tam
        // da bu depoda tekrarlayan hatayı yapmak olurdu — koruma bir uca gömülür, kardeş uç
        // korumasız kalır.
        using var kapsam = await AgirIseGirisAsync(cancellationToken, read.Principal);
        var results = await fileService.SearchMeasuredAsync(request, read.ApproveAsync, cancellationToken);
        await read.RevalidateAsync(cancellationToken);
        return Ok(ApiResponse<FileSearchResultDto>.Ok(results));
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
        request = new DirectorySizeRequest { Path = request.Path };
        using var read = await CaptureFileUseAsync([request.Path], readOnly: true, cancellationToken, allowVirtualRoot: false);

        // BU UÇ EN UCUZ SALDIRI YOLUYDU: hız sınırı yok, gövdesi bir satır, ve her çağrı tam
        // hızda 15 saniyeye kadar ağaç yürüyüşü başlatıyor. Tek isteğin bütçesi vardı, istek
        // SAYISININ bütçesi yoktu.
        using var kapsam = await AgirIseGirisAsync(cancellationToken, read.Principal);
        var result = await fileService.CalculateSizeAsync(request.Path, read.ApproveAsync, cancellationToken);
        await read.RevalidateAsync(cancellationToken);
        return Ok(ApiResponse<DirectorySizeDto>.Ok(result));
    }

    /// <summary>
    /// Bu denetleyicide EŞZAMANLILIK sınırlamasının TEK yolu — ağır her uç buradan geçer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>KURALIN KENDİSİ BURADA DEĞİL</b> (bkz. <see cref="ConcurrentWorkGate"/>): burada yalnız
    /// "kimin bütçesi" sorusu cevaplanır. Sayma/reddetme mantığı iki yere yazılsaydı biri
    /// sıkılaştırılıp diğeri unutulurdu — bu depoda tam olarak o hata sınıfı defalarca ölçüldü.
    /// </para>
    /// <para>
    /// <b>BÜTÇENİN SAHİBİ, KOTANIN SAHİBİYLE AYNI KİŞİDİR</b> (bkz.
    /// <see cref="EnsureDiskQuotaAsync"/>): <c>Customer</c> kendi ABONELİĞİNİN bütçesine tabidir —
    /// alt kullanıcı bağlı olduğu parent'ın. Bütçe oturum açan kullanıcıya göre verilseydi, bir
    /// müşteri kendi açtığı alt kullanıcılarla tavanı istediği kadar çoğaltırdı. Bayi ve yönetici
    /// kendi kimliğiyle sayılır: sitesi olmayan yollarda (panel kökü) sahip çözülemez ve onları
    /// kotasız bırakmak gibi SINIRSIZ bırakmak, korumayı en yetkili hesapta kaldırmak olurdu.
    /// </para>
    /// </remarks>
    private async Task<IDisposable> AgirIseGirisAsync(CancellationToken cancellationToken, ClaimsPrincipal? acceptedPrincipal = null)
    {
        var principal = acceptedPrincipal ?? User;
        var sahip = CustomerAccess.IsCustomer(principal)
            ? await CustomerAccess.GetSubscriptionUserIdAsync(customerScopes, principal, cancellationToken)
            : principal.GetRequiredUserId();

        return agirIsKapisi.Enter($"{AgirIsAilesi}:{sahip:N}", AgirIsSiniri, "files.too_many_operations");
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
        CancellationToken cancellationToken, ClaimsPrincipal? acceptedPrincipal = null)
    {
        var principal = acceptedPrincipal ?? User;
        Guid? quotaOwner = null;
        if (CustomerAccess.IsCustomer(principal))
        {
            quotaOwner = await CustomerAccess.GetSubscriptionUserIdAsync(customerScopes, principal, cancellationToken);
        }
        else if (CustomerAccess.IsReseller(principal))
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

        // ÖLÇÜM SONUCU OLDUĞU GİBİ GEÇER — "kaç MB" diye tek bir sayıya indirilmez. Kullanım
        // bütçeli bir ağaç yürüyüşünden gelir ve bir ALT SINIR olabilir; burada yalnız megabayt
        // sayısını almak, kuralın bilmek zorunda olduğu "bu sayı ölçüldü mü" bilgisini tam da
        // zorlama yolunda düşürmek olurdu. Belirsizlikle ne yapıldığı ORTAK kuralda yazılıdır
        // (FileQuotaGuard) ve nöbetçisi FileQuotaTests'tedir.
        var kullanim = await customerScopes.GetDiskUsageMbAsync(ownerId, cancellationToken);

        // KURALIN KENDİSİ ortak yerde ve bağımlılıksız (bkz. FileQuotaGuard): burada yalnız "kimin
        // kotası" sorusu cevaplanır, "sığıyor mu" sorusu değil.
        FileQuotaGuard.EnsureRoom(scope.MaxDiskMb, kullanim, gelenBayt, kesilmeSebebi);
    }

    private Task<PublicationUse> CapturePublicationAsync(string[] paths, CancellationToken token) =>
        CaptureFileUseAsync(paths, readOnly: false, token);

    private async Task<PublicationUse> CaptureFileUseAsync(string[] paths, bool readOnly, CancellationToken token, bool allowVirtualRoot = true)
    {
        var principal = new ClaimsPrincipal(User.Identities.Select(identity => new ClaimsIdentity(identity)));
        var services = HttpContext.RequestServices;
        var db = services.GetRequiredService<AdaPanelDbContext>();
        var sessions = services.GetRequiredService<SessionRevocationStore>();
        var sites = services.GetRequiredService<IIisSiteService>();
        var gates = services.GetRequiredService<IConcurrencyLock>();
        var targets = new Dictionary<string, SiteMutationGuard.Target>(StringComparer.OrdinalIgnoreCase);
        PublicationUse? use = null;
        try
        {
            var names = paths.Select(path => (path ?? string.Empty).Replace('\\', '/')
                    .Split('/', StringSplitOptions.RemoveEmptyEntries))
                .Select(segments => segments.Length >= 2 && string.Equals(segments[0], "sites", StringComparison.OrdinalIgnoreCase)
                    ? segments[1] : readOnly && allowVirtualRoot && (segments.Length == 0 || segments.Length == 1 && string.Equals(segments[0], "sites", StringComparison.OrdinalIgnoreCase))
                        ? null : throw new AdaPanel.Core.Common.AppException(
                            readOnly && CustomerAccess.IsScoped(principal) ? "auth.site_forbidden" : "files.site_required",
                            readOnly && CustomerAccess.IsScoped(principal) ? 403 : 400,
                            readOnly && CustomerAccess.IsScoped(principal) ? 403 : 400))
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
            // Capture the requested generation before actor/scope database waits. A replacement
            // during those waits must not become the newly approved destination of this request.
            foreach (var name in names)
                targets.Add(name, await SiteMutationGuard.CaptureAsync(sites, gates, name, token));
            var actor = await FileActorAuthority.CaptureAsync(db, customerScopes, sessions, principal, token, readOnly);
            async Task Revalidate(CancellationToken currentToken)
            {
                await actor.RevalidateAsync(db, customerScopes, sessions, principal, currentToken);
                foreach (var path in paths)
                    await CustomerAccess.EnsureFilesPathAsync(customerScopes, principal, path, allowRoot: readOnly && allowVirtualRoot, currentToken);
                currentToken.ThrowIfCancellationRequested();
            }
            use = new PublicationUse(targets, Revalidate, principal);
            await Revalidate(token);
            foreach (var target in use.Targets.Values)
                use.Pins.Add(await target.ReserveAsync((_, currentToken) => Revalidate(currentToken), token));
            return use;
        }
        catch
        {
            if (use is not null) use.Dispose();
            else foreach (var target in targets.Values.Reverse()) target.Dispose();
            throw;
        }
    }

    private sealed class PublicationUse(Dictionary<string, SiteMutationGuard.Target> targets,
        Func<CancellationToken, Task> revalidate, ClaimsPrincipal principal) : IDisposable
    {
        internal Dictionary<string, SiteMutationGuard.Target> Targets { get; } = targets;
        internal ClaimsPrincipal Principal { get; } = principal;
        internal List<IDisposable> Pins { get; } = [];
        internal Task RevalidateAsync(CancellationToken token) => revalidate(token);
        public Task ApproveAsync(SiteDto? measured, CancellationToken token)
        {
            if (measured is null && Targets.Count == 0) return revalidate(token);
            if (measured is null) throw new AdaPanel.Core.Common.AppException("auth.site_forbidden", 403, 403);
            if (!Targets.TryGetValue(measured.Name, out var target))
                throw new AdaPanel.Core.Common.AppException("auth.site_forbidden", 403, 403);
            return target.RunAsync(async _ =>
            {
                // Validate the actual snapshot used to resolve the file, as well as
                // the site's fresh identity. A later matching read cannot bless a
                // different root that the file service resolved in between.
                target.ValidateSnapshot(measured);
                await revalidate(token);
            }, token);
        }
        public void Dispose()
        {
            foreach (var pin in Pins.AsEnumerable().Reverse()) pin.Dispose();
            foreach (var target in Targets.Values.Reverse()) target.Dispose();
        }
    }

    private static FileWorkspaceDto ProjectWorkspace(FileWorkspaceDto source, ClaimsPrincipal principal) => new()
    {
        Path = source.Path, ParentPath = source.ParentPath, DisplayName = source.DisplayName,
        PhysicalPath = CustomerAccess.IsScoped(principal) ? string.Empty : source.PhysicalPath,
        IsDirectory = source.IsDirectory, Content = source.Content, ContentLoaded = source.ContentLoaded,
        ContentUnavailableReasonKey = source.ContentUnavailableReasonKey, Entries = source.Entries
    };

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
