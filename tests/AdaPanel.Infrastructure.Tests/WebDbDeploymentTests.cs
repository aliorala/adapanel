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
    public async Task DeployPhpMyAdmin_RecreatesSiteAndPersistsActualWildcardPort()
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

        var iis = new RecordingIisSiteService(actualPort: 18996);
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
            fileStore);

        try
        {
            var result = await service.DeployPhpMyAdminAsync();

            Assert.Equal(1, iis.DeleteCalls);
            Assert.Equal(8896, iis.RequestedPort);
            Assert.Equal(18996, result.Port);
            Assert.Equal("http://127.0.0.1:18996/", result.Url);
            Assert.True(result.IsHealthy);

            var stateJson = await File.ReadAllTextAsync(Path.Combine(dataRoot, "config", "webdb.json"));
            Assert.Contains("\"port\": 18996", stateJson, StringComparison.Ordinal);
            Assert.Contains("\"url\": \"http://127.0.0.1:18996/\"", stateJson, StringComparison.Ordinal);
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
        private SiteDto? site;

        public int DeleteCalls { get; private set; }

        public int RequestedPort { get; private set; }

        public Task<SiteDto> CreateSiteAsync(
            CreateSiteRequest request,
            CancellationToken cancellationToken = default)
        {
            RequestedPort = request.Port;
            site = new SiteDto
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
            return Task.FromResult(site);
        }

        public Task DeleteSiteAsync(string siteName, CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            site = null;
            return Task.CompletedTask;
        }

        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default)
            => Task.FromResult(site ?? throw new AppException("sites.site_not_found", 404, 404));

        public Task<IReadOnlyCollection<SiteDto>> GetSitesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<SiteDto>>(site is null ? [] : [site]);

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

        public Task<ComponentInstallResultDto> InstallVersionAsync(string componentId, Guid? actorUserId, string? ipAddress, CancellationToken cancellationToken = default)
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

        public Task<ComponentInstallResultDto> InstallAsync(string componentId, Guid? actorUserId, string? ipAddress, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyCollection<ComponentInstallResultDto>> InstallDefaultsAsync(Guid? actorUserId, string? ipAddress, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task RemoveAsync(string componentId, Guid? actorUserId, string? ipAddress, CancellationToken cancellationToken = default)
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
