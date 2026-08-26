using System.Collections.Concurrent;

namespace AdaPanel.Infrastructure.SiteBuilder;

/// <summary>
/// İletişim formunun hız sınırı: KİMLİKSİZ bir ucun e-posta göndermesini sınırlar.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden var:</b> bu uç kimlik doğrulamıyor ve sonucunda POSTA GÖNDERİLİYOR. Sınırsız
/// bırakılsaydı tek bir betik, sunucunun giden posta kuyruğunu doldurup site sahibinin kutusunu
/// çöpe çevirebilir ve daha kötüsü sunucunun IP itibarını yakabilirdi. Bir sunucunun kara listeye
/// düşmesi, üzerindeki BÜTÜN kiracıların postasını etkiler — yani bu, tek bir sitenin sorunu değil.
/// </para>
/// <para>
/// <b>Üç ayrı sınır, üç ayrı soruyu kapatır:</b>
/// </para>
/// <list type="number">
/// <item>
/// <b>IP başına:</b> tek bir kaynağın tek bir siteyi ya da çok sayıda siteyi bombalamasını keser.
/// </item>
/// <item>
/// <b>Site başına:</b> dağıtık bir botnet farklı IP'lerden gelse bile SİTE SAHİBİNİN kutusunu
/// korur. IP sınırı tek başına bunu yapamaz; sahibi koruyan sınır budur.
/// </item>
/// <item>
/// <b>Gönderen başına bekleme:</b> aynı adresten arka arkaya gönderimi keser. Aynı zamanda
/// kullanıcı hatasını da düzeltir: "Gönder"e iki kez basan ziyaretçi iki kopya göndermez.
/// </item>
/// </list>
/// <para>
/// <b>Neden BELLEKTE, diskte değil:</b> uç kimliksiz. Sayaçları kalıcı hâle getirmek, kimliksiz
/// bir isteğe DİSKE YAZMA yeteneği vermek olurdu — yani sınırlamaya çalıştığımız kötüye kullanımı
/// ikinci bir kaynağa (disk) taşırdık. Bedeli açıkça kabul ediliyor: panel yeniden başladığında
/// pencereler sıfırlanır. Bunu kötüye kullanmak için saldırganın paneli yeniden başlatabilmesi
/// gerekir; o yetkiye sahipse zaten hız sınırından çok daha büyük bir sorun var.
/// </para>
/// <para>
/// <b>Saat DIŞARIDAN veriliyor:</b> pencerelerin gerçekten kaydığını ölçmek için testlerin zamanı
/// ileri alabilmesi gerek. <c>DateTime.UtcNow</c> içeride okunsaydı, hız sınırı ancak gerçek
/// saatler bekleyerek sınanabilirdi — yani pratikte hiç sınanmazdı.
/// </para>
/// </remarks>
public sealed class SiteBuilderContactRateLimiter
{
    /// <summary>Aynı IP'den en fazla kaç gönderim, hangi pencerede.</summary>
    public const int MaxPerIp = 5;

    public static readonly TimeSpan IpWindow = TimeSpan.FromMinutes(15);

    /// <summary>Aynı siteye en fazla kaç gönderim, hangi pencerede (site sahibinin kutusunu korur).</summary>
    public const int MaxPerSite = 30;

    public static readonly TimeSpan SiteWindow = TimeSpan.FromHours(1);

    /// <summary>Aynı gönderenin aynı siteye iki gönderimi arasındaki en az süre.</summary>
    public static readonly TimeSpan SenderCooldown = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Bir anahtarın sayaçlarının ne kadar süre saklanacağı — bellek sızıntısına karşı.
    /// </summary>
    /// <remarks>
    /// Sınırlayıcının kendisi bir kaynak tüketim yüzeyidir: her yeni IP yeni bir girdi demek.
    /// Temizlik olmasaydı, uzun süre ayakta kalan bir panelde sözlük sınırsız büyür ve hız
    /// sınırlayıcı, engellemeye çalıştığı kötüye kullanımın aracı olurdu.
    /// </remarks>
    private static readonly TimeSpan EntryLifetime = TimeSpan.FromHours(2);

