using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Infrastructure.Scheduler;
using AdaPanel.Infrastructure.Security;
using AdaPanel.Infrastructure.SiteBuilder;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Backup;

public sealed partial class BackupService
{
    // Internal seam keeps backup tests away from the host machine's Task Scheduler.
    internal Func<string, CancellationToken, Task<CommandResult>>? TaskExportRunner { get; set; }
    internal sealed record TaskScriptEntry(string Path, string Entry, string Sha256);
    private const long MaxTaskScriptBytes = 16 * 1024 * 1024;

    // Only direct script actions and the panel's own cmd/PowerShell wrappers are followed.
    // Script contents, command arguments, imported modules and sibling files are never traversed.
    internal static IReadOnlyList<string> ReferencedTaskScripts(string xml)
    {
        var document = XDocument.Parse(xml);
        var paths = new List<string>();
        foreach (var action in document.Descendants().Where(node => node.Name.LocalName == "Exec"))
        {
            var command = action.Elements().FirstOrDefault(node => node.Name.LocalName == "Command")?.Value ?? "";
            var arguments = action.Elements().FirstOrDefault(node => node.Name.LocalName == "Arguments")?.Value ?? "";
            // Logged actions wrap both scripts and the panel's URL runner in cmd.exe.
            // The installed URL runner is not a user script to copy into the backup.
            if (SchedulerService.TryUnwrapLoggedAction(command, arguments, out var executable, out var unwrapped)
                && (ScriptTaskPlanner.IsAllowedExtension(executable)
                    || ScriptTaskPlanner.IsScriptAction(executable, unwrapped)
                    || UrlTaskRunner.IsUrlAction(executable, unwrapped)))
            {
                command = executable;
                arguments = unwrapped;
            }
            if (UrlTaskRunner.IsUrlAction(command, arguments)) continue;
            if (ScriptTaskPlanner.IsAllowedExtension(command)) paths.Add(command);
            else if (ScriptTaskPlanner.TryResolveScriptPath(command, arguments, out var path, out _)) paths.Add(path);
            else if (ScriptTaskPlanner.IsScriptAction(command, arguments))
                throw new IOException("Script action cannot be resolved safely.");
        }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string ValidateTaskScriptPath(string rawPath)
    {
        var path = ScriptTaskPlanner.NormalizePath(rawPath);
        if (path is null || !ScriptTaskPlanner.IsAllowedExtension(path)
            || PanelWriteTargetGuard.Evaluate(path, null) != PanelWriteTargetVerdict.Allowed
            || path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => new[] { "keys", ".ssh", ".aws", ".env" }.Contains(segment, StringComparer.OrdinalIgnoreCase)))
            throw new IOException("Script path is outside the allowed local user file scope.");
        SiteBuilderPathGuard.EnsureNoReparsePointOnPath(System.IO.Path.GetPathRoot(path)!, path);
        return path;
    }

    internal static async Task<List<TaskScriptEntry>> ArchiveTaskScriptsAsync(
        ZipArchive archive, ScheduledTaskExport task, int taskIndex,
        List<BackupRestoreWarningDto> warnings, CancellationToken cancellationToken)
    {
        var scripts = new List<TaskScriptEntry>();
        try
        {
            foreach (var rawPath in ReferencedTaskScripts(task.Xml))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entryName = $"tasks/scripts/{taskIndex:000}/{scripts.Count:000}{System.IO.Path.GetExtension(rawPath).ToLowerInvariant()}";
                // An empty digest persists the missing dependency in the manifest as well.
                var index = scripts.Count;
                scripts.Add(new(rawPath, entryName, ""));
                try
                {
                    var path = ValidateTaskScriptPath(rawPath);
                    await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (source.Length > MaxTaskScriptBytes) throw new IOException("Script exceeds size limit.");
                    using var memory = new MemoryStream();
                    await source.CopyToAsync(memory, cancellationToken);
                    if (memory.Length > MaxTaskScriptBytes) throw new IOException("Script exceeds size limit.");
                    var bytes = memory.ToArray();
                    var hash = Convert.ToHexString(SHA256.HashData(bytes));
                    await using var destination = archive.CreateEntry(entryName, CompressionLevel.Optimal).Open();
                    await destination.WriteAsync(bytes, cancellationToken);
                    scripts[index] = new(path, entryName, hash);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    warnings.Add(TaskScriptWarning("backup.task_script_missing", task.Name));
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Preserve an explicit unresolved dependency so restore cannot silently register it.
            scripts.Add(new("", "", ""));
            warnings.Add(TaskScriptWarning("backup.task_script_missing", task.Name));
        }
        return scripts;
    }

    internal static async Task<bool> TaskScriptsMatchAsync(string extractedRoot, IReadOnlyList<TaskScriptEntry> scripts,
        CancellationToken cancellationToken)
    {
        foreach (var script in scripts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (script.Sha256.Length != 64) return false;
                var originalPath = ValidateTaskScriptPath(script.Path);
                var archivedPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(extractedRoot,
                    script.Entry.Replace('/', System.IO.Path.DirectorySeparatorChar)));
                var scriptsRoot = System.IO.Path.Combine(extractedRoot, "tasks", "scripts");
                if (!PathContainment.IsInsideRoot(scriptsRoot, archivedPath)) return false;
                SiteBuilderPathGuard.EnsureNoReparsePointOnPath(System.IO.Path.GetPathRoot(archivedPath)!, archivedPath);
                foreach (var path in new[] { originalPath, archivedPath })
                {
                    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (stream.Length > MaxTaskScriptBytes) return false;
                    var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
                    if (!string.Equals(hash, script.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException) { return false; }
        }
        return true;
    }

    private static BackupRestoreWarningDto TaskScriptWarning(string key, string taskName) => new()
    {
        MessageKey = key, Target = taskName,
        Detail = "Çağrılan betik eksik, kapsam dışında veya içeriği farklı. Betiği arşivden doğrulayıp özgün yoluna ayrıca aktarın; alt bağımlılıklar ve çalışma hesabı izinleri ayrıca taşınmalıdır."
    };
}
