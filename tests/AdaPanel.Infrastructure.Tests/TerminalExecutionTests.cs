using System.Diagnostics;
using AdaPanel.Core.Dtos.Terminal;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Terminal;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

// Process startup must not compete with thousands of unrelated integration tests.
[Collection("Script host timing")]
public sealed class TerminalExecutionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timeout_and_request_cancellation_stop_the_running_command(bool cancelRequest)
    {
        var root = Path.Combine(Path.GetTempPath(), "adapanel-terminal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var pidFile = Path.Combine(root, "parent.pid");
        using var cancellation = new CancellationTokenSource();
        var service = new TerminalService(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
        try
        {
            var operation = service.ExecuteAsync(new ExecuteTerminalCommandRequest
            {
                WorkingDirectory = root, TimeoutSeconds = cancelRequest ? 60 : 15,
                Command = "[IO.File]::WriteAllText('" + pidFile.Replace("'", "''") + "',$PID); " +
                    "[Console]::WriteLine('BEFORE-TIMEOUT'); [Console]::Error.WriteLine('DIAGNOSTIC'); Start-Sleep -Seconds 60"
            }, cancellation.Token);
            if (cancelRequest)
            {
                using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (!File.Exists(pidFile)) await Task.Delay(25, startup.Token);
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(8)));
            }
            else
            {
                var result = await operation.WaitAsync(TimeSpan.FromSeconds(25));
                Assert.True(result.TimedOut);
                Assert.Equal(-1, result.ExitCode);
                Assert.Contains("BEFORE-TIMEOUT", result.StandardOutput);
                Assert.Contains("DIAGNOSTIC", result.StandardError);
            }
            if (int.TryParse(await File.ReadAllTextAsync(pidFile), out var pid))
            {
                try
                {
                    using var parent = Process.GetProcessById(pid);
                    await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (ArgumentException) { } // Already reaped.
            }
        }
        finally
        {
            if (File.Exists(pidFile) && int.TryParse(await File.ReadAllTextAsync(pidFile), out var pid))
            {
                try
                {
                    using var parent = Process.GetProcessById(pid);
                    if (!parent.HasExited) parent.Kill(entireProcessTree: true);
                    await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Completed_command_preserves_both_streams_and_exit_code()
    {
        var root = Path.Combine(Path.GetTempPath(), "adapanel-terminal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var service = new TerminalService(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
            var result = await service.ExecuteAsync(new ExecuteTerminalCommandRequest
            {
                WorkingDirectory = root, TimeoutSeconds = 15,
                Command = "[Console]::WriteLine('OUT'); [Console]::Error.WriteLine('ERR'); exit 7"
            });
            Assert.False(result.TimedOut);
            Assert.Equal(7, result.ExitCode);
            Assert.Contains("OUT", result.StandardOutput);
            Assert.Contains("ERR", result.StandardError);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Timeout_covers_inherited_output_pipes_after_parent_exits()
    {
        var root = Path.Combine(Path.GetTempPath(), "adapanel-terminal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var pidFile = Path.Combine(root, "child.pid");
        Task<AdaPanel.Core.Dtos.Terminal.TerminalCommandResultDto>? operation = null;
        try
        {
            var service = new TerminalService(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
            var command = "$si=[System.Diagnostics.ProcessStartInfo]::new('cmd.exe','/c ping -n 60 127.0.0.1'); " +
                "$si.UseShellExecute=$false; $si.CreateNoWindow=$true; " +
                "$p=[System.Diagnostics.Process]::Start($si); [IO.File]::WriteAllText('" + pidFile.Replace("'", "''") + "',$p.Id); " +
                "[Console]::WriteLine('PARENT-COMPLETED')";
            operation = service.ExecuteAsync(new ExecuteTerminalCommandRequest
            {
                Command = command, WorkingDirectory = root, TimeoutSeconds = 15
            });
            var result = await operation.WaitAsync(TimeSpan.FromSeconds(25));
            Assert.True(result.TimedOut);
            Assert.Equal(-1, result.ExitCode);
            Assert.True(File.Exists(pidFile), "The child must have started for this scenario to be meaningful. " + result.StandardError);
            Assert.Contains("PARENT-COMPLETED", result.StandardOutput);
        }
        finally
        {
            // Stop only the helper whose PID this test wrote; never leave it running after failure.
            if (File.Exists(pidFile) && int.TryParse(await File.ReadAllTextAsync(pidFile), out var pid))
            {
                try
                {
                    using var child = Process.GetProcessById(pid);
                    if (!child.HasExited) child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
            if (operation is not null)
            {
                try { await operation.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            }
            Directory.Delete(root, recursive: true);
        }
    }
}