    private readonly ConcurrentDictionary<string, List<DateTime>> _hits = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _senders = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private DateTime _lastSweepUtc = DateTime.MinValue;

    /// <summary>
    /// Bu gönderime izin var mı. İZİN VERİLİRSE sayaçlar İŞLENİR (yani çağrı yan etkilidir).
    /// </summary>
    /// <remarks>
    /// <para>
    /// "Sor" ve "işle" ayrı iki metot OLSAYDI, aradaki her erken dönüş yolu sayacı işlemeyi
    /// atlardı ve sınır sessizce gevşerdi. Tek metot, kararla sayacı aynı kilit altında tutar.
    /// </para>
    /// <para>
    /// <b>Sayaç yalnız İZİN VERİLEN istekte artar.</b> Reddedilen istekte de artsaydı, sürekli
    /// deneyen bir bot pencerenin hiç boşalmamasını sağlayıp GERÇEK ziyaretçileri kalıcı olarak
    /// dışarıda tutardı — yani saldırgan, hız sınırını bir hizmet engelleme aracına çevirirdi.
    /// </para>
    /// </remarks>
    public bool TryAcquire(string? ipAddress, string siteKey, string senderEmail, DateTime utcNow)
    {
        lock (_gate)
        {
            Sweep(utcNow);

            // IP okunamadığında (vekil arkası, IPv6 çözülemedi) sabit bir kova kullanılır. Bunu
            // "sınır yok" saymak, sınırı atlamanın en kolay yolunu açık bırakmak olurdu.
            var ipKey = "ip:" + (string.IsNullOrWhiteSpace(ipAddress) ? "bilinmeyen" : ipAddress.Trim());
            var siteBucket = "site:" + siteKey;
            var senderKey = siteKey + "|" + senderEmail.Trim();

            if (_senders.TryGetValue(senderKey, out var last) && utcNow - last < SenderCooldown)
            {
                return false;
            }

            if (CountWithin(ipKey, utcNow, IpWindow) >= MaxPerIp)
            {
                return false;
            }

            if (CountWithin(siteBucket, utcNow, SiteWindow) >= MaxPerSite)
            {
                return false;
            }

            Record(ipKey, utcNow);
            Record(siteBucket, utcNow);
            _senders[senderKey] = utcNow;
            return true;
        }
    }

    private int CountWithin(string key, DateTime utcNow, TimeSpan window)
    {
        if (!_hits.TryGetValue(key, out var stamps))
        {
            return 0;
        }

        stamps.RemoveAll(stamp => utcNow - stamp >= window);
        return stamps.Count;
    }

    private void Record(string key, DateTime utcNow)
    {
        var stamps = _hits.GetOrAdd(key, _ => []);
        stamps.Add(utcNow);
    }

    /// <summary>Süresi geçmiş girdileri atar. Kilit ALTINDA çağrılır.</summary>
    private void Sweep(DateTime utcNow)
    {
        // Her istekte tüm sözlüğü gezmek, yoğun bir siteyi yavaşlatırdı; süpürme seyrek.
        if (utcNow - _lastSweepUtc < TimeSpan.FromMinutes(5))
        {
            return;
        }

        _lastSweepUtc = utcNow;

        foreach (var pair in _hits.ToArray())
        {
            pair.Value.RemoveAll(stamp => utcNow - stamp >= EntryLifetime);
            if (pair.Value.Count == 0)
            {
                _hits.TryRemove(pair.Key, out _);
            }
        }

        foreach (var pair in _senders.ToArray())
        {
            if (utcNow - pair.Value >= EntryLifetime)
            {
                _senders.TryRemove(pair.Key, out _);
            }
        }
    }
}
