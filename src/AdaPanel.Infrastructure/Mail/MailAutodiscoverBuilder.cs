using System.Text;
using System.Xml;
using AdaPanel.Core.Dtos.Mail;

namespace AdaPanel.Infrastructure.Mail;

/// <summary>
/// Outlook (autodiscover) ve Thunderbird (autoconfig) XML yanıtlarını üretir.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden ayrı bir üretici:</b> iki istemci ailesi aynı olguları farklı şemalarla ister. Aynı
/// değerleri iki denetleyici gövdesine elle yazmak, birinin güncellenip diğerinin unutulduğu
/// klasik sapmayı üretirdi — kullanıcı Outlook'ta doğru, Thunderbird'de yanlış ayar alırdı.
/// Tek kaynak <see cref="MailClientSettingsDto"/>'dur.
/// </para>
/// <para>
/// <b>Sabit değer YOKTUR:</b> sunucu adı, portlar ve şifreleme biçimi yalnızca verilen ayar
/// nesnesinden okunur. Bir protokol için sunucuda dinleyici yoksa o protokol XML'e HİÇ yazılmaz;
/// çalışmayan bir ayarı dağıtmak, kullanıcının elle kurmasından daha kötüdür.
/// </para>
/// <para>
/// <b>Sır yoktur:</b> yanıtlar kimlik doğrulamasız uçlardan döner. İçlerinde yalnızca ad/port/
/// şifreleme ve isteği yapanın KENDİ yazdığı e-posta adresi bulunur; hesap listesi, parola ya da
/// sunucu iç bilgisi taşınmaz.
/// </para>
/// </remarks>
public static class MailAutodiscoverBuilder
{
    /// <summary>Outlook POX (plain old XML) autodiscover yanıt şeması.</summary>
    private const string OutlookResponseNamespace =
        "http://schemas.microsoft.com/exchange/autodiscover/outlook/responseschema/2006a";

    private const string OutlookRootNamespace =
        "http://schemas.microsoft.com/exchange/autodiscover/responseschema/2006";

    /// <summary>
    /// Outlook <c>autodiscover.xml</c> yanıtı.
    /// </summary>
    /// <param name="settings">Panelin ölçtüğü gerçek ayarlar.</param>
    /// <param name="loginName">
    /// İstemcinin bildirdiği e-posta adresi. Yalnızca ayarların alan adına aitse yansıtılır;
    /// aksi halde boş bırakılır — yanıt, isteyenin yazdığı rastgele metni geri yayınlamamalıdır.
    /// </param>
    public static string BuildOutlookXml(MailClientSettingsDto settings, string? loginName)
    {
        var kullanici = NormalizeLogin(settings, loginName);
        var builder = new StringBuilder();
        using (var writer = CreateWriter(builder))
        {
            writer.WriteStartElement("Autodiscover", OutlookRootNamespace);
            writer.WriteStartElement("Response", OutlookResponseNamespace);

            writer.WriteStartElement("User");
            writer.WriteElementString("DisplayName", kullanici.Length > 0 ? kullanici : settings.Domain);
            writer.WriteEndElement();

            writer.WriteStartElement("Account");
            writer.WriteElementString("AccountType", "email");
            writer.WriteElementString("Action", "settings");

            foreach (var sunucu in settings.Servers)
            {
                var tur = OutlookProtocolName(sunucu.Protocol);
                if (tur is null)
                {
                    continue;
                }

                writer.WriteStartElement("Protocol");
                writer.WriteElementString("Type", tur);
                writer.WriteElementString("Server", sunucu.Host);
                writer.WriteElementString("Port", sunucu.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
                writer.WriteElementString("LoginName", kullanici);
                // Outlook iki ayrı bayrak bekler: SSL bağlantının doğrudan TLS ile mi açıldığını,
                // Encryption ise şifreleme türünü bildirir. STARTTLS'te SSL "off"tur ama bağlantı
                // yine şifrelenir — ikisini birleştirmek, STARTTLS'i "şifresiz" göstermek olurdu.
                var dogrudanTls = IsImplicitTls(sunucu.SocketType);
                writer.WriteElementString("SSL", dogrudanTls ? "on" : "off");
                writer.WriteElementString("Encryption", dogrudanTls ? "SSL" : "TLS");
                writer.WriteElementString("SPA", "off");
                writer.WriteElementString("AuthRequired", "on");
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        return builder.ToString();
    }

    /// <summary>Thunderbird <c>config-v1.1.xml</c> (autoconfig) yanıtı.</summary>
    public static string BuildAutoconfigXml(MailClientSettingsDto settings)
    {
        var builder = new StringBuilder();
        using (var writer = CreateWriter(builder))
        {
            writer.WriteStartElement("clientConfig");
            writer.WriteAttributeString("version", "1.1");

            writer.WriteStartElement("emailProvider");
            writer.WriteAttributeString("id", settings.Domain);
            writer.WriteElementString("domain", settings.Domain);
            writer.WriteElementString("displayName", settings.Domain);
            writer.WriteElementString("displayShortName", settings.Domain);

            foreach (var sunucu in settings.Servers)
            {
                var gelen = string.Equals(sunucu.Protocol, "smtp", StringComparison.Ordinal) ? "outgoingServer" : "incomingServer";
                writer.WriteStartElement(gelen);
                writer.WriteAttributeString("type", sunucu.Protocol);
                writer.WriteElementString("hostname", sunucu.Host);
                writer.WriteElementString("port", sunucu.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
                writer.WriteElementString("socketType", sunucu.SocketType);
                // %EMAILADDRESS% Thunderbird'ün kendi yer tutucusudur: kullanıcı adını SUNUCU
                // bildirmez, istemci kendi girdiği adresle doldurur. Böylece yanıtta hiçbir
                // hesap bilgisi taşınmaz.
                writer.WriteElementString("username", "%EMAILADDRESS%");
                writer.WriteElementString("authentication", "password-cleartext");
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        return builder.ToString();
    }

    private static XmlWriter CreateWriter(StringBuilder builder) =>
        XmlWriter.Create(builder, new XmlWriterSettings
        {
            Indent = true,
            Encoding = new UTF8Encoding(false),
            OmitXmlDeclaration = false
        });

    /// <summary>Panel protokol adını Outlook'un beklediği ada çevirir; desteklemediği protokol null.</summary>
    private static string? OutlookProtocolName(string protocol) => protocol switch
    {
        "imap" => "IMAP",
        "pop3" => "POP3",
        "smtp" => "SMTP",
        _ => null
    };

    private static bool IsImplicitTls(string socketType) =>
        string.Equals(socketType, "SSL", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// İstemcinin bildirdiği adresi doğrular: yalnızca AYARLARIN alan adına aitse yansıtılır.
    /// </summary>
    /// <remarks>
    /// Kimlik doğrulamasız bir uçta gelen metni geri yazmak, sunucuyu başkasının içeriğini kendi
    /// alan adı altından sunan bir yansıtıcıya çevirir. Adres alan adına ait değilse boş dönülür;
    /// istemci kullanıcı adını zaten kendisi bilir.
    /// </remarks>
    private static string NormalizeLogin(MailClientSettingsDto settings, string? loginName)
    {
        var deger = (loginName ?? string.Empty).Trim();
        if (deger.Length == 0 || deger.Length > 320)
        {
            return string.Empty;
        }

        var at = deger.IndexOf('@');
        if (at <= 0 || at == deger.Length - 1)
        {
            return string.Empty;
        }

        return string.Equals(deger[(at + 1)..], settings.Domain, StringComparison.OrdinalIgnoreCase)
            ? deger.ToLowerInvariant()
            : string.Empty;
    }
}
