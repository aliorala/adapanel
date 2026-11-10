using AdaPanel.Core.Dtos.Migration;

namespace AdaPanel.Core.Interfaces;

/// <summary>Migration-only fresh native zone creation; existing zones are never adopted.</summary>
public interface IMigrationDnsProvisioner
{
    Task<MigrationDnsSnapshot> InspectAsync(string zoneName, CancellationToken cancellationToken);
    Task<MigrationDnsImportResult> ImportFreshAsync(MigrationDnsImportRequest request, CancellationToken cancellationToken);
}
