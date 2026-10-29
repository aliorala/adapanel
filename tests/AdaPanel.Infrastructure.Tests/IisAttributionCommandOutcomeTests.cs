using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Monitoring;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class IisAttributionCommandOutcomeTests
{
    [Theory]
    [InlineData(1, "", 1, "")]
    [InlineData(0, "", 1, "")]
    [InlineData(0, "<broken", 0, "")]
    public async Task FailedOrMalformedAppcmdMeasurementIsUnavailable(int xmlExit, string xml, int fallbackExit, string fallback)
    {
        var calls = 0;
        var service = new ResourceAttributionService(null!, null!, null!, null!, NullLogger<ResourceAttributionService>.Instance)
        {
            AppcmdExecutable = "fixture-appcmd.exe",
            AppcmdCommand = (_, _) => Task.FromResult(++calls == 1
                ? new CommandResult { ExitCode = xmlExit, StandardOutput = xml }
                : new CommandResult { ExitCode = fallbackExit, StandardOutput = fallback })
        };
        var result = await service.GetConsumersAsync("iis");
        Assert.True(result.Installed); Assert.False(result.Available); Assert.Equal("unavailable", result.StatusCode);
    }

    [Theory]
    [InlineData(0, "<appcmd />")]
    [InlineData(1, "unsupported xml mode")]
    public async Task SuccessfulEmptyXmlOrLegacyFallbackStillMeansNoRequests(int xmlExit, string xml)
    {
        var calls = 0;
        var service = new ResourceAttributionService(null!, null!, null!, null!, NullLogger<ResourceAttributionService>.Instance)
        {
            AppcmdExecutable = "fixture-appcmd.exe",
            AppcmdCommand = (_, _) => Task.FromResult(++calls == 1
                ? new CommandResult { ExitCode = xmlExit, StandardOutput = xml }
                : new CommandResult { ExitCode = 0, StandardOutput = "" })
        };
        var result = await service.GetConsumersAsync("iis"); Assert.True(result.Available); Assert.Empty(result.Consumers);
    }
}
