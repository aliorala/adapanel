using AdaPanel.Core.Dtos.Backup;

namespace AdaPanel.Infrastructure.Backup;

internal static class BackupComponentCoverage
{
    // Component sources are Windows site/configuration names, mail domains and FTP accounts.
    // Database names retain their separate engine-aware identity contract.
    public static string Key(BackupComponentRef component) =>
        $"{component.Kind.Trim().ToUpperInvariant()}\0{component.Source.Trim().ToUpperInvariant()}";

    public static bool IsValid(BackupComponentRef? component) => component is not null &&
        !string.IsNullOrWhiteSpace(component.Kind) && !string.IsNullOrWhiteSpace(component.Source);

    public static List<BackupComponentRef>? Snapshot(IReadOnlyCollection<BackupComponentRef>? components) =>
        components?.Select(component => new BackupComponentRef
        {
            Kind = component.Kind.Trim().ToLowerInvariant(), Source = component.Source.Trim()
        }).DistinctBy(Key, StringComparer.Ordinal).ToList();
}
