using AdaPanel.Api.Extensions;
using AdaPanel.Api.Security;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Cryptography;

namespace AdaPanel.Api.Controllers;

// Reseller (Bayi) yalnız sahip olduğu müşterilerin sitelerini yönetir; her site-uçlu işlem
// CustomerAccess.EnsureSiteAsync ile etkin kapsama göre korunur. Site OLUŞTURMA/içe aktarma
// admin-only kalır (bayi atama/limit modeli kapsam dışı).
[ApiController]
[Authorize(Roles = "Administrator,Reseller")]
[Route("api/v1/sites")]
public sealed class SitesController(
    IIisSiteService iisSiteService,
    ISiteProvisioningService siteProvisioningService,
    ISiteDefaultsService siteDefaultsService,
    ISiteCascadeDeletionService siteCascadeDeletionService,
    ICustomerScopeService customerScopes,
    IDatabaseOwnershipService dbOwnership,
    IAuditService auditService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<SiteDto>>>> GetSitesAsync(CancellationToken cancellationToken)
    {
        var sites = await iisSiteService.GetSitesAsync(cancellationToken);

        // Bayi yalnızca etkin kapsamındaki siteleri görür.
        var allowed = await CustomerAccess.GetEffectiveSitesAsync(customerScopes, User, cancellationToken);
        if (allowed is not null)
        {
            sites = sites
                .Where(site => allowed.Contains(site.Name, StringComparer.OrdinalIgnoreCase))
                .ToArray();
        }

        return Ok(ApiResponse<IReadOnlyCollection<SiteDto>>.Ok(sites));
    }

    [HttpGet("{siteName}")]
    public async Task<ActionResult<ApiResponse<SiteDto>>> GetSiteAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        return Ok(ApiResponse<SiteDto>.Ok(site));
    }

    // --- Korumalı dizinler (panel "Password-Protected Directories") ---

    [HttpGet("{siteName}/protected-dirs")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<ProtectedDirectoryDto>>>> GetProtectedDirsAsync(
        string siteName,
        [FromServices] IDirectoryProtectionService service,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var items = await service.GetProtectedDirectoriesAsync(siteName, cancellationToken);
        return Ok(ApiResponse<IReadOnlyCollection<ProtectedDirectoryDto>>.Ok(items));
    }

    [HttpPost("{siteName}/protected-dirs")]
    public async Task<ActionResult<ApiResponse<ProtectedDirectoryDto>>> ProtectDirAsync(
        string siteName,
        [FromBody] ProtectDirectoryRequest request,
        [FromServices] IDirectoryProtectionService service,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var dto = await service.ProtectAsync(siteName, request, cancellationToken);
        await WriteAuditAsync("sites.protected_dir.added", $"{siteName}:{dto.RelativePath}", cancellationToken);
        return Ok(ApiResponse<ProtectedDirectoryDto>.Ok(dto, "protected.created"));
    }

    [HttpDelete("{siteName}/protected-dirs")]
    public async Task<ActionResult<ApiResponse<object>>> UnprotectDirAsync(
        string siteName,
        [FromQuery] string relativePath,
        [FromServices] IDirectoryProtectionService service,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        await service.UnprotectAsync(siteName, relativePath, cancellationToken);
        await WriteAuditAsync("sites.protected_dir.removed", $"{siteName}:{relativePath}", cancellationToken);
        return Ok(ApiResponse<object>.Ok(new { }, "protected.removed"));
    }

    /// <summary>
    /// profesyonel panel benzeri birleşik oluşturma: siteyi kurar ve istenirse aynı akışta veritabanı,
    /// mail domaini ve FTP hesabını da best-effort oluşturur (biri kurulamazsa site yine kalır).
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "Administrator,Reseller")]
    public async Task<ActionResult<ApiResponse<SiteCreationResultDto>>> CreateSiteAsync(
        [FromBody] CreateSiteRequest request,
        [FromServices] IDatabaseService databaseService,
        [FromServices] IMailService mailService,
        [FromServices] IFtpService ftpService,
        [FromServices] IDnsService dnsService,
        [FromServices] IComponentBootstrapper componentBootstrapper,
        CancellationToken cancellationToken)
    {
        // M1 (profesyonel paneller gibi): Admin sınırsız; Bayi yalnız planının MaxSites kotası kadar site açabilir.
        await EnsureSiteQuotaAsync(cancellationToken);

        var site = await siteProvisioningService.CreateAndProvisionAsync(
            request,
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        // M1: admin değilse yeni siteyi oluşturanın kapsamına EKLE (erişebilsin + kota sayımı doğru olsun).
        await AssignSiteToActorAsync(site.Name, cancellationToken);
        await WriteAuditAsync("sites.create.completed", site.Name, cancellationToken);

        var result = new SiteCreationResultDto { Site = site };
        var installedComponents = (await componentBootstrapper.GetCatalogAsync(cancellationToken))
            .Installed
            .Where(component => string.Equals(component.Status, "Installed", StringComparison.OrdinalIgnoreCase))
            .Select(component => component.ComponentId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var databaseEngine = ResolveDatabaseEngine(request.DatabaseEngine, installedComponents);
        // Veritabanı YALNIZ kullanıcı açıkça istediğinde oluşturulur (ProvisionDefaultServices
        // artık DB'yi zorlamaz); ismi/kullanıcıyı/parolayı kullanıcı belirleyebilir.
        var shouldCreateDatabase = request.CreateDatabase;
        var shouldCreateMailDomain = request.CreateMailDomain ||
            (request.ProvisionDefaultServices && installedComponents.Contains("stalwart-mail"));
        var shouldCreateFtpUser = request.CreateFtpUser || request.ProvisionDefaultServices;

        // M1 (panel MaxDatabases): DB kotası doluysa DB oluşturmayı atla (site yine oluşur). Admin sınırsız.
        if (shouldCreateDatabase && !string.IsNullOrWhiteSpace(databaseEngine) && await IsDatabaseQuotaReachedAsync(cancellationToken))
        {
            shouldCreateDatabase = false;
            result.Warnings.Add("database: quota reached (max databases in your plan).");
        }

        if (shouldCreateDatabase && !string.IsNullOrWhiteSpace(databaseEngine))
        {
            var dbName = string.IsNullOrWhiteSpace(request.DatabaseName)
                ? AdaPanel.Core.Common.ResourceNaming.DatabaseNameFromSite(site.Name)
                : request.DatabaseName.Trim();
            var databaseUser = string.IsNullOrWhiteSpace(request.DatabaseUserName)
                ? BuildAccountName(site.Name, "db")
                : request.DatabaseUserName.Trim();
            var databasePassword = string.IsNullOrWhiteSpace(request.DatabasePassword)
                ? GeneratePassword()
                : request.DatabasePassword;
            try
            {
                await databaseService.CreateDatabaseAsync(
                    new Core.Dtos.Databases.CreateDatabaseRequest
                    {
                        Engine = databaseEngine,
                        Name = dbName,
                        UserName = databaseUser,
                        Password = databasePassword
                    },
                    cancellationToken);
                result.Created.Add($"{databaseEngine}:{dbName}");
                result.GeneratedCredentials.Add(new GeneratedCredentialDto
                {
                    Service = $"database:{databaseEngine}:{dbName}",
                    UserName = databaseUser,
                    Password = databasePassword
                });
                // M1: yeni DB'yi oluşturan müşteriye + siteye ait kaydet (kota sayımı + sahiplik).
                if (!User.IsInRole("Administrator"))
                {
                    await dbOwnership.RecordAsync(databaseEngine, dbName, User.GetRequiredUserId(), site.Name, cancellationToken);
                }
            }
            catch (Exception exception)
            {
                result.Warnings.Add($"database ({databaseEngine}:{dbName}): {exception.Message}");
            }
        }

        if (shouldCreateMailDomain)
        {
            var domain = string.IsNullOrWhiteSpace(request.HostName) ? site.Name : request.HostName.Trim();
            try
            {
                await mailService.CreateDomainAsync(
                    new Core.Dtos.Mail.CreateMailDomainRequest { DomainName = domain },
                    cancellationToken);
                result.Created.Add($"mail: {domain}");

                // profesyonel paneller gibi: mail domaini sitenin DNS bölgesine MX/SPF/DKIM/DMARC kayıtlarını
                // otomatik yazsın (mail ekranındaki davranışla tutarlı). Best-effort.
                try
                {
                    var mailDns = await mailService.GetDnsRecordsAsync(domain, cancellationToken);
                    foreach (var record in mailDns.Records.Where(r => !string.IsNullOrWhiteSpace(r.Value)))
                    {
                        await dnsService.UpsertRecordAsync(domain, new Core.Dtos.Sites.UpsertDnsRecordRequest
                        {
                            Type = record.Type,
                            Host = record.Host,
                            Value = record.Value,
                            Priority = record.Priority,
                            Ttl = 3600
                        }, cancellationToken);
                    }
                }
                catch
                {
                    // DNS otomatik uygulanamadıysa kullanıcı mail ekranından uygular.
                }
            }
            catch (Exception exception)
            {
                result.Warnings.Add($"mail ({domain}): {exception.Message}");
            }
        }

        if (shouldCreateFtpUser)
        {
            var ftpUserName = string.IsNullOrWhiteSpace(request.FtpUserName)
                ? BuildAccountName(site.Name, "ftp", maxTotalLength: 20)
                : request.FtpUserName.Trim();
            var ftpPassword = string.IsNullOrWhiteSpace(request.FtpPassword)
                ? GeneratePassword()
                : request.FtpPassword;
            try
            {
                await ftpService.CreateFtpUserAsync(
                    new Core.Dtos.Ftp.CreateFtpUserRequest
                    {
                        UserName = ftpUserName,
                        Password = ftpPassword,
                        SiteName = site.Name,
                        HomePath = string.Empty
                    },
                    cancellationToken);
                result.Created.Add($"ftp: {ftpUserName}");
                result.GeneratedCredentials.Add(new GeneratedCredentialDto
                {
                    Service = "ftp",
                    UserName = ftpUserName,
                    Password = ftpPassword
                });
            }
            catch (Exception exception)
            {
                result.Warnings.Add($"ftp ({ftpUserName}): {exception.Message}");
            }
        }

        return Ok(ApiResponse<SiteCreationResultDto>.Ok(result, "sites.create.success"));
    }

    /// <summary>Silmeden önce siteye bağlı tüm kaynakların (profesyonel panel benzeri) önizlemesi.</summary>
    [HttpGet("{siteName}/deletion-plan")]
    public async Task<ActionResult<ApiResponse<SiteDeletionPlanDto>>> GetDeletionPlanAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var plan = await siteCascadeDeletionService.GetPlanAsync(siteName, cancellationToken);
        return Ok(ApiResponse<SiteDeletionPlanDto>.Ok(plan));
    }

    /// <summary>
    /// Siteyi ve ona bağlı tüm kaynakları (mail domaini, veritabanı, FTP, DNS, proxy, kota,
    /// git, müşteri atamaları ve isteğe bağlı dosyalar) profesyonel paneller gibi cascade siler.
    /// </summary>
    [HttpDelete("{siteName}")]
    public async Task<ActionResult<ApiResponse<SiteDeletionResultDto>>> DeleteSiteAsync(
        string siteName,
        CancellationToken cancellationToken,
        [FromQuery] bool deleteFiles = true,
        [FromQuery] bool deleteDatabases = true)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var result = await siteCascadeDeletionService.DeleteAsync(siteName, deleteFiles, deleteDatabases, cancellationToken);
        await dbOwnership.RemoveBySiteAsync(siteName, cancellationToken); // M1: siteye bağlı DB sahiplik kayıtlarını temizle (kota doğru kalsın)
        await WriteAuditAsync("sites.delete.completed", siteName, cancellationToken);
        return Ok(ApiResponse<SiteDeletionResultDto>.Ok(result, "sites.delete.success"));
    }

    [HttpPost("{siteName}/start")]
    public async Task<ActionResult<ApiResponse<SiteDto>>> StartSiteAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var site = await iisSiteService.StartSiteAsync(siteName, cancellationToken);
        await WriteAuditAsync("sites.start.completed", site.Name, cancellationToken);
        return Ok(ApiResponse<SiteDto>.Ok(site, "sites.start.success"));
    }

    [HttpPost("{siteName}/stop")]
    public async Task<ActionResult<ApiResponse<SiteDto>>> StopSiteAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var site = await iisSiteService.StopSiteAsync(siteName, cancellationToken);
        await WriteAuditAsync("sites.stop.completed", site.Name, cancellationToken);
        return Ok(ApiResponse<SiteDto>.Ok(site, "sites.stop.success"));
    }

    [HttpPost("{siteName}/bindings")]
    public async Task<ActionResult<ApiResponse<SiteDto>>> AddBindingAsync(
        string siteName,
        [FromBody] SiteBindingRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var site = await iisSiteService.AddBindingAsync(siteName, request, cancellationToken);
        await WriteAuditAsync("sites.binding.added", site.Name, cancellationToken);
        return Ok(ApiResponse<SiteDto>.Ok(site, "sites.binding.add.success"));
    }

    [HttpDelete("{siteName}/bindings")]
    public async Task<ActionResult<ApiResponse<SiteDto>>> RemoveBindingAsync(
        string siteName,
        [FromQuery] RemoveSiteBindingRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var site = await iisSiteService.RemoveBindingAsync(siteName, request, cancellationToken);
        await WriteAuditAsync("sites.binding.removed", site.Name, cancellationToken);
        return Ok(ApiResponse<SiteDto>.Ok(site, "sites.binding.remove.success"));
    }

    [HttpPost("import")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<SiteDto>>>> ImportExistingSitesAsync(CancellationToken cancellationToken)
    {
        var sites = await iisSiteService.ImportExistingSitesAsync(cancellationToken);
        foreach (var site in sites)
        {
            _ = await siteDefaultsService.EnsureDefaultsAsync(site, cancellationToken);
        }

        return Ok(ApiResponse<IReadOnlyCollection<SiteDto>>.Ok(sites, "sites.import.success"));
    }

    [HttpPost("{siteName}/defaults")]
    public async Task<ActionResult<ApiResponse<SiteDefaultsResultDto>>> EnsureDefaultsAsync(
        string siteName,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var result = await siteDefaultsService.EnsureDefaultsAsync(site, cancellationToken);
        await WriteAuditAsync("sites.defaults.completed", site.Name, cancellationToken);
        return Ok(ApiResponse<SiteDefaultsResultDto>.Ok(
            result,
            result.IsComplete ? "sites.defaults.success" : "sites.defaults.partial"));
    }

    // M1: Bayi/Müşteri site kotası. Admin sınırsız. MaxSites=0 → limitsiz (mevcut davranışı korur).
    private async Task EnsureSiteQuotaAsync(CancellationToken cancellationToken)
    {
        if (User.IsInRole("Administrator"))
        {
            return;
        }

        var scope = await customerScopes.GetScopeAsync(User.GetRequiredUserId(), cancellationToken);
        if (scope.MaxSites > 0 && scope.Sites.Count >= scope.MaxSites)
        {
            throw new Core.Common.AppException("sites.quota_exceeded", 403, 403);
        }
    }

    // M1 (panel MaxDatabases): admin olmayan kullanıcının sahip olduğu DB sayısı kotayı doldurdu mu?
    private async Task<bool> IsDatabaseQuotaReachedAsync(CancellationToken cancellationToken)
    {
        if (User.IsInRole("Administrator"))
        {
            return false;
        }

        var userId = User.GetRequiredUserId();
        var scope = await customerScopes.GetScopeAsync(userId, cancellationToken);
        if (scope.MaxDatabases <= 0)
        {
            return false; // 0 = limitsiz
        }

        return await dbOwnership.CountForOwnerAsync(userId, cancellationToken) >= scope.MaxDatabases;
    }

    // M1: admin olmayan oluşturucunun yeni sitesini kendi kapsamına ekler (yoksa). OwnerId korunur (SetScopeAsync).
    private async Task AssignSiteToActorAsync(string siteName, CancellationToken cancellationToken)
    {
        if (User.IsInRole("Administrator"))
        {
            return;
        }

        var userId = User.GetRequiredUserId();
        var scope = await customerScopes.GetScopeAsync(userId, cancellationToken);
        if (scope.Sites.Contains(siteName, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        var sites = scope.Sites.ToList();
        sites.Add(siteName);
        await customerScopes.SetScopeAsync(userId, sites, scope.MaxSites, scope.MaxDiskMb, cancellationToken);
    }

    private Task WriteAuditAsync(string action, string siteName, CancellationToken cancellationToken)
    {
        return auditService.WriteAsync(
            action,
            "sites",
            $"IIS site '{siteName}' operation completed.",
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
    }

    private static string ResolveDatabaseEngine(string requestedEngine, HashSet<string> installedComponents)
    {
        if (!string.IsNullOrWhiteSpace(requestedEngine))
        {
            return requestedEngine.Trim().ToLowerInvariant();
        }

        if (installedComponents.Contains("mariadb-11"))
        {
            return "mariadb";
        }

        if (installedComponents.Contains("mysql-8"))
        {
            return "mysql";
        }

        return installedComponents.Contains("postgresql-16") ? "postgresql" : string.Empty;
    }

    // FTP kullanıcısı Windows YEREL hesabı olduğundan toplam ad EN FAZLA 20 karakter olmalı
    // (net user limiti). DB kullanıcısı için 32 yeterli. Uzun site adlarında base kırpılırken
    // benzersizliği korumak için kısa kararlı bir hash eklenir (örn. "golftaksiatak_a1b2_ftp").
    private static string BuildAccountName(string siteName, string suffix, int maxTotalLength = 32)
    {
        var normalized = new string(siteName
            .Trim()
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '_')
            .ToArray())
            .Trim('_');
        normalized = string.IsNullOrWhiteSpace(normalized) ? "site" : normalized;

        var maxBaseLength = Math.Max(1, maxTotalLength - suffix.Length - 1);
        if (normalized.Length > maxBaseLength)
        {
            // Kırpılan adda çakışmayı önlemek için 4 karakterlik kararlı hash ekle.
            var code = StableShortCode(normalized);
            var keep = Math.Max(1, maxBaseLength - code.Length - 1);
            normalized = $"{normalized[..keep]}_{code}";
        }

        return $"{normalized}_{suffix}";
    }

    /// <summary>İsimden türetilen, süreçler arası KARARLI 4 karakterlik kod (çakışma önler).</summary>
    private static string StableShortCode(string value)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash, 0, 2).ToLowerInvariant();
    }

    private static string GeneratePassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "!@#$%*-_";
        const string all = upper + lower + digits + symbols;
        var chars = new[]
        {
            upper[RandomNumberGenerator.GetInt32(upper.Length)],
            lower[RandomNumberGenerator.GetInt32(lower.Length)],
            digits[RandomNumberGenerator.GetInt32(digits.Length)],
            symbols[RandomNumberGenerator.GetInt32(symbols.Length)]
        }.ToList();

        while (chars.Count < 20)
        {
            chars.Add(all[RandomNumberGenerator.GetInt32(all.Length)]);
        }

        for (var index = chars.Count - 1; index > 0; index--)
        {
            var swapIndex = RandomNumberGenerator.GetInt32(index + 1);
            (chars[index], chars[swapIndex]) = (chars[swapIndex], chars[index]);
        }

        return new string(chars.ToArray());
    }
}
