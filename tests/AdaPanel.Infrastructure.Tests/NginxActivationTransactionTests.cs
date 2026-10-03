using System.Text;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Nginx;
using AdaPanel.Infrastructure.Nginx;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class NginxActivationTransactionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-nginx-activate-" + Guid.NewGuid().ToString("N"));
    private string Config => Path.Combine(root, "conf", "nginx.conf");
    private string State => Path.Combine(root, "conf", "adapanel.json");
    private static readonly byte[] OriginalConfig = Encoding.UTF8.GetBytes("# custom settings\r\nworker_processes 2;\r\n");
    private static readonly byte[] OriginalState = Encoding.UTF8.GetBytes("{\"listenPort\":8088,\"extra\":\"retain\"}");
    private static readonly CommandResult Valid = new() { ExitCode = 0 };

    public NginxActivationTransactionTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "conf"));
        File.WriteAllBytes(Config, OriginalConfig);
        File.WriteAllBytes(State, OriginalState);
    }

    [Theory]
    [InlineData("nginx.startup_task_failed")]
    [InlineData("nginx.reload_failed")]
    public async Task Activation_failure_restores_both_original_files_and_preserves_the_error(string messageKey)
    {
        var failure = new AppException(messageKey, 500, 500, "fixture failure");
        var actual = await Assert.ThrowsAsync<AppException>(() => Write(_ =>
        {
            Assert.Contains("listen 127.0.0.1:9088;", File.ReadAllText(Config));
            Assert.Contains("9088", File.ReadAllText(State));
            throw failure;
        }));
        Assert.Same(failure, actual);
        AssertOriginal();
        AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Activation_failure_restores_the_original_presence_of_each_file(bool configExisted, bool stateExisted)
    {
        if (!configExisted) File.Delete(Config);
        if (!stateExisted) File.Delete(State);
        await Assert.ThrowsAsync<IOException>(() => Write(_ => throw new IOException("activation failed")));
        Assert.Equal(configExisted, File.Exists(Config));
        Assert.Equal(stateExisted, File.Exists(State));
        if (configExisted) Assert.Equal(OriginalConfig, File.ReadAllBytes(Config));
        if (stateExisted) Assert.Equal(OriginalState, File.ReadAllBytes(State));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task Cancellation_after_publication_still_restores_both_files()
    {
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Write(token =>
        {
            cts.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask;
        }, cts.Token));
        AssertOriginal();
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task Successful_activation_keeps_matching_files_and_removes_backups()
    {
        var activated = false;
        await Write(_ => { activated = true; return Task.CompletedTask; });
        Assert.True(activated);
        Assert.Contains("listen 127.0.0.1:9088;", File.ReadAllText(Config));
        Assert.Equal(9088, (await NginxInstallationSupport.ReadConfigurationAsync(root, default)).ListenPort);
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task Rollback_attempts_both_files_and_keeps_a_recovery_copy_if_one_is_locked()
    {
        FileStream? locked = null;
        var failure = new IOException("fixture activation failed");
        try
        {
            var error = await Assert.ThrowsAsync<AggregateException>(() => Write(_ =>
            {
                locked = new FileStream(Config, FileMode.Open, FileAccess.Read, FileShare.Read);
                throw failure;
            }));
            Assert.Contains(failure, error.InnerExceptions);
            Assert.Equal(OriginalState, File.ReadAllBytes(State));
            var backup = Assert.Single(Directory.GetFiles(Path.Combine(root, "conf"), "nginx.conf.*.backup"));
            Assert.Equal(OriginalConfig, File.ReadAllBytes(backup));
        }
        finally { locked?.Dispose(); }
    }

    private Task Write(Func<CancellationToken, Task> activate, CancellationToken token = default)
        => NginxInstallationSupport.WriteConfigurationAsync(root, new NginxConfigurationRequest { ListenPort = 9088 },
            (_, _) => Task.FromResult(Valid), activate, token);
    private void AssertOriginal()
    {
        Assert.Equal(OriginalConfig, File.ReadAllBytes(Config));
        Assert.Equal(OriginalState, File.ReadAllBytes(State));
    }
    private void AssertNoTemporaryFiles()
        => Assert.DoesNotContain(Directory.GetFiles(Path.Combine(root, "conf")),
            path => Path.GetFileName(path) is not ("adapanel.json" or "nginx.conf"));
    public void Dispose() => Directory.Delete(root, true);
}
