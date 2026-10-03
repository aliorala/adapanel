using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Nginx;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class NginxStopVerificationTests
{
    [Fact]
    public async Task Still_running_after_the_grace_period_reports_stop_failure()
    {
        var waits = 0;
        var error = await Assert.ThrowsAsync<AppException>(() => NginxInstallationSupport.WaitForStopAsync(
            () => true, () => { waits++; return Task.CompletedTask; }));
        Assert.Equal("nginx.stop_failed", error.MessageKey);
        Assert.Equal(20, waits);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task Confirmed_exit_completes_without_waiting_the_full_grace_period(int remaining)
    {
        var waits = 0;
        await NginxInstallationSupport.WaitForStopAsync(() => waits < remaining, () => { waits++; return Task.CompletedTask; });
        Assert.Equal(remaining, waits);
    }
}
