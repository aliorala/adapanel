using AdaPanel.Core.Dtos.Mail;

namespace AdaPanel.Core.Interfaces;

public interface IWebmailService
{
    /// <summary>
    /// Roundcube webmail'i (gerekirse kurup yapılandırarak) açacak URL'i döndürür.
    /// İlk çağrıda bileşeni kurar, Stalwart'a (IMAP 143 / Submission 587 STARTTLS) bağlı
    /// config yazar ve IIS PHP sitesi olarak yayınlar; sonraki çağrılar idempotenttir.
    /// </summary>
    /// <param name="publicHost">Tarayıcıdan erişilecek genel host (panel isteğinin host'u).</param>
    Task<WebmailOpenDto> OpenAsync(string publicHost, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bir mail domaini için webmail.&lt;domain&gt; adresini otomatik HTTPS yapar (site SSL sertifikası
    /// alındığında çağrılır). Best-effort: webmail kurulu değilse / DNS yoksa / sertifika alınamazsa sessiz geçer.
    /// </summary>
    Task SecureWebmailForDomainAsync(string domain, string contactEmail, CancellationToken cancellationToken = default);
}
