using AdaPanel.Infrastructure.Bootstrap;
using AdaPanel.Infrastructure.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class ComponentServiceStopTests
{
    [Fact]
    public async Task Task_based_runtime_without_Windows_service_does_not_fail_service_stop()
    {
        // A unique missing service/task avoids changing any service on the test machine.
        // This runs the real Windows PowerShell + schtasks path, including exit-code handling.
        var id = "AdaPanel.TestMissing." + Guid.NewGuid().ToString("N");
        var bootstrapper = new ComponentBootstrapper(null!, null!, null!, null!,
            Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions()), null!, null!,
            NullLogger<ComponentBootstrapper>.Instance);
        var installDirectory = Path.Combine(Path.GetTempPath(), id);
        var exception = await Record.ExceptionAsync(
            () => bootstrapper.StopManagedRuntimeAsync(id, installDirectory, CancellationToken.None));

        // Eksik servis/görev "başarısız durdurma" sayılmamalı ve durdurma işlemi kurulum
        // dizinini YARATMAMALI (yan etkisiz).
        Assert.Null(exception);
        Assert.False(Directory.Exists(installDirectory));
    }
}
