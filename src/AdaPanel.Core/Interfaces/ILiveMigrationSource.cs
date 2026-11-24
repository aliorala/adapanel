using AdaPanel.Core.Dtos.Migration;

namespace AdaPanel.Core.Interfaces;

public interface ILiveMigrationSource
{
    Task<LiveMigrationDiscovery> DiscoverAsync(LiveMigrationConnection connection, CancellationToken cancellationToken = default);
    /// <summary>Output root must come from private server job storage, never an API request field.</summary>
    Task<LiveMigrationAcquireResult> AcquireAsync(LiveMigrationConnection connection, IReadOnlyCollection<string> selectedUnitIds,
        string privateLocalOutputRoot, IProgress<LiveMigrationProgress>? progress = null, CancellationToken cancellationToken = default);
}
