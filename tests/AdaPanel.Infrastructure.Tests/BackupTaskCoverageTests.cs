using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Support;
using AdaPanel.Infrastructure.Options;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class BackupTaskCoverageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-task-coverage-" + Guid.NewGuid().ToString("N"));
    public BackupTaskCoverageTests() => Directory.CreateDirectory(root);
    public void Dispose() { try { Directory.Delete(root, true); } catch { } }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PanelBackupPersistsPartialStatusOnlyForExportFailure(bool fails)
    {
        var store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        await File.WriteAllTextAsync(Path.Combine(root, "config", "fixture.json"), "{}");
        var service = new BackupService(new SahteSiteServisi(root), null!, null!,
            new SahteTercihServisi(Path.Combine(root, "backups")), null!, store, null!)
        {
            TaskExportRunner = (_, _) => Task.FromResult(new CommandResult { ExitCode = fails ? 1 : 0, StandardOutput = "[]" })
        };
        var record = await service.BackupPanelAsync(new BackupPanelRequest());
        Assert.Equal(fails ? "created_with_errors" : "created", record.Status);
        Assert.Equal(fails ? 1 : 0, record.Warnings.Count(warning => warning.MessageKey == "backup.task_export_failed"));
        var persisted = Assert.Single(await service.GetBackupsAsync());
        Assert.Equal(record.Status, persisted.Status);
        Assert.Equal(record.Warnings.Select(item => item.MessageKey), persisted.Warnings.Select(item => item.MessageKey));
    }

    [Theory]
    [InlineData(0, "[]", 0)]
    [InlineData(1, "[]", 1)]
    [InlineData(0, "", 1)]
    [InlineData(0, "null", 1)]
    [InlineData(0, "[{\"Name\":\"daily\",\"Xml\":\"\"}]", 1)]
    public async Task ExportSeparatesMeasuredZeroFromFailure(int exitCode, string output, int expectedWarnings)
    {
        var warnings = new List<BackupRestoreWarningDto>();
        var tasks = await BackupService.TryExportScheduledTasksAsync(warnings, default, (script, _) =>
        {
            Assert.Contains("-ErrorAction Stop", script);
            Assert.DoesNotContain("SilentlyContinue", script);
            return Task.FromResult(new CommandResult { ExitCode = exitCode, StandardOutput = output, StandardError = "token=SECRET" });
        });
        Assert.Empty(tasks);
        Assert.Equal(expectedWarnings, warnings.Count);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(warnings));
    }

    [Fact]
    public async Task ExportRetainsTaskDefinitionsAndPropagatesCancellation()
    {
        var warnings = new List<BackupRestoreWarningDto>();
        var tasks = await BackupService.TryExportScheduledTasksAsync(warnings, default, (_, _) => Task.FromResult(
            new CommandResult { StandardOutput = "[{\"Name\":\"daily\",\"Xml\":\"<Task/>\"}]" }));
        Assert.Equal("daily", Assert.Single(tasks).Name);
        Assert.Empty(warnings);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BackupService.TryExportScheduledTasksAsync(warnings,
            default, (_, _) => throw new OperationCanceledException()));
        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData(".bat")]
    [InlineData(".ps1")]
    [InlineData(".cmd")]
    public async Task ScriptIsArchivedAndRestoreRequiresBothCopiesToMatch(string extension)
    {
        var source = Path.Combine(root, "daily job" + extension);
        await File.WriteAllTextAsync(source, "echo fixture");
        var task = TaskFor(source);
        var warnings = new List<BackupRestoreWarningDto>();
        List<BackupService.TaskScriptEntry> scripts;
        using (var zip = ZipFile.Open(Path.Combine(root, "backup.zip"), ZipArchiveMode.Create))
            scripts = await BackupService.ArchiveTaskScriptsAsync(zip, task, 0, warnings, default);
        Assert.Empty(warnings);
        Assert.Equal(source, Assert.Single(scripts).Path);
        var extracted = Path.Combine(root, "extracted");
        ZipFile.ExtractToDirectory(Path.Combine(root, "backup.zip"), extracted);
        Assert.True(await BackupService.TaskScriptsMatchAsync(extracted, scripts, default));
        var archivedScript = Path.Combine(extracted, scripts[0].Entry.Replace('/', Path.DirectorySeparatorChar));
        await File.WriteAllTextAsync(archivedScript, "corrupt archive content");
        Assert.False(await BackupService.TaskScriptsMatchAsync(extracted, scripts, default));
        await File.WriteAllTextAsync(archivedScript, "echo fixture");
        Assert.True(await BackupService.TaskScriptsMatchAsync(extracted, scripts, default));
        await File.WriteAllTextAsync(source, "changed");
        Assert.False(await BackupService.TaskScriptsMatchAsync(extracted, scripts, default));
        File.Delete(source);
        Assert.False(await BackupService.TaskScriptsMatchAsync(extracted, scripts, default));
    }

    [Fact]
    public async Task MissingScriptRemainsAnExplicitIncompleteDependency()
    {
        var warnings = new List<BackupRestoreWarningDto>();
        using var zip = ZipFile.Open(Path.Combine(root, "backup.zip"), ZipArchiveMode.Create);
        var scripts = await BackupService.ArchiveTaskScriptsAsync(zip, TaskFor(Path.Combine(root, "absent.bat")), 0, warnings, default);
        Assert.Equal("", Assert.Single(scripts).Sha256);
        Assert.Single(warnings);
        Assert.False(await BackupService.TaskScriptsMatchAsync(root, scripts, default));
    }

    [Fact]
    public async Task RestoreDoesNotRegisterWhenOriginalScriptIsMissing()
    {
        var source = Path.Combine(root, "absent.bat");
        var task = TaskFor(source);
        Directory.CreateDirectory(Path.Combine(root, "tasks"));
        await File.WriteAllTextAsync(Path.Combine(root, "tasks", "000.xml"), task.Xml);
        await File.WriteAllTextAsync(Path.Combine(root, "tasks", "index.json"), JsonSerializer.Serialize(new[] {
            new { name = task.Name, entry = "tasks/000.xml", scripts = new[] { new { path = source, entry = "tasks/scripts/0.bat", sha256 = new string('A', 64) } } }
        }));
        var warnings = new List<BackupRestoreWarningDto>();
        var calls = 0;
        await BackupService.TryRestoreScheduledTasksAsync(root, true, warnings, (_, _, _) =>
        { calls++; return Task.FromResult(new CommandResult()); }, default);
        Assert.Equal(0, calls);
        Assert.Equal("backup.restore_task_script_missing", Assert.Single(warnings).MessageKey);
    }

    [Fact]
    public async Task ReparsePointIsNotFollowed()
    {
        var realDirectory = Path.Combine(root, "real");
        Directory.CreateDirectory(realDirectory);
        var source = Path.Combine(realDirectory, "original.bat");
        await File.WriteAllTextAsync(source, "echo fixture");
        // A directory junction needs no symbolic-link privilege on Windows.
        var linkDirectory = Path.Combine(root, "linked");
        var linkResult = await ProcessRunner.RunAsync("cmd.exe", $"/d /c mklink /J \"{linkDirectory}\" \"{realDirectory}\"");
        Assert.True(linkResult.Success, linkResult.StandardError);
        var link = Path.Combine(linkDirectory, "original.bat");
        var warnings = new List<BackupRestoreWarningDto>();
        List<BackupService.TaskScriptEntry> scripts;
        using (var zip = ZipFile.Open(Path.Combine(root, "backup.zip"), ZipArchiveMode.Create))
            scripts = await BackupService.ArchiveTaskScriptsAsync(zip, TaskFor(link), 0, warnings, default);
        Assert.Single(warnings);
        Assert.Empty(Assert.Single(scripts).Sha256);
        using var read = ZipFile.OpenRead(Path.Combine(root, "backup.zip"));
        Assert.Empty(read.Entries);
    }

    private static BackupService.ScheduledTaskExport TaskFor(string path)
    {
        var isPowerShell = path.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase);
        var xml = new XElement("Task", new XElement("Actions", new XElement("Exec",
            new XElement("Command", isPowerShell ? "powershell.exe" : "cmd.exe"),
            new XElement("Arguments", isPowerShell ? $"-NoProfile -File \"{path}\"" : $"/c \"\"{path}\"\""))));
        return new("daily", xml.ToString());
    }
}
