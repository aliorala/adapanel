using System.Reflection;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.AppStore;
using AdaPanel.Infrastructure.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class WordPressInstallOutcomeTests
{
    [Fact]
    public async Task BootstrapFailureKeepsCopiedFilesAndReportsManualSetupInsteadOfReadyInstallation()
    {
        using var f = new Fixture();
        var result = await f.Service.InstallAsync(new() { TemplateId = "wordpress", SiteName = "fixture.test" });
        Assert.Equal("wordpress", result.TemplateId); Assert.True(result.RequiresManualSetup);
        Assert.Equal("<?php // fixture WordPress files", await File.ReadAllTextAsync(Path.Combine(f.Site, "index.php")));
        Assert.Empty(result.AdminPassword); Assert.Empty(result.AdminUser); Assert.Empty(result.AdminUrl);
    }

    [Fact]
    public async Task CancellationAfterFilesWereCopiedIsRethrown()
    {
        using var f = new Fixture(cancelled: true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.InstallAsync(new() { TemplateId = "wordpress", SiteName = "fixture.test" }));
        Assert.True(File.Exists(Path.Combine(f.Site, "index.php")));
    }

    [Fact]
    public async Task FileOnlyTemplateDoesNotRequireWordPressBootstrap()
    {
        using var f = new Fixture();
        var result = await f.Service.InstallAsync(new() { TemplateId = "plain-php", SiteName = "fixture.test" });
        Assert.False(result.RequiresManualSetup); Assert.Equal("plain-php", result.TemplateId);
        Assert.True(File.Exists(Path.Combine(f.Site, "index.php")));
    }

    [Fact]
    public async Task InnerWpCliPreparationAlsoRethrowsCancellation()
    {
        using var f = new Fixture(cancelled: true);
        var method = typeof(AppStoreService).GetMethod("TryInstallWordPressCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(f.Service, [new AdaPanel.Core.Dtos.Store.InstallAppTemplateRequest
            { SiteName = "fixture.test" }, f.Site, "unused-php.exe", CancellationToken.None])!;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-wp-outcome-" + Guid.NewGuid().ToString("N"));
        internal string Site => Path.Combine(root, "site");
        internal readonly AppStoreService Service;
        internal Fixture(bool cancelled = false)
        {
            Directory.CreateDirectory(Site);
            var iis = Stub<IIisSiteService>((method, _) => method.Name == "GetSiteAsync"
                ? Task.FromResult(new SiteDto { Name = "fixture.test", PhysicalPath = Site, ApplicationRootPath = Site })
                : throw new NotSupportedException(method.Name));
            Exception Failure() => cancelled ? new OperationCanceledException("fixture cancellation") : new IOException("fixture PHP bootstrap unavailable");
            var components = Stub<IComponentBootstrapper>((_, _) => throw Failure());
            var http = Stub<IHttpClientFactory>((_, _) => throw Failure());
            Service = new(iis, http, components, null!, null!, null!,
                Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Path.Combine(root, "data") }),
                NullLogger<AppStoreService>.Instance)
            {
                PrepareWordPressFiles = (destination, token) => File.WriteAllTextAsync(Path.Combine(destination, "index.php"), "<?php // fixture WordPress files", token)
            };
        }
        public void Dispose() => Directory.Delete(root, true);
    }
    private static T Stub<T>(Func<MethodInfo, object?[], object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, PostgresDatabaseIdentityConsumersTests.StrictProxy>();
        ((PostgresDatabaseIdentityConsumersTests.StrictProxy)(object)proxy).Handler = handler;
        return proxy;
    }
}
