using AdaPanel.Infrastructure.Nginx;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class NginxProcessIdentityTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-nginx-pid-" + Guid.NewGuid().ToString("N"));

    public NginxProcessIdentityTests() => Directory.CreateDirectory(Path.Combine(root, "logs"));

    [Fact]
    public void Stale_pid_reused_by_an_unrelated_live_process_is_not_reported_as_nginx()
    {
        File.WriteAllText(Path.Combine(root, "logs", "nginx.pid"), Environment.ProcessId.ToString());
        Assert.False(NginxInstallationSupport.IsRunning(root));
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("-1")]
    public void Missing_or_invalid_pid_is_not_running(string pid)
    {
        File.WriteAllText(Path.Combine(root, "logs", "nginx.pid"), pid);
        Assert.False(NginxInstallationSupport.IsRunning(root));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
