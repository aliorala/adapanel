using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AdaPanel.Core.Dtos.Components;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Common;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Bootstrap;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class ComponentFailedVersionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Failed_install_never_records_requested_version(bool existing, bool replaceFiles)
    {
        var root = Path.Combine(Path.GetTempPath(), "AdaPanel-failed-version-" + Guid.NewGuid().ToString("N"));
        var installDir = Path.Combine(root, "runtimes", "test-runtime");
        Directory.CreateDirectory(installDir);
        var marker = Path.Combine(installDir, "old.txt");
        await File.WriteAllTextAsync(marker, "old installation");
        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using var db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var installedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            if (existing)
            {
                db.InstalledComponents.Add(new InstalledComponent
                {
                    ComponentId = "test-runtime", Version = "1.0.0", InstallDir = installDir,
                    Status = ComponentInstallStatus.Installed, InstalledAtUtc = installedAt,
                    ProtectedConfigurationJson = "preserved-secret"
                });
                await db.SaveChangesAsync();
            }
            // Both failures use the real download/integrity/install/catch/DB paths. No network
            // or real service is touched. Bad checksum stops before replacement; bad ZIP after.
            byte[] bytes = [1, 2, 3];
            var manifest = new ComponentManifestItemDto
            {
                Id = "test-runtime", DisplayName = "Test runtime", Version = "2.0.0",
                InstallDir = installDir, Type = "zip", DownloadUrl = "https://example.invalid/test.zip",
                Sha256 = replaceFiles ? Convert.ToHexString(SHA256.HashData(bytes)) : new string('0', 64)
            };
            var audit = DispatchProxy.Create<IAuditService, Proxy>();
            ((Proxy)(object)audit).Handler = (_, _) => Task.CompletedTask;
            var secrets = DispatchProxy.Create<ISecretProtector, Proxy>();
            ((Proxy)(object)secrets).Handler = (_, _) => "{}";
            var bootstrapper = new ComponentBootstrapper(db, new ClientFactory(bytes), secrets, audit,
                Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }),
                new ComponentDiscoveryService(NullLogger<ComponentDiscoveryService>.Instance),
                new OperationProgressService(), NullLogger<ComponentBootstrapper>.Instance);

            await Assert.ThrowsAsync<AppException>(() => bootstrapper.InstallComponentInternalAsync(manifest, null, null, CancellationToken.None));
            db.ChangeTracker.Clear();
            var failed = await db.InstalledComponents.SingleAsync();
            Assert.Equal(ComponentInstallStatus.Failed, failed.Status);
            Assert.Equal(existing && !replaceFiles ? "1.0.0" : string.Empty, failed.Version);
            Assert.NotEmpty(failed.LastError);
            Assert.Equal(!replaceFiles, File.Exists(marker));
            if (existing)
            {
                Assert.Equal("preserved-secret", failed.ProtectedConfigurationJson);
                Assert.Equal(installedAt, failed.InstalledAtUtc);
            }
            // A stale update cache must not turn a failed attempt into an ordinary update
            // button on catalog reload. The failure/retry state is authoritative.
            Directory.CreateDirectory(Path.Combine(root, "config"));
            await File.WriteAllTextAsync(Path.Combine(root, "config", "component-updates.json"), JsonSerializer.Serialize(
                new Dictionary<string, object> { [manifest.Id] = new { availableVersion = "2.0.0", updateAvailable = true, checkedAtUtc = DateTime.UtcNow } }));
            var catalog = await bootstrapper.GetCatalogAsync();
            var row = Assert.Single(catalog.Installed, item => item.ComponentId == manifest.Id);
            Assert.Equal(failed.Version, row.Version);
            Assert.Equal("Failed", row.Status);
            Assert.False(row.UpdateAvailable);
            Assert.Equal("2.0.0", row.AvailableVersion);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class ClientFactory(byte[] bytes) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(bytes));
    }
    private sealed class Handler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }
    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
    }
}
