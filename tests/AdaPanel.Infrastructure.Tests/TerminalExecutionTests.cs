using System.Diagnostics;
using AdaPanel.Core.Dtos.Terminal;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Terminal;
using AdaPanel.Infrastructure.Support;
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
            await DeleteOwnedRootAsync(root);
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
                Command = "[Console]::WriteLine('OUT Türkçe ĞÜŞİÖÇ'); Write-Error 'ERR Türkçe ĞÜŞİÖÇ'; exit 7"
            }).WaitAsync(TimeSpan.FromSeconds(25));
            Assert.False(result.TimedOut);
            Assert.Equal(7, result.ExitCode);
            Assert.Contains("OUT", result.StandardOutput);
            Assert.Contains("ERR", result.StandardError);
            Assert.Contains("Türkçe ĞÜŞİÖÇ", result.StandardOutput);
            Assert.Contains("Türkçe ĞÜŞİÖÇ", result.StandardError);
            Assert.DoesNotContain("CLIXML", result.StandardError);
        }
        finally { await DeleteOwnedRootAsync(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deadline_covers_inherited_output_pipes_after_parent_exits(bool cancelRequest)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "adapanel-terminal-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var pidFile = Path.Combine(root, "child.pid");
        using var cancellation = new CancellationTokenSource();
        Task<AdaPanel.Core.Dtos.Terminal.TerminalCommandResultDto>? operation = null;
        try
        {
            var service = new TerminalService(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
            // Record the actual pipe-owning child. Waiting for a killed CMD wrapper would
            // not wait for its PING descendant to release the inherited working directory.
            var command = "$si=[System.Diagnostics.ProcessStartInfo]::new('ping.exe','-n 60 127.0.0.1'); " +
                "$si.UseShellExecute=$false; $si.CreateNoWindow=$true; " +
                "$p=[System.Diagnostics.Process]::Start($si); [IO.File]::WriteAllText('" + pidFile.Replace("'", "''") + "',$p.Id); " +
                "[Console]::WriteLine('PARENT-COMPLETED')";
            operation = service.ExecuteAsync(new ExecuteTerminalCommandRequest
            {
                Command = command, WorkingDirectory = root, TimeoutSeconds = cancelRequest ? 60 : 15
            }, cancellation.Token);
            if (cancelRequest)
            {
                using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (!File.Exists(pidFile)) await Task.Delay(25, startup.Token);
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(8)));
                return;
            }
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
            await DeleteOwnedRootAsync(root);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deadline_does_not_wait_for_readers_that_ignore_cancellation(bool cancelRequest)
    {
        var root = Path.Combine(Path.GetTempPath(), "adapanel-terminal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readerCount = 0;
        var readers = new List<Task<string>>();
        using var cancellation = new CancellationTokenSource();
        var service = new TerminalService(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }))
        {
            PipeReader = (reader, token) => {
                async Task<string> ReadAsync()
                {
                    // Consume actual process pipes, then hold completion to deterministically
                    // reproduce a Windows pipe operation which does not honor cancellation.
                    var output = await ProcessRunner.TavanliOkuAsync(reader, 200_000, token, preservePartialOnCancellation: true);
                    await release.Task;
                    return output;
                }
                var task = ReadAsync();
                readers.Add(task);
                if (Interlocked.Increment(ref readerCount) == 2) started.TrySetResult();
                return task;
            }
        };
        var operation = service.ExecuteAsync(new ExecuteTerminalCommandRequest {
            WorkingDirectory=root, TimeoutSeconds=cancelRequest ? 60 : 2,
            Command="[Console]::WriteLine('completed command with delayed pipe completion')"
        }, cancellation.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (cancelRequest) {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(8)));
            }
            else {
                var result = await operation.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(result.TimedOut);
                Assert.Equal(-1, result.ExitCode);
            }
            Assert.False(release.Task.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            // Release every delayed reader and observe its late outcome before deleting fixtures.
            try { await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(8)); } catch { }
            try { await operation.WaitAsync(TimeSpan.FromSeconds(8)); } catch { }
            await DeleteOwnedRootAsync(root);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bounded_reader_collection_preserves_the_completed_stream_and_observes_late_completion(bool completedOutput)
    {
        var delayed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = Task.FromResult("Türkçe partial diagnostic");
        try
        {
            var result = await ProcessRunner.TopluyorAsync(completedOutput ? completed : delayed.Task,
                completedOutput ? delayed.Task : completed, TimeSpan.FromMilliseconds(25))
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(completedOutput ? "Türkçe partial diagnostic" : "", result.Output);
            Assert.Equal(completedOutput ? "" : "Türkçe partial diagnostic", result.Error);
            Assert.False(delayed.Task.IsCompleted);
        }
        finally
        {
            delayed.TrySetResult("late pipe result");
            Assert.Equal("late pipe result", await delayed.Task);
        }
    }

    private static async Task DeleteOwnedRootAsync(string root)
    {
        const string prefix = "adapanel-terminal-";
        var target = Path.GetFullPath(root);
        var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        var name = Path.GetFileName(target);
        if (!string.Equals(Path.GetDirectoryName(target), tempRoot, StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[prefix.Length..], "N", out _))
            throw new InvalidOperationException("Terminal fixture cleanup is outside its owned temporary root.");

        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(target); }
            catch (DirectoryNotFoundException) { return; }
            catch (FileNotFoundException) { return; }
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
                throw new InvalidOperationException("Terminal fixture cleanup target is not an ordinary owned directory.");
            try
            {
                Directory.Delete(target, recursive: true);
                return;
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 &&
                elapsed.Elapsed < TimeSpan.FromSeconds(5))
            {
                // Process/outcome assertions have already finished. Retry only transient
                // sharing/lock failures while Windows releases the owned working directory.
                await Task.Delay(25);
            }
        }
    }
}
