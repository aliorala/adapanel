using AdaPanel.Infrastructure.Bootstrap;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class ComponentDiscoveryServiceTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\NodeJS\\node.exe\" --service", "C:\\Program Files\\NodeJS\\node.exe")]
    [InlineData("C:\\Redis\\redis-server.exe --service-run", "C:\\Redis\\redis-server.exe")]
    public void ExtractExecutablePath_ParsesWindowsCommands(string command, string expected)
    {
        Assert.Equal(expected, ComponentDiscoveryService.ExtractExecutablePath(command));
    }

    [Fact]
    public void NormalizeUninstallCommand_ConvertsMsiRepairCommandToSilentUninstall()
    {
        var entry = new ComponentDiscoveryService.UninstallEntry(
            "{4F1CBAA0-0000-0000-0000-0123456789AB}",
            "Example",
            "1.0",
            string.Empty,
            string.Empty,
            string.Empty,
            "MsiExec.exe /I{4F1CBAA0-0000-0000-0000-0123456789AB}");

        var command = ComponentDiscoveryService.NormalizeUninstallCommand(entry);

        Assert.Contains("/x {4F1CBAA0-0000-0000-0000-0123456789AB}", command, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/qn", command, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/norestart", command, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Redis", "Redis", true)]
    [InlineData("Redis for Windows 5.0", "Redis", true)]
    [InlineData("Microsoft Visual C++ 2015-2022 Redistributable (x64)", "Redis", false)]
    public void MatchesProductName_DoesNotConfuseRedisWithRedistributable(
        string displayName,
        string productName,
        bool expected)
    {
        Assert.Equal(expected, ComponentDiscoveryService.MatchesProductName(displayName, productName));
    }

    [Theory]
    [InlineData(@"C:\Program Files\PyManager\python.exe", true)]
    [InlineData(@"C:\Users\user\AppData\Local\Microsoft\WindowsApps\python.exe", false)]
    public void IsUsablePythonPath_IgnoresWindowsAppExecutionAlias(string path, bool expected)
    {
        Assert.Equal(expected, ComponentDiscoveryService.IsUsablePythonPath(path));
    }

    [Theory]
    [InlineData("Python 3.14.3", "3.14.3")]
    [InlineData("Python 3.12", "3.12")]
    public void ParsePythonVersionOutput_ReturnsRuntimeVersion(string output, string expected)
    {
        Assert.Equal(expected, ComponentDiscoveryService.ParsePythonVersionOutput(output, "3.x"));
    }
}
