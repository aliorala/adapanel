using System.Text;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Nginx;
using AdaPanel.Infrastructure.Nginx;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class NginxConfigurationTransactionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-nginx-config-" + Guid.NewGuid().ToString("N"));
    private readonly string config;
    private readonly string metadata;
    private readonly byte[] originalConfig = Encoding.UTF8.GetBytes("# Custom configuration\r\nworker_processes 2;\r\n");
    private readonly byte[] originalMetadata = Encoding.UTF8.GetBytes("{\"listenPort\":8088,\"custom\":\"keep\"}");
    private readonly NginxConfigurationRequest request = new() { ListenPort = 9088 };
    private static readonly CommandResult Valid = new() { ExitCode = 0 };

    public NginxConfigurationTransactionTests()
    {
        var directory = Path.Combine(root, "conf");
        Directory.CreateDirectory(directory);
        config = Path.Combine(directory, "nginx.conf");
        metadata = Path.Combine(directory, "adapanel.json");
        File.WriteAllBytes(config, originalConfig);
        File.WriteAllBytes(metadata, originalMetadata);
    }

    [Fact]
    public async Task Cancellation_after_validation_preserves_both_original_files()
    {
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Write((_, _) =>
        {
            cts.Cancel();
            return Task.FromResult(Valid);
        }, cts.Token));
        AssertOriginalFiles();
        AssertNoPendingFiles();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Metadata_commit_failure_does_not_publish_untracked_configuration(bool configExisted)
    {
        if (!configExisted) File.Delete(config);
        using (var locked = new FileStream(metadata, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => Write((_, _) => Task.FromResult(Valid)));
        }

        if (configExisted) Assert.Equal(originalConfig, File.ReadAllBytes(config));
        else Assert.False(File.Exists(config));
        Assert.Equal(originalMetadata, File.ReadAllBytes(metadata));
        AssertNoPendingFiles();
    }

    [Fact]
    public async Task Validator_exception_removes_the_staged_configuration_and_preserves_originals()
    {
        var failure = new IOException("validator unavailable");
        var actual = await Assert.ThrowsAsync<IOException>(() => Write((_, _) => throw failure));
        Assert.Same(failure, actual);
        AssertOriginalFiles();
        AssertNoPendingFiles();
    }

    [Fact]
    public async Task Rejected_configuration_preserves_originals()
    {
        var error = await Assert.ThrowsAsync<AppException>(() => Write((_, _) => Task.FromResult(new CommandResult { ExitCode = 1, StandardError = "fixture invalid" })));
        Assert.Equal("nginx.config_invalid", error.MessageKey);
        AssertOriginalFiles();
        AssertNoPendingFiles();
    }

    [Fact]
    public async Task Successful_validation_publishes_a_matching_configuration_and_metadata()
    {
        await Write(async (pending, _) =>
        {
            AssertOriginalFiles();
            Assert.Contains("listen 127.0.0.1:9088;", await File.ReadAllTextAsync(Path.Combine(root, pending)));
            return Valid;
        });
        Assert.Contains("listen 127.0.0.1:9088;", await File.ReadAllTextAsync(config));
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(metadata));
        Assert.Equal(9088, json.RootElement.GetProperty("listenPort").GetInt32());
        AssertNoPendingFiles();
    }

    private Task Write(Func<string, CancellationToken, Task<CommandResult>> validate, CancellationToken token = default)
        => NginxInstallationSupport.WriteConfigurationAsync(root, request, validate, token);
    private void AssertOriginalFiles()
    {
        Assert.Equal(originalConfig, File.ReadAllBytes(config));
        Assert.Equal(originalMetadata, File.ReadAllBytes(metadata));
    }
    private void AssertNoPendingFiles() => Assert.Empty(Directory.GetFiles(Path.Combine(root, "conf"), "*.pending*"));
    public void Dispose() => Directory.Delete(root, recursive: true);
}
