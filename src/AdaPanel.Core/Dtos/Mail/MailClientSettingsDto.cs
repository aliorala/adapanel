namespace AdaPanel.Core.Dtos.Mail;

/// <summary>
/// Bir alan adı için posta istemcisi (Outlook/Thunderbird/telefon) bağlantı ayarları.
/// </summary>
/// <remarks>
/// <para>
/// Autodiscover/autoconfig XML'lerinin TEK kaynağıdır. Değerler panelin gerçek posta
/// yapılandırmasından türetilir: sunucu adı sertifikadan/karşılama adından, portlar sunucunun
/// GERÇEK ağ dinleyicilerinden. Sabit port listesi yazmak, sunucu o portu dinlemiyorken
/// istemciye çalışmayan bir ayar dağıtmak demektir — kullanıcı için "panel yanlış söyledi"
/// hatası, elle kurmaktan daha kötüdür.
/// </para>
/// <para>
/// İçinde HİÇBİR sır ya da kullanıcı bilgisi yoktur: bu ayarlar kimlik doğrulamasız uçlardan
/// yayınlanır (istemciler öyle çağırır), dolayısıyla yalnızca zaten herkese açık olan
/// "hangi ada, hangi porttan, hangi şifrelemeyle bağlanılır" bilgisini taşır.
/// </para>
/// </remarks>
public sealed class MailClientSettingsDto
{
    /// <summary>Ayarların ait olduğu alan adı (panelde tanımlı olduğu doğrulanmıştır).</summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>İstemcinin bağlanacağı sunucu adı (TLS sertifikasının kapsadığı ad).</summary>
    public string HostName { get; set; } = string.Empty;

    /// <summary>Gerçek dinleyicilerden türetilmiş sunucu girdileri.</summary>
    public IReadOnlyCollection<MailClientServerDto> Servers { get; set; } = [];
}

/// <summary>Tek bir istemci bağlantı noktası (protokol + port + şifreleme).</summary>
public sealed class MailClientServerDto
{
    /// <summary>Protokol: <c>imap</c>, <c>pop3</c> ya da <c>smtp</c>.</summary>
    public string Protocol { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; }

    /// <summary>Şifreleme: <c>SSL</c> (doğrudan TLS) ya da <c>STARTTLS</c>.</summary>
    public string SocketType { get; set; } = string.Empty;
}
