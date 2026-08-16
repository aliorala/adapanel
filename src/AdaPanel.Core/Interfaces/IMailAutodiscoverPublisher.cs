using AdaPanel.Core.Dtos.Mail;

namespace AdaPanel.Core.Interfaces;

/// <summary>
/// Posta istemcisi otomatik yapılandırmasını, alan adının GERÇEKTEN servis edildiği yere bağlar.
/// </summary>
/// <remarks>
/// <para>
/// <b>Çözdüğü arıza:</b> panelde <c>/autodiscover/autodiscover.xml</c> ve
/// <c>/.well-known/autoconfig/mail/config-v1.1.xml</c> uçları vardı ve çalışıyordu, ama istemciler
/// oraya hiç ulaşmıyordu. Sebep basit: istemci <c>https://&lt;alan&gt;/…</c> adresine gider, o
/// adres IIS'te müşterinin sitesine bağlıdır; panel ise ayrı bir portta (Kestrel) dinler. Özellik
/// yazılmış, test edilmiş ama BAĞLANMAMIŞTI.
/// </para>
/// <para>
/// <b>Seçilen yol ve gerekçesi (ölçülerek):</b> alan adını servis eden IIS sitesinin
/// <c>web.config</c>'ine URL Rewrite + ARR ile üç adet <b>Rewrite</b> (yönlendirme değil, vekil)
/// kuralı yazılır ve istek panele iletilir. Alternatifler ve neden elenmediler:
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// <b>Site köküne statik XML yazmak:</b> Outlook autodiscover'ı <b>POST</b> ile çağırır; IIS'in
/// statik dosya işleyicisi statik bir dosyaya POST'a <c>405 Method Not Allowed</c> döner. Yani
/// Outlook için hiç çalışmazdı. Ayrıca panelin posta portları/ana adı/sertifikası değiştiğinde
/// dosya bayatlar ve kimse fark etmez.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Ayrı bir <c>autodiscover.&lt;alan&gt;</c> sitesi kurmak:</b> yeni bir sertifika, yeni bir
/// DNS kaydı ve yeni bir site gerektirir. Oysa Outlook'un İLK denediği adres
/// <c>https://&lt;alan&gt;/autodiscover/autodiscover.xml</c>'dir — yani müşterinin ZATEN geçerli
/// sertifikası olan kendi alan adı. En kısa ve en az kırılgan yol o adresi çalıştırmaktır.
/// </description>
/// </item>
/// </list>
/// <para>
/// <b>Kural sırası kritiktir:</b> kurallar listenin EN BAŞINA yazılır. WordPress'in IIS kuralı
/// "dosya/klasör değilse index.php'ye yaz" biçimindedir ve <c>/autodiscover/autodiscover.xml</c>
/// diskte bir dosya olmadığı için onu yakalayıp <c>stopProcessing</c> ile bitirirdi; kural sona
/// eklenseydi WordPress kurulu her sitede özellik sessizce ölü olurdu.
/// </para>
/// </remarks>
public interface IMailAutodiscoverPublisher
{
    /// <summary>
    /// Zinciri ÖLÇER, hiçbir şey yazmaz. Panel "kurulu mu" sorusunu yanıtlamak için kullanır.
    /// </summary>
    Task<MailAutodiscoverPublicationDto> GetStatusAsync(string domain, CancellationToken cancellationToken = default);

    /// <summary>
    /// Otomatik yapılandırmayı yayınlar: vekil kuralları, <c>autodiscover.</c>/<c>autoconfig.</c>
    /// bağlamaları, (mümkünse) DNS kayıtları ve sertifika. İdempotenttir; mevcut bağlamalara ve
    /// kullanıcının kendi yazdığı kurallara DOKUNMAZ.
    /// </summary>
    Task<MailAutodiscoverPublicationDto> PublishAsync(string domain, string contactEmail, CancellationToken cancellationToken = default);

    /// <summary>
    /// Yayını GERİ ALIR: panelin yazdığı kurallar ve eklediği <c>autodiscover.</c>/<c>autoconfig.</c>
    /// bağlamaları kaldırılır. Sitenin kendi bağlamalarına ve kullanıcının kurallarına dokunulmaz.
    /// </summary>
    Task<MailAutodiscoverPublicationDto> UnpublishAsync(string domain, CancellationToken cancellationToken = default);
}
