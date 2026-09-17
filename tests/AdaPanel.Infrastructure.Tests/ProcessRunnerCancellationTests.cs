using System.Diagnostics;
using System.Text;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

[Collection("Script host timing")]
public sealed class ProcessRunnerCancellationTests
{
    [Fact]
    public async Task Request_cancellation_terminates_the_started_process()
    {
        var pidFile = Path.GetTempFileName();
        using var cancellation = new CancellationTokenSource();
        try
        {
            var operation = Run("[IO.File]::WriteAllText('" + pidFile.Replace("'", "''") + "',$PID); Start-Sleep -Seconds 60", cancellation.Token);
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (string.IsNullOrWhiteSpace(await File.ReadAllTextAsync(pidFile))) await Task.Delay(25, startup.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(8)));
            using var process = FindProcess(pidFile);
            if (process is not null) await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { Cleanup(pidFile); }
    }

    [Fact]
    public async Task Failed_stdin_file_does_not_leave_a_reader_waiting_for_eof()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sql");
        var result = await ProcessRunner.RunAsync("cmd.exe", "/c more", timeout: TimeSpan.FromSeconds(15), standardInputFilePath: missing);
        Assert.False(result.Success);
        Assert.Contains("Standard input could not be written", result.StandardError);
        Assert.DoesNotContain("timed out", result.StandardError);
    }

    [Fact]
    public async Task Already_cancelled_request_does_not_start_a_command()
    {
        var marker = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run("[IO.File]::WriteAllText('" + marker.Replace("'", "''") + "','started')", cancellation.Token));
            await Task.Delay(2000);
            Assert.False(File.Exists(marker));
        }
        finally { File.Delete(marker); }
    }

    [Fact]
    public async Task Deadline_includes_pipes_inherited_by_a_child_after_parent_exit()
    {
        var pidFile = Path.GetTempFileName();
        Task<CommandResult>? operation = null;
        try
        {
            var script = "$si=[Diagnostics.ProcessStartInfo]::new('cmd.exe','/c ping -n 60 127.0.0.1'); " +
                "$si.UseShellExecute=$false; $si.CreateNoWindow=$true; $p=[Diagnostics.Process]::Start($si); " +
                "[IO.File]::WriteAllText('" + pidFile.Replace("'", "''") + "',$p.Id); [Console]::WriteLine('PARENT-COMPLETED')";
            operation = ProcessRunner.RunAsync("powershell.exe", "-NoProfile -NonInteractive -EncodedCommand " +
                Convert.ToBase64String(Encoding.Unicode.GetBytes(script)), timeout: TimeSpan.FromSeconds(10));
            var result = await operation.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(-1, result.ExitCode);
            Assert.Contains("timed out", result.StandardError);
            Assert.Contains("PARENT-COMPLETED", result.StandardOutput);
        }
        finally
        {
            Cleanup(pidFile);
            if (operation is not null) { try { await operation.WaitAsync(TimeSpan.FromSeconds(5)); } catch { } }
        }
    }

    private static Task<CommandResult> Run(string script, CancellationToken token) => ProcessRunner.RunAsync(
        "powershell.exe", "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
        cancellationToken: token, timeout: TimeSpan.FromSeconds(45));

    private static Process? FindProcess(string pidFile)
    {
        if (!int.TryParse(File.ReadAllText(pidFile), out var pid)) return null;
        try { return Process.GetProcessById(pid); }
        catch (ArgumentException) { return null; }
    }

    private static void Cleanup(string pidFile)
    {
        using var process = FindProcess(pidFile);
        if (process is not null && !process.HasExited) process.Kill(entireProcessTree: true);
        File.Delete(pidFile);
    }
}
