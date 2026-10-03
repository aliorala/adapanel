using AdaPanel.Api.Extensions;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Database;
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
    AdaPanelDbContext dbContext) : ControllerBase
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
        var authority = await DatabaseActorAuthority.CaptureAsync(dbContext, customerScopes, User, cancellationToken);
        var userId = authority.UserId;
        // Sahiplik ve kota abonelik kimliğiyle işler (alt-kullanıcı → parent); audit gerçek aktörle yazılır.
        var subscriptionId = authority.SubscriptionId;

        // Count, provider creation and ownership commit share the subscription/resource leases.
        // Different database names must not both consume the same final quota slot.
        using (await DatabaseLifecycle.EnterSubscriptionAsync(subscriptionId, cancellationToken))
        {
            await authority.RevalidateAsync(dbContext, customerScopes, User, cancellationToken);
            await databaseService.CreateDatabaseAsync(request, async token =>
                {
                    // A second wait may occur on the database resource lease. Recheck at the
                    // provider boundary, using the original tenant rather than resolving a new target.
                    await authority.RevalidateAsync(dbContext, customerScopes, User, token);
                    if (!User.IsInRole("Administrator"))
                    {
                        var scope = await customerScopes.GetScopeAsync(userId, token);
                        if (scope.MaxDatabases > 0 && await dbOwnership.CountForOwnerAsync(subscriptionId, token) >= scope.MaxDatabases)
                            throw new AppException("database.quota_exceeded", 403, 403);
                    }
                    if (!string.IsNullOrWhiteSpace(request.SiteName))
                        await CustomerAccess.EnsureSiteAsync(customerScopes, User, request.SiteName, token);
                },
                token => dbOwnership.RecordAsync(request.Engine, request.Name, subscriptionId, null, token),
                cancellationToken);
        }
        // KONU = ABONELİK, aktör ise oturumdaki kullanıcı. İkisi self-serviste bile AYRILIR:
        // aboneliğin alt kullanıcısı (Plesk "additional user") veritabanını kendi adına açar ama
        // kaynak PARENT aboneliğe aittir ve kotasından düşer. Konu yazılmasaydı, abonelik sahibi
        // kendi kotasını tüketen bu işlemi denetim ekranında göremezdi.
        await auditService.WriteForSubscriptionAsync(
            "database.create.completed",
            $"database:{request.Name}",
            $"[{request.Engine}] '{request.Name}' (self-servis)",
            userId,
            subscriptionId,
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
        var authority = await DatabaseActorAuthority.CaptureAsync(dbContext, customerScopes, User, cancellationToken);
        var userId = authority.UserId;

        Guid? sahip = null;
        using (await DatabaseLifecycle.EnterSubscriptionAsync(authority.SubscriptionId, cancellationToken))
        {
            await authority.RevalidateAsync(dbContext, customerScopes, User, cancellationToken);
            await databaseService.DeleteDatabaseAsync(engine, name, async token =>
                {
                    // Both the actor and the original tenant must still be authorized after
                    // resource/account waits. A valid old cookie alone cannot authorize DROP.
                    await authority.RevalidateAsync(dbContext, customerScopes, User, token);
                    sahip = await dbOwnership.GetOwnerAsync(engine, name, token);
                    if (!User.IsInRole("Administrator") && sahip != authority.SubscriptionId)
                        throw new AppException("database.not_owned", 403, 403);
                },
                token => dbOwnership.RemoveAsync(engine, name, token), cancellationToken);
        }
        await auditService.WriteForSubscriptionAsync(
            "database.delete.completed",
            $"database:{name}",
            $"[{engine}] '{name}' (self-servis)",
            userId,
            sahip,
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
