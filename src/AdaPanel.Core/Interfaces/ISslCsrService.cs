using AdaPanel.Core.Dtos.Ssl;

namespace AdaPanel.Core.Interfaces;

public interface ISslCsrService
{
    Task<IReadOnlyCollection<SslCsrDto>> ListAsync(CancellationToken cancellationToken = default);

    Task<SslCsrDto> CreateAsync(CreateSslCsrRequest request, CancellationToken cancellationToken = default);

    Task<SslCsrDto> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// CA'nin imzaladigi sertifikayi, istegin OZEL ANAHTARIYLA eslestirip siteye baglar. Genel anahtar
    /// uyusmuyorsa <c>ssl.csr_certificate_mismatch</c> ile REDDEDER; hicbir sey yazilmaz.
    /// </summary>
    Task<SslCertificateDto> ImportSignedAsync(string id, ImportSignedCertificateRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Bekleyen istegi ve korunan ozel anahtarini siler.</summary>
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
}
