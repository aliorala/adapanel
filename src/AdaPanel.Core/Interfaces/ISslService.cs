using AdaPanel.Core.Dtos.Ssl;

namespace AdaPanel.Core.Interfaces;

public interface ISslService
{
    Task<SslWorkspaceDto> GetSiteWorkspaceAsync(string siteName, CancellationToken cancellationToken = default);

    Task<SslCertificateDto> IssueLetsEncryptAsync(IssueLetsEncryptRequest request, CancellationToken cancellationToken = default);

    Task<SslCertificateDto> ImportCertificateAsync(ImportCertificateRequest request, CancellationToken cancellationToken = default);

    Task<RenewAllResultDto> RenewAllAsync(CancellationToken cancellationToken = default);

    /// <summary>HTTP→HTTPS kalıcı (301) yönlendirmesini açar/kapatır (panel "Force HTTPS").</summary>
    Task<SslWorkspaceDto> SetHttpsRedirectAsync(SetHttpsRedirectRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bir sitenin HTTPS bağlamasını kaldırır (panel kaydı dahil).
    /// <para>
    /// <b>Erişilebilirlik güvencesi:</b> kaldırılan bağlama sitenin SON HTTPS bağlamasıysa ve
    /// "Force HTTPS" yönlendirmesi açıksa, yönlendirme ÖNCE kapatılır — kapatılamıyorsa işlem
    /// <c>ssl.remove_would_break_site</c> ile reddedilir. Sertifikası olmayan bir siteyi HTTPS'e
    /// yönlendirmeye devam etmek, siteyi tamamen erişilemez bırakırdı.
    /// </para>
    /// <para>
    /// <b>Sertifika:</b> varsayılan olarak Windows sertifika deposunda BIRAKILIR (bağlama geri
    /// alınabilir, sertifika silmek geri alınamaz). Açıkça istenirse bile, aynı sertifikayı
    /// kullanan başka bir IIS bağlaması ÖLÇÜLDÜĞÜNDE silinmez.
    /// </para>
    /// </summary>
    Task<RemoveSslCertificateResultDto> RemoveCertificateAsync(RemoveSslCertificateRequest request, CancellationToken cancellationToken = default);

    /// <summary>Site silinince panelin SSL durumundaki o siteye ait kayıtları temizler (öksüz kalmasın).</summary>
    Task RemoveSiteAsync(string siteName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Domainsiz önizlemenin bıraktığı geçici host'a ait sertifika kayıtlarını siler ve kaç tane
    /// silindiğini döner. Önizleme host'u gerçek bir alan adı değildir; bağlaması kaldırıldığında
    /// sertifika kaydı öksüz kalır ve listede gerçek bir sertifika gibi görünür.
    /// </summary>
    Task<int> RemovePreviewCertificatesAsync(string siteName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Best-effort: bir host'u (ör. webmail.&lt;domain&gt;) verilen IIS sitesinde HTTPS yapar — önce host'u
    /// KAPSAYAN mevcut bir sertifika varsa (yüklenen wildcard dahil) onu bağlar, yoksa Let's Encrypt dener.
    /// HTTP→HTTPS yönlendirmesi EKLEMEZ (paylaşımlı webmail sitesini bozmaz). Başarılıysa true.
    /// </summary>
    Task<bool> TrySecureHostAsync(string siteName, string hostName, string contactEmail, CancellationToken cancellationToken = default);

    /// <summary>
    /// Domainsiz önizleme (nip.io) host'u için HIZLI self-signed HTTPS bağlaması ekler — ACME beklemez
    /// (buton donmaz), HTTP→HTTPS yönlendirmesini DEĞİŞTİRMEZ. Böylece https zorlayan siteler resetlenmez
    /// (tarayıcı bir kez sertifika uyarısı gösterir, "Devam et" ile açılır).
    /// </summary>
    Task EnsurePreviewCertificateAsync(string siteName, string hostName, CancellationToken cancellationToken = default);
}
