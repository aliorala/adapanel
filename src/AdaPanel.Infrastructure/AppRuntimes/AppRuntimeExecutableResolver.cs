using System.Data.Common;
using AdaPanel.Core.Common;
using AdaPanel.Core.Enums;
using AdaPanel.Data.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AdaPanel.Infrastructure.AppRuntimes;

/// <summary>Shared executable discovery for the read-only inventory and actual hosting operations.</summary>
internal static class AppRuntimeExecutableResolver
{
    internal static async Task<string?> ResolveAsync(AdaPanelDbContext db, string? requestedComponentId,
        string runtimeType, CancellationToken token,
        Func<string, IEnumerable<string>>? additionalCandidates = null, Func<string, bool>? probe = null)
    {
        token.ThrowIfCancellationRequested();
        var (defaultId, executable) = runtimeType switch
        {
            "node" => ("nodejs-lts", "node.exe"),
            "python" => ("python-3", "python.exe"),
            _ => throw new ArgumentOutOfRangeException(nameof(runtimeType))
        };
        var componentId = string.IsNullOrWhiteSpace(requestedComponentId) ? defaultId : requestedComponentId.Trim();
        try
        {
            // Only identity/status/path are read. Discovery, credential decryption and inventory
            // synchronization belong to the administrator's software workflow, not this reader.
            var directory = await db.InstalledComponents.AsNoTracking()
                .Where(item => item.ComponentId == componentId && item.Status == ComponentInstallStatus.Installed)
                .Select(item => item.InstallDir)
                .FirstOrDefaultAsync(token);
            IEnumerable<string> Candidates()
            {
                if (!string.IsNullOrWhiteSpace(directory)) yield return Path.Combine(directory, executable);
                foreach (var candidate in (additionalCandidates ?? AdditionalCandidates)(runtimeType))
                    yield return candidate;
            }
            foreach (var candidate in Candidates().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                if ((probe ?? IsExecutableFile)(candidate)) return candidate;
            }
            token.ThrowIfCancellationRequested();
            return null;
        }
        catch (Exception error) when (error is DbException or IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            // A denied or failed probe cannot establish absence. Server paths and DB diagnostics
            // are not part of this tenant-facing error contract.
            throw new AppException("appruntime.inventory_unavailable", 503, 503);
        }
    }

    internal static IEnumerable<string> AdditionalCandidates(string runtimeType)
    {
        var fallback = runtimeType == "node"
            ? new[] { "%ProgramFiles%\\nodejs\\node.exe", "%ProgramFiles(x86)%\\nodejs\\node.exe" }
            : new[] { "%ProgramFiles%\\Python312\\python.exe", "%ProgramFiles%\\Python311\\python.exe",
                "%LocalAppData%\\Programs\\Python\\Python312\\python.exe", "%LocalAppData%\\Programs\\Python\\Python311\\python.exe" };
        foreach (var path in fallback) yield return Environment.ExpandEnvironmentVariables(path);
        var executable = runtimeType == "node" ? "node.exe" : "python.exe";
        // Match where.exe's executable roots without starting any command during a read.
        yield return Path.Combine(Environment.CurrentDirectory, executable);
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            var path = directory.Trim().Trim('"');
            if (path.Length > 0) yield return Path.Combine(Environment.ExpandEnvironmentVariables(path), executable);
        }
    }

    private static bool IsExecutableFile(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.Directory) == 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}
