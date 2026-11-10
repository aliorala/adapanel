using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using AdaPanel.Api.Controllers;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class FullMigrationControllerTests
{
    [Fact]
    public void EveryEndpointRequiresAdministratorAndDisallowsResponseCaching()
    {
        var type = typeof(FullMigrationController);
        Assert.Equal("Administrator", type.GetCustomAttribute<AuthorizeAttribute>()!.Roles);
        Assert.Equal("api/v1/migration/full", type.GetCustomAttribute<RouteAttribute>()!.Template);
        var cache = type.GetCustomAttribute<ResponseCacheAttribute>()!;
        Assert.True(cache.NoStore);
        Assert.Equal(ResponseCacheLocation.None, cache.Location);
        Assert.DoesNotContain(type.GetMethods(), method => method.GetCustomAttribute<AllowAnonymousAttribute>() != null);
    }

    [Fact]
    public async Task SourceCredentialsDoNotEnterAuditOrStatus()
    {
        var area = new Area();
        const string secret = "source-credential-sentinel";
        var request = new MigrationConnectionRequest { Provider = "adapanel", AdaPanel = new() { BaseUrl = "https://source.example:8890", ApiToken = secret } };
        area.AcquisitionCall = (method, args) =>
        {
            Assert.Equal(nameof(IMigrationAcquisitionService.DiscoverAsync), method.Name);
            Assert.Equal(area.Owner, args![0]);
            Assert.Same(request, args[1]);
            return Task.FromResult(new MigrationDiscoveryDto { Provider = "adapanel", AdaPanel = new() { Provider = "adapanel", Complete = true } });
        };
        var result = await area.Controller.DiscoverAsync(request, default);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(result.Result).Value));
        Assert.NotEmpty(area.Audits);
        Assert.DoesNotContain(secret, string.Join(" ", area.Audits));
    }

    [Fact]
    public async Task PublicPackageInventoryOmitsTaskArgumentsAndPayloadList()
    {
        var area = new Area();
        var id = Guid.NewGuid();
        area.MigrationCall = (method, args) =>
        {
            Assert.Equal(nameof(IFullMigrationService.GetPackagesAsync), method.Name);
            Assert.Equal(area.Owner, args![0]);
            return Task.FromResult<IReadOnlyList<PortableMigrationPackage>>([new(id, new()
            {
                Provider = PortableMigrationProvider.Adapanel, Complete = true,
                Tasks = [new() { Id = "task", Name = "safe-name", Command = "private-program", Arguments = "TOP-SECRET", Url = "https://private/?token=TOP-SECRET" }],
                Payloads = [new() { Path = "private-artifact-path" }]
            })]);
        };
        var response = await area.Controller.PackagesAsync(default);
        var body = Assert.IsType<ApiResponse<IReadOnlyList<MigrationPackageSummaryDto>>>(Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(id, Assert.Single(body.Data!).Id);
        var json = JsonSerializer.Serialize(body);
        Assert.DoesNotContain("TOP-SECRET", json);
        Assert.DoesNotContain("private-program", json);
        Assert.DoesNotContain("private-artifact-path", json);
        Assert.Contains("safe-name", json);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("acquisition")]
    [InlineData("plan")]
    public async Task OwnershipFailurePreventsMutationAndAudit(string kind)
    {
        var area = new Area();
        var id = Guid.NewGuid();
        var calls = 0;
        object? Reject(MethodInfo method, object?[]? args)
        {
            calls++;
            Assert.Equal(area.Owner, args![0]);
            Assert.DoesNotContain("Cancel", method.Name);
            Assert.DoesNotContain("Start", method.Name);
            throw new AppException("migration.operation_not_found", 404, 404);
        }
        area.MigrationCall = Reject;
        area.AcquisitionCall = Reject;
        if (kind == "operation") await Assert.ThrowsAsync<AppException>(() => area.Controller.CancelAsync(id, default));
        else if (kind == "acquisition") await Assert.ThrowsAsync<AppException>(() => area.Controller.CancelAcquisitionAsync(id, default));
        else await Assert.ThrowsAsync<AppException>(() => area.Controller.StartAsync(new() { PlanId = id, PlanHash = new string('A', 64) }, default));
        Assert.Equal(1, calls);
        Assert.Empty(area.Audits);
    }

    [Fact]
    public async Task CredentialReadIsDedicatedOwnedAuditedAndNoStore()
    {
        var area = new Area();
        var id = Guid.NewGuid();
        area.MigrationCall = (method, args) =>
        {
            Assert.Equal(area.Owner, args![0]); Assert.Equal(id, args[1]);
            if (method.Name == nameof(IFullMigrationService.GetOperationAsync)) return Task.FromResult(new MigrationOperationDto { Id = id });
            if (method.Name != nameof(IFullMigrationService.GetCredentialsAsync)) throw new InvalidOperationException();
            Assert.NotEmpty(area.Audits);
            return Task.FromResult<IReadOnlyList<MigrationCreatedCredential>>([new() { Password = "NEW-SECRET" }]);
        };
        var response = await area.Controller.CredentialsAsync(id, default);
        var value = Assert.IsType<ApiResponse<IReadOnlyList<MigrationCreatedCredential>>>(Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal("NEW-SECRET", Assert.Single(value.Data!).Password);
        Assert.DoesNotContain("NEW-SECRET", string.Join(" ", area.Audits));
        Assert.Equal("no-store", area.Controller.Response.Headers.CacheControl.ToString());
    }

    private sealed class Area
    {
        public Guid Owner = Guid.NewGuid();
        public List<string> Audits = [];
        public Func<MethodInfo, object?[]?, object?> AcquisitionCall = (_, _) => throw new InvalidOperationException();
        public Func<MethodInfo, object?[]?, object?> MigrationCall = (_, _) => throw new InvalidOperationException();
        public FullMigrationController Controller;
        public Area()
        {
            var acquisition = Stub<IMigrationAcquisitionService>((method, args) => AcquisitionCall(method, args));
            var migration = Stub<IFullMigrationService>((method, args) => MigrationCall(method, args));
            var audit = Stub<IAuditService>((_, args) => { Audits.Add(string.Join(" ", args!.OfType<string>())); return Task.CompletedTask; });
            Controller = new(acquisition, migration, audit)
            {
                ControllerContext = new() { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new(ClaimTypes.NameIdentifier, Owner.ToString()), new(ClaimTypes.Role, "Administrator")], "fixture")) } }
            };
        }
    }
    private static T Stub<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    { var proxy = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)proxy).Handler = handler; return proxy; }
    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    }
}
