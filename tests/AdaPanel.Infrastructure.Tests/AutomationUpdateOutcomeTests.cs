using System.Reflection;
using AdaPanel.Core.Dtos.Components;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Automation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class AutomationUpdateOutcomeTests
{
    [Theory]
    [InlineData("8.3.33", true, "Installed", "8.3.36", false)]
    [InlineData("8.3.35", true, "Installed", "8.3.36", false)]
    [InlineData("8.3.36", false, "Installed", "8.3.36", false)]
    [InlineData("unknown", true, "Installed", "8.3.36", false)]
    [InlineData("8.3.36", true, "Installed", "unknown", false)]
    [InlineData("8.3.36", true, "Failed", "8.3.36", false)]
    [InlineData("8.3.36", true, "Installed", "8.3.36", true)]
    [InlineData("8.3.37", true, "Installed", "8.3.36", true)]
    public async Task Auto_update_requires_measured_version_reaching_checked_target(
        string actualVersion, bool measured, string installStatus, string targetVersion, bool succeeds)
    {
        var installs = 0;
        var component = DispatchProxy.Create<IComponentBootstrapper, AutomationVersionCheckTests.Proxy>();
        ((AutomationVersionCheckTests.Proxy)(object)component).Handler = (method, args) =>
        {
            if (method.Name == nameof(IComponentBootstrapper.CheckForUpdatesAsync))
                return Task.FromResult(new SoftwareCatalogDto
                {
                    Installed = [new() { ComponentId = "php-8.3", DisplayName = "PHP", Version = "8.3.33",
                        AvailableVersion = targetVersion, Status = "Installed", VersionCheckSupported = true,
                        UpdateAvailable = true }]
                });
            Assert.Equal(nameof(IComponentBootstrapper.InstallAsync), method.Name);
            Assert.Contains(true, args!);
            installs++;
            return Task.FromResult(new ComponentInstallResultDto
            {
                ComponentId = "php-8.3", Version = actualVersion, VersionMeasured = measured,
                Status = installStatus, Message = "Installer returned"
            });
        };
        var constructor = typeof(AutomationService).GetConstructors().Single();
        var service = constructor.Invoke(constructor.GetParameters().Select(parameter =>
            parameter.ParameterType == typeof(IComponentBootstrapper) ? (object)component :
            parameter.ParameterType == typeof(ILogger<AutomationService>) ? NullLogger<AutomationService>.Instance : null).ToArray());
        var task = (Task)typeof(AutomationService).GetMethod("RunUpdateCheckAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, [true, CancellationToken.None])!;
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        Assert.Equal(1, installs);
        Assert.Equal(succeeds ? "applied" : "completed_with_errors", result.GetType().GetProperty("Status")!.GetValue(result));
        Assert.Equal(succeeds ? 0 : 1, result.GetType().GetProperty("Pending")!.GetValue(result));
        var details = (string)result.GetType().GetProperty("Details")!.GetValue(result)!;
        Assert.Contains(succeeds ? "1 update(s) installed" : "0 update(s) installed", details);
    }
}
