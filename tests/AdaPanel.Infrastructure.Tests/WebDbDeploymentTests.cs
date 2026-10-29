using System.Net;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Components;
using AdaPanel.Core.Dtos.Php;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using AdaPanel.Infrastructure.WebDbClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class WebDbDeploymentTests
{
    [Fact]
    public async Task DeploymentLease_SerializesSameProviderButAllowsDifferentProvider()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "webdb.json");
        var first = await WebDbClientService.DeploymentLifecycle.EnterAsync(path, "adminer", default);
        var waiting = WebDbClientService.DeploymentLifecycle.EnterAsync(path, "adminer", default);
        Assert.False(waiting.IsCompleted);
        using var other = await WebDbClientService.DeploymentLifecycle.EnterAsync(path, "phpmyadmin", default);
        first.Dispose();
        using var second = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeployPhpMyAdmin_RecreatesSiteAndPersistsActualWildcardPort(bool concurrentAdminer)
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), $"adapanel-webdb-{Guid.NewGuid():N}");
        var installRoot = Path.Combine(dataRoot, "phpmyadmin");
        Directory.CreateDirectory(installRoot);
        await File.WriteAllTextAsync(Path.Combine(installRoot, "index.php"), "<?php echo 'ready';");
        await File.WriteAllTextAsync(Path.Combine(installRoot, "config.sample.inc.php"), "<?php");

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AdaPanelDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var dbContext = new AdaPanelDbContext(options);
        await dbContext.Database.EnsureCreatedAsync();
        dbContext.InstalledComponents.AddRange(
            new InstalledComponent
            {
                ComponentId = "phpmyadmin",
                DisplayName = "phpMyAdmin",
                Version = "5.2",
                InstallDir = installRoot,
                Status = ComponentInstallStatus.Installed
            },
            new InstalledComponent
            {
                ComponentId = "php-8.3",
                DisplayName = "PHP 8.3",
                Version = "8.3",
                InstallDir = Path.Combine(dataRoot, "php"),
                Status = ComponentInstallStatus.Installed
            });
        await dbContext.SaveChangesAsync();

        var adminerRoot = Path.Combine(dataRoot, "adminer");
        Directory.CreateDirectory(adminerRoot);
        await File.WriteAllTextAsync(Path.Combine(adminerRoot, "adminer.php"), "<?php echo 'ready';");
        dbContext.InstalledComponents.Add(new InstalledComponent { ComponentId = "adminer", DisplayName = "Adminer",
            Version = "5", InstallDir = adminerRoot, Status = ComponentInstallStatus.Installed });
        await dbContext.SaveChangesAsync();

        var iis = new RecordingIisSiteService(actualPort: 18996) { ConcurrentProviders = concurrentAdminer };
        var fileStore = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(
            new PanelRuntimeOptions { DataRoot = dataRoot }));
        var service = new WebDbClientService(
            dbContext,
            new UnusedComponentBootstrapper(),
            iis,
            new SuccessfulPhpService(),
            new PassthroughSecretProtector(),
            new AdaPanel.Infrastructure.Database.DatabaseCredentialStore(fileStore, new PassthroughSecretProtector()),
            new StubHttpClientFactory(),
            fileStore,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WebDbClientService>.Instance);

        try
        {
            var first = service.DeployPhpMyAdminAsync();
            if (concurrentAdminer)
            {
                await iis.FirstCreateEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await service.DeployAdminerAsync();
                iis.ReleaseFirstCreate.TrySetResult();
            }
            var result = await first;

            Assert.Equal(concurrentAdminer ? 2 : 1, iis.DeleteCalls);
            if (!concurrentAdminer) Assert.Equal(8896, iis.RequestedPort);
            Assert.Equal(18996, result.Port);
            Assert.Equal("http://127.0.0.1:18996/", result.Url);
            Assert.True(result.IsHealthy);

            var stateJson = await File.ReadAllTextAsync(Path.Combine(dataRoot, "config", "webdb.json"));
            Assert.Contains("\"port\": 18996", stateJson, StringComparison.Ordinal);
            Assert.Contains("\"url\": \"http://127.0.0.1:18996/\"", stateJson, StringComparison.Ordinal);
            if (concurrentAdminer)
            {
                using var states = System.Text.Json.JsonDocument.Parse(stateJson);
                Assert.Equal(2, states.RootElement.GetArrayLength());
                Assert.Contains("adminer", stateJson);
                Assert.Contains("phpmyadmin", stateJson);
            }
        }
        finally
        {
            await dbContext.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    private sealed class RecordingIisSiteService(int actualPort) : IIisSiteService
    {
        private readonly Dictionary<string, SiteDto> sites = new(StringComparer.OrdinalIgnoreCase);
        public bool ConcurrentProviders { get; init; }
        public TaskCompletionSource FirstCreateEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstCreate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DeleteCalls { get; private set; }

        public int RequestedPort { get; private set; }

        public async Task<SiteDto> CreateSiteAsync(
            CreateSiteRequest request,
            CancellationToken cancellationToken = default)
        {
            RequestedPort = request.Port;
            var site = new SiteDto
            {
                Id = 1,
                Name = request.Name,
                State = "Started",
                AutoStart = true,
                PhysicalPath = request.PhysicalPath,
                ApplicationPoolName = request.ApplicationPoolName,
                Bindings =
                [
                    new SiteBindingDto
                    {
                        Protocol = "http",
                        HostName = string.Empty,
                        IpAddress = "*",
                        Port = actualPort,
                        BindingInformation = $"*:{actualPort}:"
                    }
                ]
            };
            sites[request.Name] = site;
            if (ConcurrentProviders && request.Name.EndsWith("PhpMyAdmin", StringComparison.Ordinal))
            {
                FirstCreateEntered.TrySetResult();
                await ReleaseFirstCreate.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            return site;
        }

        public Task DeleteSiteAsync(string siteName, CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            sites.Remove(siteName);
            return Task.CompletedTask;
        }

        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default)
            => Task.FromResult(sites.GetValueOrDefault(siteName) ?? throw new AppException("sites.site_not_found", 404, 404));

        public Task<IReadOnlyCollection<SiteDto>> GetSitesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<SiteDto>>(sites.Values.ToArray());

        public Task<SiteDto> UpdatePhysicalPathAsync(string siteName, string physicalPath, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SiteDto> StartSiteAsync(string siteName, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SiteDto> StopSiteAsync(string siteName, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SiteDto> AddBindingAsync(string siteName, SiteBindingRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SiteDto> RemoveBindingAsync(string siteName, RemoveSiteBindingRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyCollection<SiteDto>> ImportExistingSitesAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class SuccessfulPhpService : IPhpService
    {
        public int RegisterFastCgiCalls { get; private set; }

        public Task<SitePhpAssignmentDto> SetSitePhpVersionAsync(
            string siteName,
            SetSitePhpVersionRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SitePhpAssignmentDto
            {
                SiteName = siteName,
                ComponentId = request.ComponentId,
                IsFastCgiRegistered = true
            });

        public Task<IReadOnlyCollection<PhpVersionDto>> GetInstalledVersionsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        // Araç siteleri (AdaPanel.*) izolasyon dışıdır; sahte servis çağrıyı sessizce yutar.
        public Task EnsureSiteIsolationAsync(string siteName, bool enabled, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<ComponentInstallResultDto> InstallVersionAsync(string componentId, Guid? actorUserId, string? ipAddress, CancellationToken cancellationToken = default, bool force = false)
            => throw new NotSupportedException();

        public Task RegisterFastCgiAsync(string componentId, CancellationToken cancellationToken = default)
        {
            RegisterFastCgiCalls++;
            return Task.CompletedTask;
        }

        public Task EnsureExtensionsAsync(string componentId, IEnumerable<string> extensions, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<SitePhpSettingsDto> GetSitePhpSettingsAsync(string siteName, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SitePhpSettingsDto> UpdateSitePhpSettingsAsync(string siteName, UpdateSitePhpSettingsRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SitePhpSettingsDto> GetGlobalPhpSettingsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SitePhpSettingsDto> UpdateGlobalPhpSettingsAsync(UpdateSitePhpSettingsRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RedisExtensionResultDto> EnableRedisExtensionAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class PassthroughSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;

        public string Unprotect(string protectedValue) => protectedValue;
    }

    private sealed class UnusedComponentBootstrapper : IComponentBootstrapper
    {
        public Task<SoftwareCatalogDto> GetCatalogAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SoftwareCatalogDto> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<int> GetPendingUpdateCountAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ComponentInstallResultDto> InstallAsync(string componentId, Guid? actorUserId, string? ipAddress, bool force = false, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyCollection<ComponentInstallResultDto>> InstallDefaultsAsync(Guid? actorUserId, string? ipAddress, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyCollection<string>> RemoveAsync(string componentId, Guid? actorUserId, string? ipAddress, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new StubHttpMessageHandler())
            {
                BaseAddress = new Uri("http://127.0.0.1")
            };
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.RequestUri?.AbsolutePath == "/adapanel-health.php"
                ? "AdaPanel-WebDb-OK"
                : "<html>phpMyAdmin</html>";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body),
                RequestMessage = request
            });
        }
    }
}
