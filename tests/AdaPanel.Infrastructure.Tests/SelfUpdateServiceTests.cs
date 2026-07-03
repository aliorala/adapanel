using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Update;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.SelfUpdate;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class SelfUpdateServiceTests
{
    [Fact]
    public async Task ApplyUpdateAsync_RequiresSha256BeforeDownload()
    {
        var dataRoot = CreateDataRoot();
        try
        {
            var service = CreateService(dataRoot, new RecordingLauncher());

            var exception = await Assert.ThrowsAsync<AppException>(() =>
                service.ApplyUpdateAsync(new ApplyUpdateRequest
                {
                    DownloadUrl = "https://example.test/AdaPanelSetup.exe"
                }));

            Assert.Equal("update.checksum_required", exception.MessageKey);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Fact]
    public async Task InstallPreparedUpdateAsync_RevalidatesAndSchedulesPackage()
    {
        var dataRoot = CreateDataRoot();
        try
        {
            var updatesDirectory = Path.Combine(dataRoot, "updates");
            Directory.CreateDirectory(updatesDirectory);
            var packagePath = Path.Combine(updatesDirectory, "AdaPanelSetup.exe");
            await File.WriteAllBytesAsync(packagePath, [(byte)'M', (byte)'Z', 1, 2, 3, 4]);
            string hash;
            await using (var packageStream = File.OpenRead(packagePath))
            {
                hash = Convert.ToHexString(await SHA256.HashDataAsync(packageStream));
            }
            await File.WriteAllTextAsync(
                Path.Combine(updatesDirectory, "pending-update.json"),
                JsonSerializer.Serialize(new
                {
                    version = "0.2.1",
                    downloadUrl = "https://example.test/AdaPanelSetup.exe",
                    packagePath,
                    packageSha256 = hash,
                    stagedAtUtc = DateTime.UtcNow
                }));

            var launcher = new RecordingLauncher();
            var service = CreateService(dataRoot, launcher);
            var status = await service.InstallPreparedUpdateAsync();

            Assert.Equal(packagePath, launcher.PackagePath);
            Assert.Equal(hash, launcher.PackageSha256);
            Assert.True(status.IsInstallScheduled);
            Assert.NotNull(status.InstallScheduledAtUtc);
            Assert.Equal("update.install_scheduled", status.Notes);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("0.2.6", "0.2.7", true)]   // daha yeni release -> guncelleme var
    [InlineData("0.2.7", "0.2.7", false)]  // esit -> yok
    [InlineData("0.2.7", "0.2.6", false)]  // release ESKI (downgrade) -> uyari YOK
    [InlineData("0.2.2-preview", "0.2.4", true)]   // "-suffix" sayisal kiyastan once atilir
    [InlineData("0.2.4+abc123", "0.2.4", false)]   // "+hash" eki de atilir -> esit
    [InlineData("0.2.6", "", false)]               // bos tag -> yok
    [InlineData("abc", "def", true)]               // cozulemeyen -> eski esitsizlik davranisi
    public void IsNewerVersion_ComparesSemanticVersions(string current, string latest, bool expected)
    {
        Assert.Equal(expected, SelfUpdateService.IsNewerVersion(current, latest));
    }

    private static SelfUpdateService CreateService(string dataRoot, IUpdateInstallerLauncher launcher)
    {
        var fileStore = new JsonFileStore(
            Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = dataRoot }));
        return new SelfUpdateService(fileStore, new StubHttpClientFactory(), launcher);
    }

    private static string CreateDataRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"adapanel-update-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class RecordingLauncher : IUpdateInstallerLauncher
    {
        public string PackagePath { get; private set; } = string.Empty;

        public string PackageSha256 { get; private set; } = string.Empty;

        public Task ScheduleAsync(
            string packagePath,
            string packageSha256,
            CancellationToken cancellationToken = default)
        {
            PackagePath = packagePath;
            PackageSha256 = packageSha256;
            return Task.CompletedTask;
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new NotFoundHandler());
    }

    // Testi hermetik tutar: gerçek ağa (GitHub/örnek URL) ÇIKMAZ, her isteğe 404 döner. Böylece
    // CheckForUpdate uzak sürüm bulamaz → SHA boş kalır → ApplyUpdate indirmeden ÖNCE checksum_required
    // fırlatır. (Aksi halde canlı aliorala/adapanel release'i gerçek SHA döndürüp bu testi kırıyordu.)
    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
