using AdaPanel.Core.Dtos.Migration;

namespace AdaPanel.Core.Interfaces;

public interface IMigrationPackageStore
{
    Task<PortableMigrationPackage> UploadAsync(Stream zip, CancellationToken cancellationToken = default);
    Task<PortableMigrationManifest> ReadManifestAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Stream> OpenPayloadAsync(Guid id, string payloadPath, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
