using AdaPanel.Core.Dtos.Files;

namespace AdaPanel.Core.Interfaces;

public interface IFileService
{
    Task<FileWorkspaceDto> GetWorkspaceAsync(string path, CancellationToken cancellationToken = default);

    Task<FileWorkspaceDto> WriteAsync(WriteFileRequest request, CancellationToken cancellationToken = default);

    Task<FileWorkspaceDto> DeleteAsync(DeleteFileRequest request, CancellationToken cancellationToken = default);

    Task<FileWorkspaceDto> CreateDirectoryAsync(CreateDirectoryRequest request, CancellationToken cancellationToken = default);

    Task<FileWorkspaceDto> SetPermissionsAsync(SetFilePermissionsRequest request, CancellationToken cancellationToken = default);

    Task<string> SaveUploadAsync(string path, Stream stream, CancellationToken cancellationToken = default);

    Task<(string FileName, string ContentType, byte[] Content)> DownloadAsync(string path, CancellationToken cancellationToken = default);

    Task<FileWorkspaceDto> CompressAsync(ArchiveFileRequest request, CancellationToken cancellationToken = default);

    Task<FileWorkspaceDto> ExtractAsync(ArchiveFileRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<FileEntryDto>> SearchAsync(SearchFilesRequest request, CancellationToken cancellationToken = default);
}
