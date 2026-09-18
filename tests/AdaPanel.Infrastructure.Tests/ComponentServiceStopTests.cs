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
        await bootstrapper.StopManagedRuntimeAsync(id, Path.Combine(Path.GetTempPath(), id), CancellationToken.None);
    }
}
