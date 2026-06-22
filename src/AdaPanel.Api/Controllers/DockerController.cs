using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Docker;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/docker")]
public sealed class DockerController(IDockerService dockerService) : ControllerBase
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
        return Ok(ApiResponse<DockerWorkspaceDto>.Ok(workspace, "docker.started"));
    }

    [HttpPost("containers/{id}/stop")]
    public async Task<ActionResult<ApiResponse<DockerWorkspaceDto>>> StopAsync(string id, CancellationToken cancellationToken)
    {
        var workspace = await dockerService.StopContainerAsync(id, cancellationToken);
        return Ok(ApiResponse<DockerWorkspaceDto>.Ok(workspace, "docker.stopped"));
    }

    [HttpPost("containers/{id}/restart")]
    public async Task<ActionResult<ApiResponse<DockerWorkspaceDto>>> RestartAsync(string id, CancellationToken cancellationToken)
    {
        var workspace = await dockerService.RestartContainerAsync(id, cancellationToken);
        return Ok(ApiResponse<DockerWorkspaceDto>.Ok(workspace, "docker.restarted"));
    }

    [HttpDelete("containers/{id}")]
    public async Task<ActionResult<ApiResponse<DockerWorkspaceDto>>> RemoveAsync(string id, CancellationToken cancellationToken)
    {
        var workspace = await dockerService.RemoveContainerAsync(id, cancellationToken);
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
        return Ok(ApiResponse<DockerWorkspaceDto>.Ok(workspace, "docker.container_started"));
    }

    [HttpPost("images/pull")]
    public async Task<ActionResult<ApiResponse<DockerWorkspaceDto>>> PullAsync([FromBody] PullImageRequest request, CancellationToken cancellationToken)
    {
        var workspace = await dockerService.PullImageAsync(request, cancellationToken);
        return Ok(ApiResponse<DockerWorkspaceDto>.Ok(workspace, "docker.image_pulled"));
    }

    [HttpDelete("images/{id}")]
    public async Task<ActionResult<ApiResponse<DockerWorkspaceDto>>> RemoveImageAsync(string id, CancellationToken cancellationToken)
    {
        var workspace = await dockerService.RemoveImageAsync(id, cancellationToken);
        return Ok(ApiResponse<DockerWorkspaceDto>.Ok(workspace, "docker.image_removed"));
    }
}
