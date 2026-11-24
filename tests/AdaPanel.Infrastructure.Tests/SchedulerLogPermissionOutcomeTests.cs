using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Scheduler;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class SchedulerLogPermissionOutcomeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ada-task-log-" + Guid.NewGuid().ToString("N"));
    private SchedulerService Service() => new(null!,
        Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }),
        NullLogger<SchedulerService>.Instance);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task Native_permission_exit_code_controls_output_capture(int exitCode)
    {
        var service = Service();
        service.TaskCommand = (script, _) =>
        {
            // Execute the generated PowerShell, with the native utility safely replaced.
            var run = ScriptTestHost.Run($$"""
                function icacls.exe { $global:LASTEXITCODE = {{exitCode}}; 'fixture ACL result' }
                {{script}}
                """);
            return Task.FromResult(new CommandResult
            {
                ExitCode = run.ExitCode, StandardOutput = run.StdOut, StandardError = run.StdErr
            });
        };
        var path = await service.PrepareLogPathAsync("daily", @"NT AUTHORITY\LOCAL SERVICE", default);
        if (exitCode == 0) Assert.Equal(Path.Combine(root, "logs", "tasks", SchedulerService.ResolveLogFileName("daily")), path);
        else Assert.Null(path);
    }

    [Fact]
    public async Task Failed_command_result_disables_output_capture()
    {
        var service = Service();
        service.TaskCommand = (_, _) => Task.FromResult(new CommandResult { ExitCode = -1 });
        Assert.Null(await service.PrepareLogPathAsync("daily", "fixture", default));
    }

    [Fact]
    public async Task Cancellation_after_permission_command_is_not_swallowed()
    {
        var service = Service();
        using var cancel = new CancellationTokenSource();
        service.TaskCommand = (_, _) =>
        {
            cancel.Cancel();
            return Task.FromResult(new CommandResult());
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.PrepareLogPathAsync("daily", "fixture", cancel.Token));
    }

    [Fact]
    public async Task Already_cancelled_request_does_not_create_log_directory()
    {
        var service = Service();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.PrepareLogPathAsync("daily", null, new CancellationToken(true)));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task System_task_does_not_change_permissions()
    {
        var service = Service();
        service.TaskCommand = (_, _) => throw new InvalidOperationException("No ACL change expected.");
        Assert.NotNull(await service.PrepareLogPathAsync("daily", null, default));
    }
}
