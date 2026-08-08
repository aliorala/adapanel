using AdaPanel.Api.Extensions;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

// profesyonel panel benzeri self-servis veritabanı yönetimi. Bayi (ve admin) YALNIZ kendi SAHİP OLDUĞU veritabanlarını
// listeler/oluşturur/siler. Admin DatabasesController (root parola, transfer, kullanıcı yönetimi, uzak erişim)
// BİLEREK ayrı + admin-only kalır — burada parola/kredensiyel ASLA dönmez, başka müşterinin DB'si GÖRÜNMEZ.
// MaxDatabases kotası dayatılır; her DB oluşturana ait kaydedilir, silince kayıt temizlenir.
[ApiController]
[Authorize(Roles = "Administrator,Reseller,Customer")]
[RequireSubUserSection("database")]
[Route("api/v1/my-databases")]
public sealed class ResellerDatabasesController(
    IDatabaseService databaseService,
    IDatabaseOwnershipService dbOwnership,
    ICustomerScopeService customerScopes,
    IAuditService auditService,
    ILogger<ResellerDatabasesController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<OwnedDatabaseDto>>>> GetMineAsync(CancellationToken cancellationToken)
    {
        // Alt-kullanıcıda sahiplik PARENT aboneliğe çözülür → abonelikteki herkes aynı DB listesini paylaşır.
        var owned = await dbOwnership.GetOwnedAsync(
            await CustomerAccess.GetSubscriptionUserIdAsync(customerScopes, User, cancellationToken), cancellationToken);
        var list = owned
            .Select(item => new OwnedDatabaseDto { Engine = item.Engine, Name = item.DatabaseName })
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return Ok(ApiResponse<IReadOnlyCollection<OwnedDatabaseDto>>.Ok(list));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<OwnedDatabaseDto>>>> CreateAsync(
        [FromBody] CreateDatabaseRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.GetRequiredUserId();
        // Sahiplik ve kota abonelik kimliğiyle işler (alt-kullanıcı → parent); audit gerçek aktörle yazılır.
        var subscriptionId = await CustomerAccess.GetSubscriptionUserIdAsync(customerScopes, User, cancellationToken);

        // M1: kota (admin sınırsız; MaxDatabases>0 ise sahip olunan DB sayısı sınırı aşamaz).
        if (!User.IsInRole("Administrator"))
        {
            var scope = await customerScopes.GetScopeAsync(userId, cancellationToken);
            if (scope.MaxDatabases > 0 && await dbOwnership.CountForOwnerAsync(subscriptionId, cancellationToken) >= scope.MaxDatabases)
            {
                throw new AppException("database.quota_exceeded", 403, 403);
            }
        }

        // GÜVENLİK (çapraz-kiracı): istek bir SİTE adı taşıyabiliyor ve servis onu doğrudan
        // siteDatabaseLinks.LinkAsync'e veriyor. Denetlenmezse kiracı kendi veritabanını
        // BAŞKASININ sitesine bağlayabilirdi — o site silindiğinde cascade veritabanını DROP eder.
        if (!string.IsNullOrWhiteSpace(request.SiteName))
        {
            await CustomerAccess.EnsureSiteAsync(customerScopes, User, request.SiteName, cancellationToken);
        }

        // Veritabanı ile sahiplik kaydı TEK PARÇA olmalı. Önceden veritabanı yaratılıyor, sonra
        // kayıt yazılıyordu; arada bir hata (ör. kullanıcı adı zaten var → 409) sahiplik kaydını
        // hiç yazdırmıyor ve sunucuda YETİM bir veritabanı bırakıyordu: kiracı onu listede
        // göremiyor, kotasına sayılmıyor, silmeye kalkınca "size ait değil" hatası alıyordu.
        // Artık kısmi başarı temizleniyor.
        await databaseService.CreateDatabaseAsync(request, cancellationToken);
        try
        {
            await dbOwnership.RecordAsync(request.Engine, request.Name, subscriptionId, null, cancellationToken);
        }
        catch
        {
            try
            {
                await databaseService.DeleteDatabaseAsync(request.Engine, request.Name, CancellationToken.None);
            }
            catch (Exception cleanupException)
            {
                logger.LogError(
                    cleanupException,
                    "Sahiplik kaydı yazılamadı ve veritabanı geri alınamadı; YETİM kalmış olabilir: [{Engine}] {Name}",
                    request.Engine,
                    request.Name);
            }

            throw;
        }
        await auditService.WriteAsync(
            "database.create.completed",
            "database",
            $"[{request.Engine}] '{request.Name}' (self-servis)",
            userId,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
        return await GetMineAsync(cancellationToken);
    }

    [HttpDelete("{name}")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<OwnedDatabaseDto>>>> DeleteAsync(
        string name,
        [FromQuery] string engine,
        CancellationToken cancellationToken)
    {
        var userId = User.GetRequiredUserId();

        // KRİTİK: admin değilse YALNIZ kendi aboneliğinin sahip olduğu DB'yi silebilir (başkasınınkini değil).
        if (!User.IsInRole("Administrator"))
        {
            var owner = await dbOwnership.GetOwnerAsync(engine, name, cancellationToken);
            if (owner != await CustomerAccess.GetSubscriptionUserIdAsync(customerScopes, User, cancellationToken))
            {
                throw new AppException("database.not_owned", 403, 403);
            }
        }

        await databaseService.DeleteDatabaseAsync(engine, name, cancellationToken);
        await dbOwnership.RemoveAsync(engine, name, cancellationToken);
        await auditService.WriteAsync(
            "database.delete.completed",
            "database",
            $"[{engine}] '{name}' (self-servis)",
            userId,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
        return await GetMineAsync(cancellationToken);
    }
}

/// <summary>Self-servis liste için güvenli (parolasız) veritabanı kaydı.</summary>
public sealed class OwnedDatabaseDto
{
    public string Engine { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;
}
