using System.Diagnostics;
using System.Text;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Terminal;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
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
        cancellationToken.ThrowIfCancellationRequested();
        var workingDirectory = ResolveWorkingDirectory(request.WorkingDirectory);
        var script = "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n$ProgressPreference = 'SilentlyContinue'\n" + request.Command.Trim();
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
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

        // TAVANLI OKUMA. Eski hâli ReadToEndAsync idi: cikti TAMAMEN bellege aliniyor, sonra
        // gosterim icin kirpiliyordu -- yani kirpma bellegi hic korumuyordu. Tek bir
        // "Get-ChildItem C:\ -Recurse" paneli dusurmeye yeter.
        //
        // 0.8.9'da bu acik ProcessRunner'da kapatilmisti, ama terminal kendi surecini kuruyor
        // (kodlanmis komut + calisma dizini) ve o kapidan GECMIYORDU. Bu depoda tekrar eden
        // sinif: koruma yazilir, tek bir yola baglanir, kardes yol sessizce disarida kalir.
        // Kural ikinci kez yazilmadi; ProcessRunner'in metodu paylasildi -- tavan asildiktan
        // SONRA boruyu tuketmeye devam etme davranisi da (yoksa arac write'ta asilir) oradan geliyor.
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));
        var outputTask = ProcessRunner.TavanliOkuAsync(
            process.StandardOutput, ProcessRunner.DefaultOutputCharLimit, timeoutSource.Token, preservePartialOnCancellation: true);
        var errorTask = ProcessRunner.TavanliOkuAsync(
            process.StandardError, ProcessRunner.DefaultOutputCharLimit, timeoutSource.Token, preservePartialOnCancellation: true);

        try
        {
            // A child can retain stdout/stderr after PowerShell exits. The deadline applies
            // to the entire request, including those pipes, rather than only the parent PID.
            await Task.WhenAll(process.WaitForExitAsync(timeoutSource.Token), outputTask, errorTask);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            await TryKillAsync(process);
        }
        catch
        {
            await TryKillAsync(process);
            throw;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var timedOut = timeoutSource.IsCancellationRequested;
        if (timedOut) await TryKillAsync(process);
        var standardOutput = await outputTask;
        var standardError = await errorTask;
        stopwatch.Stop();

        return new TerminalCommandResultDto
        {
            ExitCode = timedOut ? -1 : process.ExitCode,
            StandardOutput = Truncate(standardOutput),
            StandardError = Truncate(ProcessRunner.NormalizePowerShellStream(standardError)),
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

    private static async Task TryKillAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                // Allow handles and the working-directory lease to close, with a bounded cleanup.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await process.WaitForExitAsync(cleanup.Token);
            }
        }
        catch
        {
            // The process may already have exited between the checks.
        }
    }
}
