namespace AdaPanel.Infrastructure.Options;

public sealed class PanelRuntimeOptions
{
    public const string SectionName = "Panel";

    public string ServiceName { get; set; } = "AdaPanel";

    public string DataRoot { get; set; } = "%ProgramData%\\AdaPanel";

    public string DefaultLanguage { get; set; } = "tr";

    public int PreferredHttpsPort { get; set; } = 8888;

    public string SecureEntryPath { get; set; } = "panel";

    public bool AllowHttpFallback { get; set; }

    public string AuthCookieName { get; set; } = "AdaPanel.Auth";

    public string TotpIssuer { get; set; } = "AdaPanel";

    public int LoginLockoutMinutes { get; set; } = 15;

    /// <summary>
    /// KULLANICIDAN GELEN dış hedeflerin iç/özel ağ adreslerine bakmasına izin ver. VARSAYILAN KAPALI.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kapalıyken yedek uzak hedefi, zamanlanmış görev adresi, dosya göçü konağı, bildirim
    /// webhook'u/SMTP konağı ve git deposu adresi <see cref="Support.OutboundAddressGuard"/>'dan
    /// geçer; loopback, link-local (bulut meta verisi), özel ağ ve çoklu yayın adreslerine
    /// çözülenler reddedilir.
    /// </para>
    /// <para>
    /// <b>Bayrak NEDEN var — fazla kapatmak da bir hatadır.</b> Bu adreslerin bir kısmı MEŞRU
    /// biçimde içeride olur: iç ağdaki bir NAS'a yedek atmak, LAN'daki bir SMTP aktarıcısı, aynı
    /// makinedeki bir siteye vuran zamanlanmış görev. Körlemesine yasaklamak çalışan kurulumları
    /// kırardı; bu yüzden yasak kapatılabilir ama KAPALI OLDUĞU BİLİNEREK kapatılır.
    /// </para>
    /// <para>
    /// <b>Panelin KENDİ sabit yerel adresleri bu bayraktan ETKİLENMEZ</b> (phpMyAdmin vekili,
    /// Stalwart yönetimi, sağlık kontrolü, küme, <c>CacheService</c>/<c>WebDbClientService</c>):
    /// onlar kullanıcı girdisi değildir ve doğrulayıcıya hiç uğramaz.
    /// </para>
    /// <para>
    /// <b>Neden yalnız appsettings.json'da, panel arayüzünde DEĞİL:</b> gerekçe
    /// <see cref="Support.OutboundTargetPolicy.AllowInternalTargets"/> üzerinde yazılı — bayrağı
    /// HTTP yüzeyine koymak, korumayı korunan yüzeyin kendisinden kapatmaya izin vermek olurdu.
    /// </para>
    /// </remarks>
    public bool AllowInternalOutboundTargets { get; set; }

    /// <summary>
    /// Kimlik doğrulaması OLMAYAN bir SMTP oturumunun, sunucunun KENDİ barındırdığı bir alan adı
    /// adına posta göndermesine izin ver. VARSAYILAN KAPALI (yani sıkı).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kapalıyken panel Stalwart'ın <c>MtaStageMail.isSenderAllowed</c> yuvasına
    /// <see cref="Mail.MailSenderPolicy.Strict"/> ifadesini yazar ve DATA aşaması script'ine
    /// <c>From</c> başlığı hizalama bloğunu ekler. Gerekçenin tamamı
    /// <see cref="Mail.MailSenderPolicy"/> üzerinde yazılıdır: kimliksiz bağlantıya kendi alan
    /// adlarımızı bırakmak, aynı sunucudaki bir kiracının BAŞKA bir kiracının GELEN KUTUSUNA
    /// onun adına posta atmasına izin veriyordu (yalıtılmış kopyada gerçek SMTP oturumuyla
    /// kanıtlandı).
    /// </para>
    /// <para>
    /// <b>Bayrak NEDEN var — fazla kapatmak da bir hatadır.</b> Sıkı kural, sitelerin PHP
    /// <c>mail()</c> ile kimliksiz olarak <c>localhost:25</c>'ten kendi alan adları adına
    /// göndermesini de durdurur. Bu MEŞRU bir kullanımdır; doğru çözümü kimlik doğrulamalı
    /// gönderimdir (587/465 + kutu parolası) ve panel kullanıcıya bunu söyler
    /// (<c>mail.senderPolicy.*</c> metinleri). Ama bu geçişi yapamayan bir işletmecinin
    /// posta akışını tamamen kaybetmemesi için kapı bırakılmıştır: açıkken panel yuvaya
    /// Stalwart'ın FABRİKA varsayılanını geri yazar, yani anahtar gerçekten geri alınabilir.
    /// </para>
    /// <para>
    /// <b>Neden yalnız appsettings.json'da, panel arayüzünde DEĞİL:</b> gerekçe
    /// <see cref="AllowInternalOutboundTargets"/> ve paket güveni bayrağında da yazılı —
    /// korumayı korunan HTTP yüzeyinin kendisinden kapatılabilir yapmak, korumayı kaldırmakla
    /// aynı kapıya çıkar.
    /// </para>
    /// </remarks>
    public bool AllowUnauthenticatedLocalSenders { get; set; }
}
