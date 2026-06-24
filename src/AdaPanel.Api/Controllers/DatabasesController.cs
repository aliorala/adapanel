using AdaPanel.Api.Extensions;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/db")]
public sealed class DatabasesController(
    IDatabaseService databaseService,
    IRemoteDbAccessService remoteDbAccessService,
    IDatabaseOwnershipService dbOwnership,
    IAuditService auditService) : ControllerBase
{
    [HttpGet("engines")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<DatabaseEngineStatusDto>>>> GetEnginesAsync(
        CancellationToken cancellationToken)
    {
        var engines = await databaseService.GetEnginesAsync(cancellationToken);
        return Ok(ApiResponse<IReadOnlyCollection<DatabaseEngineStatusDto>>.Ok(engines));
    }

    [HttpPost("credentials")]
    public async Task<ActionResult<ApiResponse<DatabaseEngineStatusDto>>> RegisterCredentialsAsync(
        [FromBody] RegisterDatabaseCredentialsRequest request,
        CancellationToken cancellationToken)
    {
        var status = await databaseService.RegisterCredentialsAsync(request, cancellationToken);
        await WriteAuditAsync(
            "database.credentials.registered",
            request.Engine,
            $"Existing '{request.Engine}' server credentials were registered for management.",
            cancellationToken);
        return Ok(ApiResponse<DatabaseEngineStatusDto>.Ok(status, "database.credentials_saved"));
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<DatabaseWorkspaceDto>>> GetWorkspaceAsync(
        [FromQuery] string engine,
        CancellationToken cancellationToken)
    {
        var workspace = await databaseService.GetWorkspaceAsync(engine, cancellationToken);
        return Ok(ApiResponse<DatabaseWorkspaceDto>.Ok(workspace));
    }

    // K2 (panel mysql_secure_installation gibi): güvensiz yönetici hesaplarını (anonim + uzak root) kaldırır.
    // root@localhost'a dokunmaz → panel DB erişimi kopmaz. Admin-only.
    [HttpPost("harden")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<string>>>> HardenAsync(
        [FromQuery] string engine,
        CancellationToken cancellationToken)
    {
        var removed = await databaseService.HardenAdminAccountsAsync(engine, cancellationToken);
        await WriteAuditAsync(
            "database.hardened",
            engine,
            removed.Count == 0
                ? $"Hardening '{engine}': no insecure admin accounts found."
                : $"Hardening '{engine}': removed {string.Join(", ", removed)}.",
            cancellationToken);
        return Ok(ApiResponse<IReadOnlyList<string>>.Ok(removed, "database.hardened"));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<DatabaseWorkspaceDto>>> CreateDatabaseAsync(
        [FromBody] CreateDatabaseRequest request,
        CancellationToken cancellationToken)
    {
        var workspace = await databaseService.CreateDatabaseAsync(request, cancellationToken);
        await WriteAuditAsync("database.create.completed", request.Engine, $"Database '{request.Name}' was created.", cancellationToken);
        return Ok(ApiResponse<DatabaseWorkspaceDto>.Ok(workspace, "database.create.success"));
    }

    // Var olan DB+kullanıcıyı panele tanıt (import): GRANT ALL (eksikse) + kullanıcı parolasını sakla → phpMyAdmin
    // "bu kullanıcı olarak aç" + panel yönetimi. Harici (uygulama/elle) oluşturulmuş DB'ler için.
    [HttpPost("import")]
    public async Task<ActionResult<ApiResponse<DatabaseWorkspaceDto>>> ImportDatabaseAsync(
        [FromBody] ImportDatabaseRequest request,
        CancellationToken cancellationToken)
    {
        var workspace = await databaseService.ImportDatabaseAsync(request, cancellationToken);
        await WriteAuditAsync("database.import.completed", request.Engine, $"Database '{request.DatabaseName}' bound to user '{request.UserName}'.", cancellationToken);
        return Ok(ApiResponse<DatabaseWorkspaceDto>.Ok(workspace, "database.import.success"));
    }

    [HttpDelete("{name}")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteDatabaseAsync(
        string name,
        [FromQuery] string engine,
        CancellationToken cancellationToken)
    {
        await databaseService.DeleteDatabaseAsync(engine, name, cancellationToken);
        await dbOwnership.RemoveAsync(engine, name, cancellationToken); // M1: sahiplik kaydını temizle (kota over-count olmasın)
        await WriteAuditAsync("database.delete.completed", engine, $"Database '{name}' was removed.", cancellationToken);
        return Ok(ApiResponse<object>.Ok(new { engine, name }, "database.delete.success"));
    }

    [HttpPost("users")]
    public async Task<ActionResult<ApiResponse<DatabaseWorkspaceDto>>> CreateUserAsync(
        [FromBody] CreateDatabaseUserRequest request,
        CancellationToken cancellationToken)
    {
        var workspace = await databaseService.CreateUserAsync(request, cancellationToken);
        await WriteAuditAsync("database.user.created", request.Engine, $"Database user '{request.UserName}' was created.", cancellationToken);
        return Ok(ApiResponse<DatabaseWorkspaceDto>.Ok(workspace, "database.user_created"));
    }

    [HttpPost("users/password")]
    public async Task<ActionResult<ApiResponse<DatabaseWorkspaceDto>>> SetUserPasswordAsync(
        [FromBody] SetDatabaseUserPasswordRequest request,
        CancellationToken cancellationToken)
    {
        var workspace = await databaseService.SetUserPasswordAsync(request, cancellationToken);
        await WriteAuditAsync("database.user.password_changed", request.Engine, $"Password changed for database user '{request.UserName}'.", cancellationToken);
        return Ok(ApiResponse<DatabaseWorkspaceDto>.Ok(workspace, "database.user_password_changed"));
    }

    [HttpDelete("users/{userName}")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteUserAsync(
        string userName,
        [FromQuery] string engine,
        [FromQuery] string host,
        CancellationToken cancellationToken)
    {
        await databaseService.DeleteUserAsync(engine, userName, host, cancellationToken);
        await WriteAuditAsync("database.user.deleted", engine, $"Database user '{userName}' was removed.", cancellationToken);
        return Ok(ApiResponse<object>.Ok(new { engine, userName }, "database.user_deleted"));
    }

    [HttpPost("grant")]
    public async Task<ActionResult<ApiResponse<DatabaseWorkspaceDto>>> GrantAccessAsync(
        [FromBody] GrantDatabaseAccessRequest request,
        CancellationToken cancellationToken)
    {
        var workspace = await databaseService.GrantAccessAsync(request, cancellationToken);
        await WriteAuditAsync(
            "database.grant.updated",
            request.Engine,
            $"Access for user '{request.UserName}' on database '{request.DatabaseName}' was updated.",
            cancellationToken);

        return Ok(ApiResponse<DatabaseWorkspaceDto>.Ok(workspace, "database.grant_updated"));
    }

    // profesyonel paneller gibi sunucu taraflı taşıma: kaynak DB bilgileri girilir, sistem mysqldump→mysql ile otomatik aktarır.
    [HttpPost("transfer")]
    public async Task<ActionResult<ApiResponse<DatabaseTransferResultDto>>> TransferDatabaseAsync(
        [FromBody] DatabaseTransferRequest request,
        CancellationToken cancellationToken)
    {
        var result = await databaseService.TransferDatabaseAsync(request, cancellationToken);
        await WriteAuditAsync(
            "database.transfer.completed",
            request.Engine,
            $"Database '{result.SourceDatabase}' from {request.SourceHost} was transferred to '{result.TargetDatabase}'.",
            cancellationToken);
        return Ok(ApiResponse<DatabaseTransferResultDto>.Ok(result, "database.transfer.success"));
    }

    // profesyonel panel benzeri "Uzak Erişim": masaüstü istemcilerin (HeidiSQL/Workbench) bağlanabilmesi için 3306'yı
    // yalnız belirtilen IP'ye açar/kapatır. MariaDB zaten dinler; gerçek kapı Windows Güvenlik Duvarı.
    [HttpGet("remote-access")]
    public async Task<ActionResult<ApiResponse<RemoteDbAccessDto>>> GetRemoteAccessAsync(CancellationToken cancellationToken)
    {
        return Ok(ApiResponse<RemoteDbAccessDto>.Ok(await remoteDbAccessService.GetAsync(cancellationToken)));
    }

    [HttpPut("remote-access")]
    public async Task<ActionResult<ApiResponse<RemoteDbAccessDto>>> SetRemoteAccessAsync(
        [FromBody] UpdateRemoteDbAccessRequest request,
        CancellationToken cancellationToken)
    {
        var dto = await remoteDbAccessService.SetAsync(request, cancellationToken);
        await WriteAuditAsync(
            request.Enabled ? "database.remote.enabled" : "database.remote.disabled",
            "mariadb",
            $"Remote DB access enabled={request.Enabled}, ip={request.AllowedIp ?? "(any)"}.",
            cancellationToken);
        return Ok(ApiResponse<RemoteDbAccessDto>.Ok(dto, request.Enabled ? "database.remote_enabled" : "database.remote_disabled"));
    }

    private Task WriteAuditAsync(string action, string engine, string detail, CancellationToken cancellationToken)
    {
        return auditService.WriteAsync(
            action,
            "database",
            $"[{engine}] {detail}",
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
    }
}
