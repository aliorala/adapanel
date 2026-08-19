using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Docker;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

// DENETİM KAYDI: Docker uçları panelin dışında, ayrı bir motorda kalıcı sonuç üretir. İkisi YIKICI:
// konteyner kaldırma (`DELETE containers/{id}`) ve imaj kaldırma (`DELETE images/{id}`) — kaldırılan
// konteynerin adı/kimliği o andan sonra hiçbir yerde sorgulanamaz, yani kaydı SONRADAN yazmak
// imkânsızdır. `run` ise sunucuda yeni bir süreç ve yeni port bağlaması başlatır (image + port + env
// ile); iz bırakmazsa "bu port neden dinleniyor" sorusu cevapsız kalır.
//
// Kimliği kayda yazmak neden yeterli değil: kullanıcı kısa kimlik de girebilir. Bu yüzden mümkün
// olduğunda ÇALIŞMA ALANI anlık görüntüsünden konteynerin adı/imajı çözülüp kayda eklenir.
[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/docker")]
public sealed class DockerController(
    IDockerService dockerService,
    IAuditService auditService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<DockerWorkspaceDto>>> GetAsync(CancellationToken cancellationToken)
    {
        var workspace = await dockerService.GetWorkspaceAsync(cancellationToken);
        return Ok(ApiResponse<DockerWorkspaceDto>.Ok(workspace));
    }

    [HttpPost("containers/{id}/start")]
    public async Task<ActionResult<ApiResponse<DockerWorkspaceDto>>> StartAsync(string id, CancellationToken cancellationToken)
    {
        var workspace = await dockerService.StartContainerAsync(id, cancellationToken);

        // Başlatma da durum değiştirir: duran bir konteyner yeniden port dinlemeye başlar.
        await auditService.AuditAsync(
            HttpContext,
            "docker.container.started",
            $"docker-container:{id}",
            $"Container {Tanim(workspace, id)} started.");

        return Ok(ApiResponse<DockerWorkspaceDto>.Ok(workspace, "docker.started"));
    }

    [HttpPost("containers/{id}/stop")]
    public async Task<ActionResult<ApiResponse<DockerWorkspaceDto>>> StopAsync(string id, CancellationToken cancellationToken)
    {
        var workspace = await dockerService.StopContainerAsync(id, cancellationToken);

        // Durdurma bir KESİNTİDİR; "servis neden düştü" sorusunun ilk bakılacak yeri budur.
        await auditService.AuditAsync(
            HttpContext,
            "docker.container.stopped",
            $"docker-container:{id}",
            $"Container {Tanim(workspace, id)} stopped.");

        return Ok(ApiResponse<DockerWorkspaceDto>.Ok(workspace, "docker.stopped"));
    }

    [HttpPost("containers/{id}/restart")]
    public async Task<ActionResult<ApiResponse<DockerWorkspaceDto>>> RestartAsync(string id, CancellationToken cancellationToken)
    {
        var workspace = await dockerService.RestartContainerAsync(id, cancellationToken);

        await auditService.AuditAsync(
            HttpContext,
            "docker.container.restarted",
            $"docker-container:{id}",
            $"Container {Tanim(workspace, id)} restarted.");

        return Ok(ApiResponse<DockerWorkspaceDto>.Ok(workspace, "docker.restarted"));
    }

    [HttpDelete("containers/{id}")]
    public async Task<ActionResult<ApiResponse<DockerWorkspaceDto>>> RemoveAsync(string id, CancellationToken cancellationToken)
    {
        // Kaldırmadan ÖNCE anlık görüntü alınır: silinen konteyner sonraki listede yoktur, tanımı
        // (ad/imaj) ancak silme öncesinden okunabilir. Kaydın "neyi kaybettik" sorusuna cevap
        // verebilmesi buna bağlı.
        var oncesi = await dockerService.GetWorkspaceAsync(cancellationToken);
        var tanim = Tanim(oncesi, id);

        var workspace = await dockerService.RemoveContainerAsync(id, cancellationToken);

        await auditService.AuditAsync(
            HttpContext,
            "docker.container.removed",
            $"docker-container:{id}",
            $"Container {tanim} removed (container data destroyed).");

        return Ok(ApiResponse<DockerWorkspaceDto>.Ok(workspace, "docker.removed"));
    }

    [HttpGet("containers/{id}/logs")]
    public async Task<ActionResult<ApiResponse<DockerLogsDto>>> LogsAsync(string id, [FromQuery] int tail = 200, CancellationToken cancellationToken = default)
    {
        var logs = await dockerService.GetLogsAsync(id, tail, cancellationToken);
        return Ok(ApiResponse<DockerLogsDto>.Ok(logs));
    }

    [HttpPost("containers/run")]
    public async Task<ActionResult<ApiResponse<DockerWorkspaceDto>>> RunAsync([FromBody] RunContainerRequest request, CancellationToken cancellationToken)
    {
        var workspace = await dockerService.RunContainerAsync(request, cancellationToken);

        // Ortam DEĞİŞKENLERİ kasten yazılmaz: parola/jeton taşımaları olağandır ve kayıt, korumaya
        // çalıştığı sırrın ikinci deposu olmamalıdır. Yalnız KAÇ tane olduğu yazılır.
        await auditService.AuditAsync(
            HttpContext,
            "docker.container.run",
            $"docker-image:{request.Image}",
            $"Container '{request.Name}' started from image '{request.Image}' "
            + $"(ports: {(string.IsNullOrWhiteSpace(request.Ports) ? "none" : request.Ports)}, "
            + $"env vars: {OrtamDegiskeniSayisi(request.Environment)}).");

        return Ok(ApiResponse<DockerWorkspaceDto>.Ok(workspace, "docker.container_started"));
    }

    [HttpPost("images/pull")]
    public async Task<ActionResult<ApiResponse<DockerWorkspaceDto>>> PullAsync([FromBody] PullImageRequest request, CancellationToken cancellationToken)
    {
        var workspace = await dockerService.PullImageAsync(request, cancellationToken);

        // Dışarıdan sunucuya KOD indirir: "bu imaj sunucuya nereden geldi" tedarik zinciri sorusudur.
        await auditService.AuditAsync(
            HttpContext,
            "docker.image.pulled",
            $"docker-image:{request.Image}",
            $"Image '{request.Image}' pulled from registry.");

        return Ok(ApiResponse<DockerWorkspaceDto>.Ok(workspace, "docker.image_pulled"));
    }

    [HttpDelete("images/{id}")]
    public async Task<ActionResult<ApiResponse<DockerWorkspaceDto>>> RemoveImageAsync(string id, CancellationToken cancellationToken)
    {
        var workspace = await dockerService.RemoveImageAsync(id, cancellationToken);

        // İmaj kaldırma YIKICIDIR: kayıt defterinden çekilemeyen (yerel olarak üretilmiş) bir imaj
        // silindiğinde geri getirilemez.
        await auditService.AuditAsync(
            HttpContext,
            "docker.image.removed",
            $"docker-image:{id}",
            $"Image '{id}' removed.");

        return Ok(ApiResponse<DockerWorkspaceDto>.Ok(workspace, "docker.image_removed"));
    }

    /// <summary>
    /// Ortam değişkeni SAYISI — değerleri değil.
    /// </summary>
    /// <remarks>
    /// "KEY=VALUE" satırlarının değerleri sır olabilir (DB parolası, API anahtarı). Sayı yine de
    /// bilgi taşır: sonradan "bu konteyner gizli değerlerle mi başlatıldı" sorusuna cevap verir.
    /// </remarks>
    private static int OrtamDegiskeniSayisi(string? environment) =>
        string.IsNullOrWhiteSpace(environment)
            ? 0
            : environment.Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

    /// <summary>
    /// Konteynerin insan tarafından okunabilir tanımı (ad + imaj); çözülemezse ham kimlik.
    /// </summary>
    /// <remarks>
    /// Ham kimlik tek başına yetersizdir: kısa kimlik girilebilir, konteyner silindikten sonra hiçbir
    /// yerde çözülemez. Çözülemediğinde kaydı hiç yazmamak yerine ham kimlikle yazmak tercih edilir —
    /// eksik bir kayıt, olmayan kayıttan iyidir.
    /// </remarks>
    private static string Tanim(DockerWorkspaceDto workspace, string id)
    {
        var container = workspace.Containers.FirstOrDefault(item =>
            string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)
            || item.Id.StartsWith(id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Name, id, StringComparison.OrdinalIgnoreCase));

        return container is null
            ? $"'{id}'"
            : $"'{container.Name}' ({container.Image}, id {container.Id})";
    }
}
