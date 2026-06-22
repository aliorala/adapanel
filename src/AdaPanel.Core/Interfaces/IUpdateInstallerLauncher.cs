namespace AdaPanel.Core.Interfaces;

public interface IUpdateInstallerLauncher
{
    Task ScheduleAsync(
        string packagePath,
        string packageSha256,
        CancellationToken cancellationToken = default);
}
