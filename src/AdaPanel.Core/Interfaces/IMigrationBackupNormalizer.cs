using AdaPanel.Core.Dtos.Migration;

namespace AdaPanel.Core.Interfaces;

public interface IMigrationBackupNormalizer
{
    Task<PortableMigrationPackage> NormalizeAsync(string provider, Stream input, CancellationToken cancellationToken = default);
}
