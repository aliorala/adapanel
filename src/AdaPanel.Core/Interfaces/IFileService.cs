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

    /// <summary>
    /// İndirilecek dosyanın FİZİKSEL YOLUNU döndürür — içeriğini değil.
    /// <para>
    /// Önceden <c>byte[]</c> dönüyordu, yani dosya TAMAMEN belleğe alınıyordu ve boyut sınırı
    /// yoktu. Kiracının FTP/git ile koyduğu 3 GB'lık bir dosyayı indirmek tek bir dizi tahsisi
    /// demekti (2 GB üstü .NET'te zaten atılır) → OutOfMemoryException → panel süreci TÜM
    /// kiracılar için çöker. Yükleme 200 MB ile sınırlıydı, indirmede hiçbir sınır yoktu.
    /// </para>
    /// <para>
    /// Yol döndürmek, çağıranın dosyayı akış hâlinde vermesini sağlar (<c>PhysicalFile</c>) —
    /// yedek indirme akışında zaten kullanılan ve kanıtlanmış yöntem.
    /// </para>
    /// </summary>
    Task<(string FileName, string ContentType, string PhysicalPath)> DownloadAsync(string path, CancellationToken cancellationToken = default);

    Task<FileWorkspaceDto> CompressAsync(ArchiveFileRequest request, CancellationToken cancellationToken = default);

    Task<FileWorkspaceDto> ExtractAsync(ArchiveFileRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<FileEntryDto>> SearchAsync(SearchFilesRequest request, CancellationToken cancellationToken = default);
}
