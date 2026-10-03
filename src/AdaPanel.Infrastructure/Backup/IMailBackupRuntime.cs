using AdaPanel.Core.Dtos.Backup;

namespace AdaPanel.Infrastructure.Backup;

/// <summary>The operating-system boundary used while copying the offline mail store.</summary>
public interface IMailBackupRuntime
{
    string GetInstallDirectory();
    Task StopAsync(string installDirectory, CancellationToken cancellationToken);
    Task<BackupRestoreWarningDto?> StartAsync(CancellationToken cancellationToken);
}
