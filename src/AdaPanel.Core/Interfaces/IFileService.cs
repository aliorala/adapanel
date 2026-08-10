using AdaPanel.Core.Dtos.Files;

namespace AdaPanel.Core.Interfaces;

public interface IFileService
{
    Task<FileWorkspaceDto> GetWorkspaceAsync(string path, CancellationToken cancellationToken = default);

    Task<FileWorkspaceDto> WriteAsync(WriteFileRequest request, CancellationToken cancellationToken = default);

    Task<FileWorkspaceDto> DeleteAsync(DeleteFileRequest request, CancellationToken cancellationToken = default);

    Task<FileWorkspaceDto> CreateDirectoryAsync(CreateDirectoryRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bir ya da birden çok ögeyi başka klasöre TAŞIR; tek öge + <c>NewName</c> ile YENİDEN ADLANDIRIR.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden vardı olmalı:</b> panelde taşıma/yeniden adlandırma hiç yoktu — kullanıcı bir dosyayı
    /// komşu klasöre almak için indirip tekrar yüklemek zorundaydı (büyük dosyada pratikte imkânsız,
    /// klasörde ise hiç mümkün değil).
    /// </para>
    /// <para>
    /// <b>Sözleşme (uygulamanın uyması ZORUNLU olan kısım):</b>
    /// </para>
    /// <list type="bullet">
    /// <item>Kaynak ve hedef sanal alanın içinde olmalıdır; dışarı çıkan istek REDDEDİLİR (istisna).</item>
    /// <item>Yol üzerindeki hiçbir bağlantı (junction/symlink) TAKİP EDİLMEZ — bir bağlantıyı taşımak
    /// ya da üzerine yazmak, hedefindeki müşteri sitesini yok edebilir.</item>
    /// <item>Hedefte aynı adlı öge varsa VARSAYILAN davranış REDDETMEKTİR; üzerine yazma yalnız
    /// <c>Overwrite</c> ile ve yalnız DOSYALAR için mümkündür.</item>
    /// <item>Bir klasör KENDİ ALT AĞACINA taşınamaz.</item>
    /// <item>Kısmi başarısızlık istisna değil, öge başına RAPOR üretir.</item>
    /// </list>
    /// </remarks>
    Task<MoveFilesResultDto> MoveAsync(MoveFilesRequest request, CancellationToken cancellationToken = default);

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
