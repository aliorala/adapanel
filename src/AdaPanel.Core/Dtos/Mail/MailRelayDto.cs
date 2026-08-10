namespace AdaPanel.Core.Dtos.Mail;

/// <summary>
/// Giden postanın bir SMTP röle (smarthost) üzerinden gönderilmesi ayarı.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden zorunlu:</b> bulut sağlayıcılarının çoğu 25/tcp ÇIKIŞINI kapatır. O sunucularda
/// posta hiç gitmez ve panelde bunun sebebini gösteren tek bir ekran bile yoktu. Rölenin
/// (SendGrid/Mailgun/ISS relay) tanımlanabilmesi, Windows barındırmada pratikte tercih değil
/// zorunluluktur.
/// </para>
/// <para>
/// <b>Burada PAROLA ALANI YOKTUR — kasıtlıdır.</b> Röle parolası panelde DPAPI ile şifreli
/// saklanır ve posta sunucusuna yazılır; hiçbir okuma yanıtında geri dönmez. Yanıta "maskeli"
/// bile olsa koymak, sırrın ikinci bir kopyasını (tarayıcı belleği, günlük, hata raporu)
/// üretirdi. Kullanıcı arayüzünün ihtiyacı olan tek bilgi <see cref="HasPassword"/>'dür.
/// </para>
/// </remarks>
public sealed class MailRelayDto
{
    /// <summary>Posta sunucusu bu makinede kurulu mu?</summary>
    public bool Installed { get; set; }

    /// <summary>Yönetim arayüzüne bağlanılabildi mi (ayar gerçekten okunabildi mi)?</summary>
    public bool Configured { get; set; }

    /// <summary>
    /// Röle FİİLEN devrede mi? Yalnızca röle tanımının varlığına değil, giden yönlendirme
    /// ifadesinin gerçekten bu röleyi göstermesine bakılır.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Röle sunucusunun adresi (ad ya da IP).</summary>
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; }

    /// <summary>Taşıma protokolü: <c>smtp</c> ya da <c>lmtp</c>.</summary>
    public string Protocol { get; set; } = string.Empty;

    /// <summary>Bağlantı doğrudan TLS ile mi açılıyor (465 gibi), yoksa STARTTLS mi?</summary>
    public bool ImplicitTls { get; set; }

    /// <summary>Röle sertifikası doğrulanmasın mı? (yalnız iç ağdaki röleler için).</summary>
    public bool AllowInvalidCerts { get; set; }

    /// <summary>Kimlik doğrulama kullanıcı adı (parola ASLA dönmez).</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Panelde saklı bir röle parolası var mı? (değerin kendisi değil, varlığı.)</summary>
    public bool HasPassword { get; set; }
}
