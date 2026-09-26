using System.Reflection;
using AdaPanel.Core.Dtos.Components;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Automation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class AutomationVersionCheckTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_check_marks_job_incomplete_and_never_auto_applies_stale_version(bool knownUpdate)
    {
        var component = DispatchProxy.Create<IComponentBootstrapper, Proxy>();
        ((Proxy)(object)component).Handler = (method, _) => method.Name == nameof(IComponentBootstrapper.CheckForUpdatesAsync)
            ? Task.FromResult(new SoftwareCatalogDto
            {
                Installed = [new() { ComponentId = "adminer", DisplayName = "Adminer", Version = "6.1.0",
                    AvailableVersion = "6.1.1", Status = "Installed", VersionCheckSupported = true,
                    VersionCheckFailed = true, UpdateAvailable = knownUpdate }]
            }) : throw new InvalidOperationException("Must not install after an unsuccessful version check");
        var constructor = typeof(AutomationService).GetConstructors().Single();
        var service = constructor.Invoke(constructor.GetParameters().Select(parameter =>
            parameter.ParameterType == typeof(IComponentBootstrapper) ? (object)component :
            parameter.ParameterType == typeof(ILogger<AutomationService>) ? NullLogger<AutomationService>.Instance : null).ToArray());
        var task = (Task)typeof(AutomationService).GetMethod("RunUpdateCheckAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, [true, CancellationToken.None])!;
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        Assert.Equal("completed_with_errors", result.GetType().GetProperty("Status")!.GetValue(result));
        var details = (string)result.GetType().GetProperty("Details")!.GetValue(result)!;
        Assert.Contains("Version checks failed: Adminer", details);
        Assert.DoesNotContain("up to date", details);
        Assert.Equal(knownUpdate ? 1 : 0, result.GetType().GetProperty("Pending")!.GetValue(result));
    }

    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
    }
}
