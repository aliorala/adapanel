using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
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
    ISiteDatabaseLinkService siteDatabaseLinks,
    IIisSiteService iisSiteService,
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
            request.Engine,
            $"Existing '{request.Engine}' server credentials were registered for management.");
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
    /// <summary>Tek veritabanının detay kartı: bağlantı ucu, kullanıcıları, boyutu.</summary>
    [HttpGet("{name}/detail")]
    public async Task<ActionResult<ApiResponse<DatabaseDetailDto>>> GetDetailAsync(
        string name,
        [FromQuery] string engine,
        CancellationToken cancellationToken)
    {
        var detay = await databaseService.GetDetailAsync(engine, name, cancellationToken);
        return Ok(ApiResponse<DatabaseDetailDto>.Ok(detay));
    }

    /// <summary>
    /// Veritabanını denetler; gövdede tablo adı verilirse SEÇİLENLERİ onarır.
    /// <para>
    /// Onarım kör yapılmaz: önce denetim sonucu gösterilir, kullanıcı hangi tablonun
    /// onarılacağını seçer. Onarım yıkıcı olabilir ve kullanıcının neyin değişeceğini görmesi
    /// gerekir.
    /// </para>
    /// </summary>
    [HttpPost("{name}/check")]
    public async Task<ActionResult<ApiResponse<DatabaseCheckResultDto>>> CheckAndRepairAsync(
        string name,
        [FromQuery] string engine,
        [FromBody] string[]? repairTables,
        CancellationToken cancellationToken)
    {
        var sonuc = await databaseService.CheckAndRepairAsync(engine, name, repairTables ?? [], cancellationToken);

        if (repairTables is { Length: > 0 })
        {
            await WriteAuditAsync("database.repaired", engine, name, $"Tables repaired: {string.Join(", ", repairTables)}.");
        }

        return Ok(ApiResponse<DatabaseCheckResultDto>.Ok(sonuc));
    }

    /// <summary>
    /// Veritabanini baska bir siteye (abonelige) tasir.
    /// <para>
    /// MySQL YETKILERINE DOKUNULMAZ. Tasima, panelin kendi sahiplik/baglanti kaydinin
    /// degismesidir; veritabani kullanicilari ve GRANT'lari oldugu gibi kalir. Bu bilincli:
    /// yetkilere dokunmak, o an calisan uygulamanin baglantisini KOPARIRDI (wp-config.php'deki
    /// kimlik bilgileri degismedigi halde erisim kaybolurdu). Degisen sey panelde kimin gordugu
    /// ve kimin kotasina sayildigidir.
    /// </para>
    /// </summary>
    [HttpPost("{name}/move")]
    public async Task<ActionResult<ApiResponse<object>>> MoveDatabaseAsync(
        string name,
        [FromQuery] string engine,
        [FromBody] MoveDatabaseRequest request,
        CancellationToken cancellationToken)
    {
        // HEDEF SITE GERCEKTEN VAR MI: olmayan bir siteye baglamak, veritabanini panelde
        // hicbir yerde gorunmeyen bir kayda hapsederdi.
        var site = await iisSiteService.GetSiteAsync(request.TargetSiteName, cancellationToken);

        await siteDatabaseLinks.LinkAsync(engine, name, site.Name, cancellationToken);

        // Sahiplik de tasinir; yoksa veritabani yeni sahibin kotasina sayilmaz ve eski sahip onu
        // gormeye devam ederdi.
        var owner = await dbOwnership.GetOwnerAsync(engine, name, cancellationToken);
        if (owner is { } mevcutSahip)
        {
            await dbOwnership.RecordAsync(engine, name, mevcutSahip, site.Name, cancellationToken);
        }

        await WriteAuditAsync("database.moved", engine, name, $"Database moved to site '{site.Name}'.");
        return Ok(ApiResponse<object>.Ok(new { name, site = site.Name }, "database.moved"));
    }

    [HttpPost("harden")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<string>>>> HardenAsync(
        [FromQuery] string engine,
        CancellationToken cancellationToken)
    {
        var removed = await databaseService.HardenAdminAccountsAsync(engine, cancellationToken);
        await WriteAuditAsync(
            "database.hardened",
            engine,
            engine,
            removed.Count == 0
                ? "Hardening: no insecure admin accounts found."
                : $"Hardening: removed {string.Join(", ", removed)}.");
        return Ok(ApiResponse<IReadOnlyList<string>>.Ok(removed, "database.hardened"));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<DatabaseWorkspaceDto>>> CreateDatabaseAsync(
        [FromBody] CreateDatabaseRequest request,
        CancellationToken cancellationToken)
    {
        var workspace = await databaseService.CreateDatabaseAsync(request, cancellationToken);
        await WriteAuditAsync("database.create.completed", request.Engine, request.Name, "Database was created.");
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
        await WriteAuditAsync("database.import.completed", request.Engine, request.DatabaseName, $"Database bound to user '{request.UserName}'.");
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
        await WriteAuditAsync("database.delete.completed", engine, name, "Database was removed.");
        return Ok(ApiResponse<object>.Ok(new { engine, name }, "database.delete.success"));
    }

    [HttpPost("users")]
    public async Task<ActionResult<ApiResponse<DatabaseWorkspaceDto>>> CreateUserAsync(
        [FromBody] CreateDatabaseUserRequest request,
        CancellationToken cancellationToken)
    {
        var workspace = await databaseService.CreateUserAsync(request, cancellationToken);
        // PAROLA YAZILMAZ: request.Password kayda hiç girmez. Yazılan şey kullanıcının kimliği,
        // hangi host'tan bağlanabildiği ve (varsa) doğrudan tam yetki aldığı veritabanıdır —
        // "bu hesap neye erişebiliyordu" sorusunun cevabı bunlar.
        await WriteAuditAsync(
            "database.user.created",
            request.Engine,
            request.UserName,
            $"Database user created for host '{request.AllowedHost}'"
            + (string.IsNullOrWhiteSpace(request.DatabaseName)
                ? " with no grants."
                : $" with full access to '{request.DatabaseName}'."));
        return Ok(ApiResponse<DatabaseWorkspaceDto>.Ok(workspace, "database.user_created"));
    }

    [HttpPost("users/password")]
    public async Task<ActionResult<ApiResponse<DatabaseWorkspaceDto>>> SetUserPasswordAsync(
        [FromBody] SetDatabaseUserPasswordRequest request,
        CancellationToken cancellationToken)
    {
        var workspace = await databaseService.SetUserPasswordAsync(request, cancellationToken);
        await WriteAuditAsync("database.user.password_changed", request.Engine, request.UserName, "Database user password was changed.");
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
        await WriteAuditAsync("database.user.deleted", engine, userName, $"Database user for host '{host}' was removed.");
        return Ok(ApiResponse<object>.Ok(new { engine, userName }, "database.user_deleted"));
    }

    [HttpPost("grant")]
    public async Task<ActionResult<ApiResponse<DatabaseWorkspaceDto>>> GrantAccessAsync(
        [FromBody] GrantDatabaseAccessRequest request,
        CancellationToken cancellationToken)
    {
        var workspace = await databaseService.GrantAccessAsync(request, cancellationToken);
        // GRANT bir YETKİLENDİRME değişikliğidir: hangi hakların verildiği yazılmazsa kayıt
        // "bir şey değişti" demekten öteye geçmez ve uyuşmazlıkta işe yaramaz.
        await WriteAuditAsync(
            "database.grant.updated",
            request.Engine,
            request.DatabaseName,
            $"Access for user '{request.UserName}'@'{request.AllowedHost}' set to: "
            + (request.Privileges.Length == 0 ? "ALL" : string.Join(", ", request.Privileges)));

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
            result.TargetDatabase,
            $"Database '{result.SourceDatabase}' from {request.SourceHost} was transferred here.");
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
            "mariadb",
            $"Remote DB access enabled={request.Enabled}, ip={request.AllowedIp ?? "(any)"}.");
        return Ok(ApiResponse<RemoteDbAccessDto>.Ok(dto, request.Enabled ? "database.remote_enabled" : "database.remote_disabled"));
    }

    /// <summary>
    /// Bu denetleyicinin denetim kaydı yazma kalıbı.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kimlik/IP çözümü ve hata yalıtımı artık ORTAK yardımcıya (<c>HttpAuditExtensions</c>)
    /// devredildi — burada yalnızca bu modüle özgü kısım kalıyor: motor etiketi ve hedefin adı.
    /// Önceden bu metot kimliği/IP'yi kendi çözüyordu ve aynı kod panelin başka uçlarında da
    /// tekrarlanacaktı; kopya olduğu sürece biri eksik kalırdı.
    /// </para>
    /// <para>
    /// <c>resource</c> artık "database" gibi sabit bir tür değil, işlemin HEDEFİ (veritabanı ya da
    /// kullanıcı adı). Denetim kaydının cevaplaması gereken soru "neye" — motor bilgisi ayrıntıya taşındı.
    /// </para>
    /// </remarks>
    private Task WriteAuditAsync(string action, string engine, string target, string detail) =>
        auditService.AuditAsync(HttpContext, action, target, $"[{engine}] {detail}");
}
