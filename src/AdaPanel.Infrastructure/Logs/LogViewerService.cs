using AdaPanel.Core.Dtos.Logs;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace AdaPanel.Infrastructure.Logs;

public sealed class LogViewerService(
    IIisSiteService iisSiteService,
    IOptions<PanelRuntimeOptions> runtimeOptions) : ILogViewerService
{
    private readonly string dataRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(runtimeOptions.Value.DataRoot));

    public Task<LogTailDto> TailPanelLogAsync(int lines, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var logsDirectory = Path.Combine(dataRoot, "logs");
        Directory.CreateDirectory(logsDirectory);
        var logFile = new DirectoryInfo(logsDirectory)
            .GetFiles("*.log", SearchOption.TopDirectoryOnly)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault();

        if (logFile is null)
        {
            return Task.FromResult(new LogTailDto
            {
                Name = "AdaPanel",
                Path = logsDirectory
            });
        }

        return Task.FromResult(BuildTail(logFile, "AdaPanel", lines));
    }

    public async Task<LogTailDto> TailSiteLogAsync(string siteName, int lines, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var logsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System).Replace("System32", "inetpub\\logs\\LogFiles"),
            $"W3SVC{site.Id}");

        Directory.CreateDirectory(logsDirectory);
        var logFile = new DirectoryInfo(logsDirectory)
            .GetFiles("*.log", SearchOption.TopDirectoryOnly)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault();

        if (logFile is null)
        {
            return new LogTailDto
            {
                Name = siteName,
                Path = logsDirectory
            };
        }

        return BuildTail(logFile, siteName, lines);
    }

    private static LogTailDto BuildTail(FileInfo fileInfo, string name, int lines)
    {
        var safeLineCount = Math.Clamp(lines, 10, 500);
        var content = ReadTailLines(fileInfo.FullName, safeLineCount);

        return new LogTailDto
        {
            Name = name,
            Path = fileInfo.FullName,
            Lines = content,
            LastModifiedUtc = fileInfo.LastWriteTimeUtc
        };
    }

    private static string[] ReadTailLines(string path, int lineCount)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var tail = new Queue<string>(lineCount);

        while (reader.ReadLine() is { } line)
        {
            if (tail.Count == lineCount)
            {
                tail.Dequeue();
            }

            tail.Enqueue(line);
        }

        return tail.ToArray();
    }
}
