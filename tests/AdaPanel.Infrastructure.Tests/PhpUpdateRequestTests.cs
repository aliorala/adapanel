using System.Reflection;
using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Core.Dtos.Components;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Php;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class PhpUpdateRequestTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Software_request_preserves_force_through_php_service_to_installer(bool force)
    {
        var actor = Guid.NewGuid();
        object?[]? captured = null;
        var stopBeforeIis = new IOException("Stop before OS integration in this routing regression test.");
        var bootstrapper = DispatchProxy.Create<IComponentBootstrapper, Proxy>();
        ((Proxy)(object)bootstrapper).Handler = (method, args) =>
        {
            Assert.Equal(nameof(IComponentBootstrapper.InstallAsync), method.Name);
            captured = args;
            return Task.FromException<ComponentInstallResultDto>(stopBeforeIis);
        };
        var scope = new JobScope();
        var jobs = DispatchProxy.Create<IRunningJobService, Proxy>();
        ((Proxy)(object)jobs).Handler = (method, _) =>
        {
            Assert.Equal(nameof(IRunningJobService.BeginAsync), method.Name);
            return Task.FromResult<IAsyncDisposable>(scope);
        };
        var php = new PhpService(null!, bootstrapper, null!, null!, NullLogger<PhpService>.Instance);
        var controller = new SoftwareController(bootstrapper, null!, jobs, php)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actor.ToString())], "test"))
                }
            }
        };
        var error = await Assert.ThrowsAsync<IOException>(() => controller.InstallAsync(new ComponentInstallRequest
        {
            ComponentId = "php-8.3", Force = force
        }));
        Assert.Same(stopBeforeIis, error);
        Assert.NotNull(captured);
        Assert.Equal("php-8.3", captured[0]);
        Assert.Equal(actor, captured[1]);
        Assert.Equal(force, captured[3]);
        Assert.Equal(CancellationToken.None, captured[4]);
        Assert.True(scope.Disposed);
    }

    [Fact]
    public async Task Ordinary_php_install_remains_non_forced()
    {
        bool? captured = null;
        var bootstrapper = DispatchProxy.Create<IComponentBootstrapper, Proxy>();
        ((Proxy)(object)bootstrapper).Handler = (_, args) =>
        {
            captured = (bool)args![3]!;
            return Task.FromException<ComponentInstallResultDto>(new IOException("Stop before IIS."));
        };
        var php = new PhpService(null!, bootstrapper, null!, null!, NullLogger<PhpService>.Instance);
        await Assert.ThrowsAsync<IOException>(() => php.InstallVersionAsync("php-8.3", null, null));
        Assert.False(captured);
    }

    private sealed class JobScope : IAsyncDisposable
    {
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
    }
}
