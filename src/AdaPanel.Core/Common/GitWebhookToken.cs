namespace AdaPanel.Core.Common;

/// <summary>
/// Git webhook jetonunun TAŞINMA BİÇİMİ — tek kaynak.
/// </summary>
/// <remarks>
/// <para>
/// <b>ÖLÇÜLEN AÇIK:</b> jeton yalnızca <c>?token=…</c> SORGU DİZESİYLE kabul ediliyordu. Sorgu
/// dizeleri IIS/W3C erişim günlüklerine olduğu gibi yazılır ve panelin KENDİ günlük görüntüleyicisi
/// vardır — yani dağıtım anahtarı, günlükleri okuyabilen herkese görünüyordu. O anahtarı elde eden
/// biri sitenin içeriğini kendi deposundan yeniden yazdırabilir (<c>fetch</c> + <c>reset --hard</c>,
/// yerel değişiklikleri EZER). Sızıntı yolu ayrıca kalıcıdır: günlükler haftalarca saklanır ve
/// yedeklenir.
/// </para>
/// <para>
/// <b>GERİYE DÖNÜK UYUM PAZARLIK KONUSU DEĞİL.</b> Kullanıcıların GitHub/GitLab arayüzüne
/// yapıştırdığı mevcut webhook adresleri sorgu dizesi taşıyor. Sorgu yolunu kapatmak, panel
/// güncellendiği anda o kancaların hepsini SESSİZCE öldürürdü (sağlayıcı 401 görür, kullanıcı
/// hiçbir şey görmez). Bu yüzden sorgu yolu KABUL EDİLMEYE DEVAM EDER; değişen şey panelin
/// ÜRETTİĞİ/GÖSTERDİĞİ biçim ve sorgu yolunun kullanıldığında DENETİM KAYDINA yazılmasıdır.
/// </para>
/// <para>
/// <b>Neden Core'da:</b> adı hem denetleyici (okumak için) hem DTO (arayüze bildirmek için)
/// kullanıyor. İki yere yazılsaydı biri değişip öteki kalırdı ve arayüz, sunucunun okumadığı bir
/// başlığı talimat olarak gösterirdi — kullanıcı için sebebi görünmeyen bir 401.
/// </para>
/// </remarks>
public static class GitWebhookToken
{
    /// <summary>
    /// Tercih edilen taşıyıcı: istek başlığı. Başlıklar erişim günlüklerine yazılmaz.
    /// </summary>
    /// <remarks>
    /// <c>Authorization</c> SEÇİLMEDİ ve bu bilinçli: panelin varsayılan kimlik şeması o başlığı
    /// okuyor (<c>PanelAuthentication.SelectScheme</c>), yani webhook jetonunu oraya koymak isteği
    /// "geçersiz panel anahtarı" yoluna sokardı. Kendi başlığımız, iki kimlik yolunu ayrı tutar.
    /// </remarks>
    public const string HeaderName = "X-AdaPanel-Token";

    /// <summary>Eski (ve hâlâ kabul edilen) taşıyıcı: sorgu dizesi alanı.</summary>
    public const string QueryName = "token";

    /// <summary>Jetonun hangi yoldan geldiği — denetim kaydına yazılır.</summary>
    public enum Transport
    {
        /// <summary>Jeton hiç gelmedi.</summary>
        None = 0,

        /// <summary>Başlıkla geldi (günlüğe düşmez).</summary>
        Header = 1,

        /// <summary>Sorgu dizesiyle geldi — erişim günlüklerine YAZILIR.</summary>
        Query = 2
    }

    /// <summary>
    /// Jetonu seçer. BAŞLIK ÖNCELİKLİ.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Öncelik sırası önemli: kullanıcı yeni biçime geçerken bir süre iki taşıyıcıyı birden
    /// gönderen bir kurulum olabilir (ör. eski adresi silmeden yeni başlığı eklemek). Başlık
    /// öncelikliyse geçiş, kullanıcı eski adresi temizlemeden ÇALIŞIR ve denetim kaydı da doğru
    /// olanı ("başlık") gösterir.
    /// </para>
    /// <para>
    /// Karar SAF bir fonksiyondur (HttpContext almaz): denetleyiciye gömülü olsaydı ancak tam bir
    /// HTTP boru hattı kurularak sınanabilirdi ve bu depoda o, "hiç sınanmadı" ile eş anlamlıdır.
    /// </para>
    /// </remarks>
    public static (string Token, Transport Source) Select(string? headerValue, string? queryValue)
    {
        var header = (headerValue ?? string.Empty).Trim();
        if (header.Length > 0)
        {
            return (header, Transport.Header);
        }

        var query = (queryValue ?? string.Empty).Trim();
        if (query.Length > 0)
        {
            return (query, Transport.Query);
        }

        return (string.Empty, Transport.None);
    }
}
