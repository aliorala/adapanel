using System.Reflection;
using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Ai;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Migration.Packages;
using AdaPanel.Infrastructure.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

[Collection("MigrationSourceApi")]
public sealed class MigrationSourceControllerTests
{
    [Fact]
    public void Controller_is_admin_only_and_readonly_source_post_does_not_grant_neighbor_mutations()
    {
        Assert.Equal("Administrator", typeof(MigrationSourceController).GetCustomAttribute<AuthorizeAttribute>()!.Roles);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Administrator")], PanelTokenAuthenticationHandler.SchemeName));
        PanelTokenCapabilities.EnsureAllowed(principal, "POST", AdaPanelMigrationSourceProtocol.ExportPath);
        Assert.Throws<AppException>(() => PanelTokenCapabilities.EnsureAllowed(principal, "POST", "/api/v1/migration/source/export-delete"));
    }

    [Fact]
    public async Task Inventory_is_live_authorized_versioned_and_noncacheable()
    {
        using var f = new Fixture(); var controller = f.Controller();
        var response = await controller.GetInventoryAsync(default);
        var body = Assert.IsType<ApiResponse<MigrationSourceInventoryDto>>(Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.True(body.Success); Assert.Equal("adapanel", body.Data!.Provider);
        Assert.Equal(AdaPanelMigrationSourceProtocol.Version, controller.Response.Headers[AdaPanelMigrationSourceProtocol.VersionHeader]);
        Assert.Contains("no-store", controller.Response.Headers.CacheControl.ToString());
        Assert.Equal("nosniff", controller.Response.Headers["X-Content-Type-Options"]);
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("role")]
    [InlineData("deleted")]
    public async Task Stale_admin_claims_never_reach_exporter(string change)
    {
        using var f = new Fixture();
        if (change == "inactive") f.User.IsActive = false;
        else if (change == "role") f.User.Role = UserRole.Customer;
        else f.Db.Users.Remove(f.User);
        await f.Db.SaveChangesAsync();
        Assert.Equal("auth.unauthorized", (await Assert.ThrowsAsync<AppException>(() => f.Controller().GetInventoryAsync(default))).MessageKey);
        Assert.Equal(0, f.Exporter.InventoryCalls);
    }

    [Fact]
    public async Task Missing_export_feature_is_explicit_503_not_an_old_404()
    {
        using var f = new Fixture(); var error = await Assert.ThrowsAsync<AppException>(() => f.Controller(false).GetInventoryAsync(default));
        Assert.Equal(503, error.HttpStatusCode); Assert.Equal("migration.source.unavailable", error.MessageKey);
    }

    [Fact]
    public async Task Export_stream_disposal_removes_private_workspace_and_releases_single_export_gate()
    {
        using var f = new Fixture(); var controller = f.Controller();
        var result = Assert.IsType<FileStreamResult>(await controller.ExportAsync(new() { AllSites = true }, default));
        Assert.Equal("application/zip", result.ContentType); Assert.False(result.EnableRangeProcessing);
        Assert.True(Directory.Exists(f.Exporter.Workspace));
        Assert.Equal("migration.source.export_busy", (await Assert.ThrowsAsync<AppException>(() => f.Controller().ExportAsync(new(), default))).MessageKey);
        await result.FileStream.DisposeAsync(); Assert.False(Directory.Exists(f.Exporter.Workspace));
        var next = Assert.IsType<FileStreamResult>(await f.Controller().ExportAsync(new(), default));
        next.FileStream.Dispose(); Assert.False(Directory.Exists(f.Exporter.Workspace));
    }

    [Fact]
    public async Task Real_mvc_file_executor_disposes_the_stream_and_cleans_workspace_after_copy()
    {
        using var f = new Fixture(); var controller = f.Controller();
        controller.Response.Body = new MemoryStream();
        var result = Assert.IsType<FileStreamResult>(await controller.ExportAsync(new(), default));
        var executor = new FileStreamResultExecutor(NullLoggerFactory.Instance);
        await executor.ExecuteAsync(new ActionContext(controller.HttpContext, new RouteData(), new ActionDescriptor()), result);
        Assert.False(Directory.Exists(f.Exporter.Workspace));
        Assert.Equal(3, controller.Response.Body.Length);
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("password")]
    public async Task Actor_change_during_export_prevents_publication_and_cleans_artifact(string change)
    {
        using var f = new Fixture();
        f.Exporter.BeforeReturn = async () =>
        {
            if (change == "inactive") f.User.IsActive = false; else f.User.PasswordHash = "changed";
            await f.Db.SaveChangesAsync();
        };
        var controller = f.Controller(stamp: CookieSessionValidator.ComputeStamp(f.User.PasswordHash));
        Assert.Equal("auth.unauthorized", (await Assert.ThrowsAsync<AppException>(() => controller.ExportAsync(new(), default))).MessageKey);
        Assert.False(Directory.Exists(f.Exporter.Workspace));
    }

    [Fact]
    public async Task Revoked_bearer_token_is_revalidated_after_export_before_any_bytes_are_returned()
    {
        using var f = new Fixture(); f.Exporter.BeforeReturn = () => { f.Tokens.Revoked = true; return Task.CompletedTask; };
        Assert.Equal("auth.unauthorized", (await Assert.ThrowsAsync<AppException>(() => f.Controller(token: true).ExportAsync(new(), default))).MessageKey);
        Assert.False(Directory.Exists(f.Exporter.Workspace)); Assert.True(f.Tokens.Validations >= 3);
    }

    [Fact]
    public async Task Unexpected_artifact_path_is_rejected_without_deleting_unowned_content()
    {
        using var f = new Fixture(); f.Exporter.Unowned = true;
        Assert.Equal("migration.source.export_failed", (await Assert.ThrowsAsync<AppException>(() => f.Controller().ExportAsync(new(), default))).MessageKey);
        Assert.True(File.Exists(f.Exporter.FilePath));
        f.Exporter.Unowned = false;
        var next = Assert.IsType<FileStreamResult>(await f.Controller().ExportAsync(new(), default)); next.FileStream.Dispose();
    }

    [Fact]
    public async Task Cancellation_after_export_cleans_artifact_and_releases_the_gate()
    {
        using var f = new Fixture(); using var cancellation = new CancellationTokenSource();
        f.Exporter.BeforeReturn = () => { cancellation.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Controller().ExportAsync(new(), cancellation.Token));
        Assert.False(Directory.Exists(f.Exporter.Workspace));
        f.Exporter.BeforeReturn = null;
        var next = Assert.IsType<FileStreamResult>(await f.Controller().ExportAsync(new(), default)); next.FileStream.Dispose();
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "AdaPanel.SourceApi." + Guid.NewGuid().ToString("N"));
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        internal readonly AdaPanelDbContext Db;
        internal readonly PanelUser User = new() { UserName = "admin", Email = "admin@example.test", PasswordHash = "hash", Role = UserRole.Administrator };
        internal readonly Exporter Exporter;
        internal readonly Tokens Tokens;
        internal Fixture()
        {
            connection.Open(); Db = new(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options); Db.Database.EnsureCreated();
            Db.Users.Add(User); Db.SaveChanges(); Exporter = new(Root); Tokens = new(User.Id);
        }
        internal MigrationSourceController Controller(bool available = true, bool token = false, string? stamp = null)
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, User.Id.ToString()), new(ClaimTypes.Role, nameof(UserRole.Administrator)) };
            if (stamp != null) claims.Add(new(CookieSessionValidator.CredentialStampClaim, stamp));
            var context = new DefaultHttpContext { User = new(new ClaimsIdentity(claims, token ? PanelTokenAuthenticationHandler.SchemeName : "fixture")) };
            if (token) context.Request.Headers.Authorization = "Bearer adap_fixture";
            return new(Db, Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Root }), Tokens, available ? Exporter : null)
            { ControllerContext = new() { HttpContext = context } };
        }
        public void Dispose()
        {
            Db.Dispose(); connection.Dispose(); if (Directory.Exists(Root)) PortableMigrationStorage.DeleteOwnedDirectory(Root);
        }
    }
    private sealed class Exporter(string root) : IAdaPanelMigrationExporter
    {
        internal int InventoryCalls;
        internal bool Unowned;
        internal string Workspace = "", FilePath = "";
        internal Func<Task>? BeforeReturn;
        public Task<MigrationSourceInventoryDto> GetInventoryAsync(CancellationToken token = default)
        { InventoryCalls++; return Task.FromResult(new MigrationSourceInventoryDto { Complete = true }); }
        public async Task<MigrationExportArtifact> ExportAsync(MigrationExportRequest request, CancellationToken token = default)
        {
            Workspace = Unowned ? Path.Combine(root, "unowned") : Path.Combine(root, "migration-exports", Guid.NewGuid().ToString("N"));
            PortableMigrationStorage.CreatePrivateDirectory(Workspace); FilePath = Path.Combine(Workspace, "migration.zip");
            await File.WriteAllBytesAsync(FilePath, [1, 2, 3], token);
            if (BeforeReturn != null) await BeforeReturn();
            return new(FilePath, "AdaPanel-migration.zip", Workspace);
        }
    }
    private sealed class Tokens(Guid userId) : IApiTokenService
    {
        internal bool Revoked;
        internal int Validations;
        public Task<PanelApiToken?> ValidateAsync(string presentedToken, CancellationToken token = default)
        { Validations++; return Task.FromResult<PanelApiToken?>(Revoked ? null : new() { UserId = userId }); }
        public Task<IReadOnlyList<ApiTokenDto>> GetForUserAsync(Guid userId, CancellationToken token = default) => throw new NotSupportedException();
        public Task<CreatedApiTokenDto> CreateAsync(Guid userId, CreateApiTokenRequest request, CancellationToken token = default) => throw new NotSupportedException();
        public Task RevokeAsync(Guid userId, Guid tokenId, CancellationToken token = default) => throw new NotSupportedException();
        public Task<int> RevokeAllForUserAsync(Guid userId, CancellationToken token = default) => throw new NotSupportedException();
    }
}
