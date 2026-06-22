using System.Diagnostics;
using System.Text;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Terminal;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace AdaPanel.Infrastructure.Terminal;

public sealed class TerminalService(IOptions<PanelRuntimeOptions> runtimeOptions) : ITerminalService
{
    private const int MaximumOutputLength = 200_000;
    private readonly string dataRoot = Path.GetFullPath(
        Environment.ExpandEnvironmentVariables(runtimeOptions.Value.DataRoot));

    public async Task<TerminalCommandResultDto> ExecuteAsync(
        ExecuteTerminalCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        var workingDirectory = ResolveWorkingDirectory(request.WorkingDirectory);
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(request.Command.Trim()));
        var stopwatch = Stopwatch.StartNew();

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -EncodedCommand {encodedCommand}",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };

        try
        {
            process.Start();
        }
        catch
        {
            throw new AppException("terminal.start_failed", 500, 500);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            TryKill(process);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var standardOutput = await outputTask;
        var standardError = await errorTask;
        stopwatch.Stop();

        return new TerminalCommandResultDto
        {
            ExitCode = timedOut ? -1 : process.ExitCode,
            StandardOutput = Truncate(standardOutput),
            StandardError = Truncate(standardError),
            WorkingDirectory = workingDirectory,
            DurationMilliseconds = stopwatch.ElapsedMilliseconds,
            TimedOut = timedOut
        };
    }

    private string ResolveWorkingDirectory(string requestedPath)
    {
        var candidate = string.IsNullOrWhiteSpace(requestedPath)
            ? dataRoot
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(requestedPath.Trim()));

        var allowedRoots = new[]
        {
            dataRoot,
            Path.GetFullPath(AppContext.BaseDirectory),
            Path.GetFullPath(Environment.ExpandEnvironmentVariables("%SystemDrive%\\inetpub"))
        };

        if (!allowedRoots.Any(root => IsWithinRoot(candidate, root)))
        {
            throw new AppException("terminal.working_directory_denied", 403, 403);
        }

        if (!Directory.Exists(candidate))
        {
            throw new AppException("terminal.working_directory_missing", 404, 404);
        }

        return candidate;
    }

    private static bool IsWithinRoot(string candidate, string root)
    {
        var normalizedCandidate = Path.GetFullPath(candidate);
        var normalizedRoot = Path.GetFullPath(root);
        return string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.StartsWith(
                normalizedRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static string Truncate(string value)
    {
        return value.Length <= MaximumOutputLength
            ? value
            : value[..MaximumOutputLength] + Environment.NewLine + "[output truncated]";
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
        }
        catch
        {
            // The process may already have exited between the checks.
        }
    }
}
