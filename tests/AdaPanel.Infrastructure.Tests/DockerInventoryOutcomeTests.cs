using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Docker;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class DockerInventoryOutcomeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedInventoryDoesNotBecomeAnEmptySuccessfulWorkspace(bool images)
    {
        var service = Service(args => args.StartsWith(images ? "images " : "ps ", StringComparison.Ordinal)
            ? new CommandResult { ExitCode = 1, StandardError = "fixture access denied" }
            : Success(args.StartsWith("version ") ? "26.0" : ""));
        var error = await Assert.ThrowsAsync<AppException>(() => service.GetWorkspaceAsync());
        Assert.Equal("docker.command_failed", error.MessageKey);
        Assert.Contains("fixture access denied", error.Details);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"ID\":42}")]
    [InlineData("{\"ID\":\"container-a\"}\nnot-json")]
    public async Task MalformedOrPartialInventoryIsRejected(string output)
    {
        var service = Service(args => Success(args.StartsWith("version ") ? "26.0" : output));
        Assert.Equal("docker.command_failed", (await Assert.ThrowsAsync<AppException>(() => service.GetWorkspaceAsync())).MessageKey);
    }

    [Fact]
    public async Task MeasuredEmptyInventoryIsStillAValidEmptyWorkspace()
    {
        var workspace = await Service(args => Success(args.StartsWith("version ") ? "26.0" : "")).GetWorkspaceAsync();
        Assert.True(workspace.Installed); Assert.True(workspace.DaemonRunning);
        Assert.Empty(workspace.Containers); Assert.Empty(workspace.Images);
    }

    [Fact]
    public async Task UnavailableDaemonStillUsesExistingOfflineStateWithoutReadingInventory()
    {
        var calls = new List<string>();
        var service = Service(args => { calls.Add(args); return new() { ExitCode = 1 }; });
        var workspace = await service.GetWorkspaceAsync();
        Assert.True(workspace.Installed); Assert.False(workspace.DaemonRunning); Assert.Single(calls);
    }

    private static DockerService Service(Func<string, CommandResult> command) => new(NullLogger<DockerService>.Instance)
    { CommandRunner = (args, _) => Task.FromResult<CommandResult?>(command(args)) };
    private static CommandResult Success(string output) => new() { ExitCode = 0, StandardOutput = output };
}
