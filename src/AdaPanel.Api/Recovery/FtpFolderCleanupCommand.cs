using System.Security.Principal;
using System.Text.Json;
using AdaPanel.Infrastructure.Backup;

namespace AdaPanel.Api.Recovery;

public static class FtpFolderCleanupCommand
{
    public const string Switch = "--cleanup-empty-ftp-backup-folders";

    public static async Task<int> RunAsync(string[] args)
    {
        if (!OperatingSystem.IsWindows() || !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            return 2;
        if (args.Length != 2 || !Path.IsPathFullyQualified(args[1])) return 2;
        var dataRoot = Path.GetFullPath(args[1]);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var result = await BackupFtpFolderMaintenance.RunAsync(dataRoot, timeout.Token);
            Console.WriteLine(JsonSerializer.Serialize(result));
            return result.Unavailable == 0 ? 0 : 1;
        }
        catch (Exception exception)
        {
            // Do not print target credentials or protected settings in console logs.
            Console.Error.WriteLine("FTP folder maintenance failed: " + exception.GetType().Name);
            return 1;
        }
    }
}
