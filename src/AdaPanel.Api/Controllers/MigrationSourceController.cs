using System.Security.Claims;
using System.Text.RegularExpressions;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Migration.Packages;
using AdaPanel.Infrastructure.Audit;
using AdaPanel.Infrastructure.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/migration/source")]
public sealed class MigrationSourceController(AdaPanelDbContext database, IOptions<PanelRuntimeOptions> runtime,
    IApiTokenService apiTokens, IAdaPanelMigrationExporter? exporter = null,
    ILogger<MigrationSourceController>? logger = null, IAuditService? auditService = null) : ControllerBase
{
    private static readonly SemaphoreSlim ExportGate = new(1, 1);

    [HttpGet("inventory")]
    public async Task<ActionResult<ApiResponse<MigrationSourceInventoryDto>>> GetInventoryAsync(CancellationToken cancellationToken)
    {
        var authority = await CaptureAdministratorAsync(cancellationToken);
        if (exporter == null) throw Unavailable();
        var inventory = await exporter.GetInventoryAsync(cancellationToken);
        if (await CaptureAdministratorAsync(cancellationToken) != authority) throw UnauthorizedActor();
        Headers();
        return Ok(ApiResponse<MigrationSourceInventoryDto>.Ok(inventory));
    }

    [HttpPost("export")]
    [RequestSizeLimit(1024 * 1024)]
    public async Task<IActionResult> ExportAsync([FromBody] MigrationExportRequest request, CancellationToken cancellationToken)
    {
        var authority = await CaptureAdministratorAsync(cancellationToken);
        if (exporter == null) throw Unavailable();
        if (!await ExportGate.WaitAsync(0, cancellationToken)) throw new AppException("migration.source.export_busy", 409, 409);
        var release = new ExportLease();
        OwnedExportStream? content = null;
        try
        {
            if (await CaptureAdministratorAsync(cancellationToken) != authority) throw UnauthorizedActor();
            var snapshot = new MigrationExportRequest
            {
                AllSites = request.AllSites, SiteIds = [.. (request.SiteIds ?? [])], AllDatabases = request.AllDatabases,
                DatabaseIds = [.. (request.DatabaseIds ?? [])], IncludeMail = request.IncludeMail, IncludeDns = request.IncludeDns,
                IncludeTasks = request.IncludeTasks, TaskIds = [.. (request.TaskIds ?? [])]
            };
            var artifact = await exporter.ExportAsync(snapshot, cancellationToken);
            ValidateOwnedArtifact(artifact);
            try
            {
                content = new OwnedExportStream(new FileStream(artifact.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan), artifact.Workspace, release, logger);
            }
            catch { TryCleanup(artifact.Workspace, logger); throw; }
            if (await CaptureAdministratorAsync(cancellationToken) != authority) throw UnauthorizedActor();
            cancellationToken.ThrowIfCancellationRequested();
            if (auditService is not null)
                await auditService.AuditAsync(HttpContext, "migration.source.export", "migration:source-export",
                    "Administrator exported a selected source snapshot; archive contents and credentials excluded.", null);
            Headers();
            // FileStreamResult disposes on completion/abort. Registration also covers a later filter failure.
            Response.RegisterForDispose(content);
            return File(content, "application/zip", artifact.FileName, enableRangeProcessing: false);
        }
        catch (InvalidDataException) { content?.Dispose(); release.Dispose(); throw new AppException("migration.source.export_failed", 503, 503); }
        catch { content?.Dispose(); release.Dispose(); throw; }
    }

    private async Task<(Guid Id, string Stamp)> CaptureAdministratorAsync(CancellationToken token)
    {
        if (User.Identity?.IsAuthenticated != true || !Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) throw UnauthorizedActor();
        var actor = await database.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == userId, token);
        if (actor is null || !actor.IsActive || actor.Role != UserRole.Administrator || User.FindFirstValue(ClaimTypes.Role) != nameof(UserRole.Administrator)) throw UnauthorizedActor();
        var stamp = CookieSessionValidator.ComputeStamp(actor.PasswordHash);
        if (User.FindFirstValue(CookieSessionValidator.CredentialStampClaim) is { } claimed && claimed != stamp) throw UnauthorizedActor();
        if (PanelAuthentication.IsTokenAuthenticated(User))
        {
            var presented = PanelAuthentication.ExtractToken(Request);
            if (presented == null || await apiTokens.ValidateAsync(presented, token) is not { } key || key.UserId != userId) throw UnauthorizedActor();
        }
        return (userId, stamp);
    }
    private void ValidateOwnedArtifact(MigrationExportArtifact artifact)
    {
        var root = Path.GetFullPath(Path.Combine(Environment.ExpandEnvironmentVariables(runtime.Value.DataRoot), "migration-exports"));
        var workspace = Path.GetFullPath(artifact.Workspace).TrimEnd(Path.DirectorySeparatorChar);
        var file = Path.GetFullPath(artifact.FilePath);
        if (!string.Equals(Path.GetDirectoryName(workspace), root, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(workspace), "N", out _) ||
            !string.Equals(Path.GetDirectoryName(file), workspace, StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(artifact.FileName, @"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\.zip\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("migration.source.artifact_invalid");
        PortableMigrationStorage.Guard(workspace); PortableMigrationStorage.Guard(file);
    }
    private void Headers()
    {
        Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
        Response.Headers.Pragma = "no-cache";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers[AdaPanelMigrationSourceProtocol.VersionHeader] = AdaPanelMigrationSourceProtocol.Version;
    }
    private static AppException UnauthorizedActor() => new("auth.unauthorized", 401, 401);
    private static AppException Unavailable() => new("migration.source.unavailable", 503, 503);
    private static void TryCleanup(string workspace, ILogger<MigrationSourceController>? logger)
    {
        try { if (Directory.Exists(workspace)) PortableMigrationStorage.DeleteOwnedDirectory(workspace); }
        catch { logger?.LogWarning("migration.source.cleanup_failed"); }
    }
    private sealed class ExportLease : IDisposable
    {
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) ExportGate.Release(); }
    }
    private sealed class OwnedExportStream(FileStream inner, string workspace, ExportLease lease, ILogger<MigrationSourceController>? logger) : Stream
    {
        private int disposed;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => inner.ReadAsync(buffer, token);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => inner.ReadAsync(buffer, offset, count, token);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref disposed, 1) == 0)
            {
                try { inner.Dispose(); TryCleanup(workspace, logger); }
                finally { lease.Dispose(); }
            }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { await inner.DisposeAsync(); TryCleanup(workspace, logger); }
            finally { lease.Dispose(); }
            GC.SuppressFinalize(this);
        }
    }
}
