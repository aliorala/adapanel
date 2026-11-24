using System.ComponentModel.DataAnnotations;
using AdaPanel.Api.Extensions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/migration/full")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class FullMigrationController(IMigrationAcquisitionService acquisition, IFullMigrationService migration,
    IAuditService auditService) : ControllerBase
{
    [HttpPost("discover")]
    [RequestSizeLimit(128 * 1024)]
    public async Task<ActionResult<ApiResponse<MigrationDiscoveryDto>>> DiscoverAsync([FromBody] MigrationConnectionRequest request, CancellationToken token)
    {
        // Do not serialize the connection into logs: it contains source credentials.
        await auditService.AuditAsync(HttpContext, "migration.full.discover", "migration:source",
            "Live source discovery requested; credentials excluded.", null);
        var result = await acquisition.DiscoverAsync(User.GetRequiredUserId(), request, token);
        return Ok(ApiResponse<MigrationDiscoveryDto>.Ok(result));
    }

    [HttpPost("acquisitions")]
    [RequestSizeLimit(2 * 1024 * 1024)]
    public async Task<ActionResult<ApiResponse<MigrationAcquisitionDto>>> AcquireAsync([FromBody] MigrationAcquireRequest request, CancellationToken token)
    {
        await auditService.AuditAsync(HttpContext, "migration.full.acquire", "migration:source",
            "Live source snapshot acquisition requested; credentials excluded.", null);
        var result = await acquisition.StartAsync(User.GetRequiredUserId(), request, token);
        return Ok(ApiResponse<MigrationAcquisitionDto>.Ok(result));
    }

    [HttpGet("acquisitions")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<MigrationAcquisitionDto>>>> AcquisitionsAsync(CancellationToken token)
        => Ok(ApiResponse<IReadOnlyList<MigrationAcquisitionDto>>.Ok(await acquisition.ListAsync(User.GetRequiredUserId(), token)));

    [HttpGet("acquisitions/{id:guid}")]
    public async Task<ActionResult<ApiResponse<MigrationAcquisitionDto>>> AcquisitionAsync(Guid id, CancellationToken token)
        => Ok(ApiResponse<MigrationAcquisitionDto>.Ok(await acquisition.GetAsync(User.GetRequiredUserId(), id, token)));

    [HttpPost("acquisitions/{id:guid}/cancel")]
    public async Task<ActionResult<ApiResponse<MigrationAcquisitionDto>>> CancelAcquisitionAsync(Guid id, CancellationToken token)
    {
        // Ownership check precedes the audit record; another administrator's job cannot be probed.
        var owner = User.GetRequiredUserId();
        await acquisition.GetAsync(owner, id, token);
        await auditService.AuditAsync(HttpContext, "migration.full.acquire.cancel", $"migration-acquisition:{id:D}", "Source acquisition cancellation requested.", null);
        return Ok(ApiResponse<MigrationAcquisitionDto>.Ok(await acquisition.CancelAsync(owner, id, token)));
    }

    [HttpGet("packages")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<MigrationPackageSummaryDto>>>> PackagesAsync(CancellationToken token)
    {
        var source = await migration.GetPackagesAsync(User.GetRequiredUserId(), token);
        // Payload paths, source SQL, raw task arguments and protected archive metadata are not UI inventory.
        var summary = source.Select(package => new MigrationPackageSummaryDto
        {
            Id = package.Id, Provider = package.Manifest.Provider.ToString().ToLowerInvariant(), SourceName = package.Manifest.SourceName,
            CreatedAtUtc = package.Manifest.CreatedAtUtc, Complete = package.Manifest.Complete, Issues = package.Manifest.Issues,
            Sites = package.Manifest.Sites, Databases = package.Manifest.Databases,
            MailDomains = package.Manifest.MailDomains, DnsZones = package.Manifest.DnsZones.Select(zone => new MigrationResourceSummaryDto
            { Id = zone.Id, Name = zone.Name, SiteIds = zone.SiteIds, Count = zone.Records.Count }).ToList(),
            Tasks = package.Manifest.Tasks.Select(task => new MigrationResourceSummaryDto
            { Id = task.Id, Name = task.Name, SiteIds = task.SiteIds, MappingRequired = task.ScheduleUnknown || task.SourcePlatform != "windows" }).ToList()
        }).ToArray();
        return Ok(ApiResponse<IReadOnlyList<MigrationPackageSummaryDto>>.Ok(summary));
    }

    [HttpPost("plans")]
    [RequestSizeLimit(8 * 1024 * 1024)]
    public async Task<ActionResult<ApiResponse<MigrationPlanDto>>> PlanAsync([FromBody] MigrationPlanRequest request, CancellationToken token)
    {
        await auditService.AuditAsync(HttpContext, "migration.full.plan", "migration:plan", "Target preflight requested; no target data changed.", null);
        return Ok(ApiResponse<MigrationPlanDto>.Ok(await migration.PlanAsync(User.GetRequiredUserId(), request, token)));
    }

    [HttpGet("plans/{id:guid}")]
    public async Task<ActionResult<ApiResponse<MigrationPlanDto>>> PlanAsync(Guid id, CancellationToken token)
        => Ok(ApiResponse<MigrationPlanDto>.Ok(await migration.GetPlanAsync(User.GetRequiredUserId(), id, token)));

    [HttpPost("operations")]
    [RequestSizeLimit(4096)]
    public async Task<ActionResult<ApiResponse<MigrationOperationDto>>> StartAsync([FromBody] StartFullMigrationRequest request, CancellationToken token)
    {
        var owner = User.GetRequiredUserId();
        await migration.GetPlanAsync(owner, request.PlanId, token);
        await auditService.AuditAsync(HttpContext, "migration.full.start", $"migration-plan:{request.PlanId:D}", "Target transfer requested; outcome is tracked per component.", null);
        return Ok(ApiResponse<MigrationOperationDto>.Ok(await migration.StartAsync(owner, request.PlanId, request.PlanHash, token)));
    }

    [HttpGet("operations")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<MigrationOperationDto>>>> OperationsAsync(CancellationToken token)
        => Ok(ApiResponse<IReadOnlyList<MigrationOperationDto>>.Ok(await migration.GetOperationsAsync(User.GetRequiredUserId(), token)));

    [HttpGet("operations/{id:guid}")]
    public async Task<ActionResult<ApiResponse<MigrationOperationDto>>> OperationAsync(Guid id, CancellationToken token)
        => Ok(ApiResponse<MigrationOperationDto>.Ok(await migration.GetOperationAsync(User.GetRequiredUserId(), id, token)));

    [HttpPost("operations/{id:guid}/cancel")]
    public async Task<ActionResult<ApiResponse<MigrationOperationDto>>> CancelAsync(Guid id, CancellationToken token)
    {
        var owner = User.GetRequiredUserId();
        await migration.GetOperationAsync(owner, id, token);
        await auditService.AuditAsync(HttpContext, "migration.full.cancel", $"migration-operation:{id:D}", "Target transfer cancellation requested; completed resources will be retained.", null);
        return Ok(ApiResponse<MigrationOperationDto>.Ok(await migration.CancelAsync(owner, id, token)));
    }

    [HttpGet("operations/{id:guid}/credentials")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<MigrationCreatedCredential>>>> CredentialsAsync(Guid id, CancellationToken token)
    {
        var owner = User.GetRequiredUserId();
        await migration.GetOperationAsync(owner, id, token);
        await auditService.AuditAsync(HttpContext, "migration.full.credentials.read", $"migration-operation:{id:D}", "Administrator requested generated migration credentials; values excluded.", null);
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        return Ok(ApiResponse<IReadOnlyList<MigrationCreatedCredential>>.Ok(await migration.GetCredentialsAsync(owner, id, token)));
    }
}

public sealed class StartFullMigrationRequest
{
    public Guid PlanId { get; set; }
    [Required, RegularExpression("^[A-Fa-f0-9]{64}$")] public string PlanHash { get; set; } = "";
}

public sealed class MigrationPackageSummaryDto
{
    public Guid Id { get; set; }
    public string Provider { get; set; } = "";
    public string SourceName { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public bool Complete { get; set; }
    public List<PortableMigrationIssue> Issues { get; set; } = [];
    public List<PortableMigrationSite> Sites { get; set; } = [];
    public List<PortableMigrationDatabase> Databases { get; set; } = [];
    public List<PortableMigrationMailDomain> MailDomains { get; set; } = [];
    public List<MigrationResourceSummaryDto> DnsZones { get; set; } = [];
    public List<MigrationResourceSummaryDto> Tasks { get; set; } = [];
}

public sealed class MigrationResourceSummaryDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> SiteIds { get; set; } = [];
    public int Count { get; set; }
    public bool MappingRequired { get; set; }
}
