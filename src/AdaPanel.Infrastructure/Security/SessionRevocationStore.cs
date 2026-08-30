using System.Security.Cryptography;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Security;

/// <summary>
/// ÇIKIŞ YAPILMIŞ OTURUMLARIN SUNUCU TARAFINDAKİ KARA LİSTESİ.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ölçülen açık:</b> panelin oturum çerezi KENDİ KENDİNE YETERLİDİR — kimlik, roller ve bitiş
/// anı çerezin içinde şifreli olarak taşınır, sunucuda hiçbir kayıt tutulmaz. Bu yüzden
/// <c>HttpContext.SignOutAsync</c> yalnızca TARAYICIDAKİ çerezi siler; çerezin KENDİSİ geçerli
/// kalmaya devam eder. Yani "Çıkış" düğmesine basmak, çerezi daha önce ele geçirmiş birini
/// (omuz üstünden bakan, ödünç alınan bilgisayarın tarayıcı profili, ağ kaydı, XSS ile sızmış bir
/// değer) DIŞARI ATMIYORDU: aynı dize 12 saat (kalıcı seçilmişse 14 gün) boyunca çalışmaya devam
/// ediyordu. Bir güvenlik olayına verilebilecek en yaygın ilk yanıt — "her yerden çıkış yap" —
/// panelde fiilen bir görüntüden ibaretti.
/// </para>
/// <para>
/// <b>Neden oturum kimliği (sid), neden "kullanıcının bütün oturumlarını düşür" DEĞİL:</b>
/// kullanıcı başına tek bir "şu andan öncekiler geçersiz" damgası tutmak daha az kod olurdu ama
/// telefondan çıkış yapmak masaüstündeki oturumu da düşürürdü. Panelde bu, "çıkış yaptım, öbür
/// ekranda işimi kaybettim" demektir. Bu yüzden her girişte 128 bitlik bir oturum kimliği üretilir
/// (<see cref="NewSessionId"/>) ve YALNIZ çıkış yapan oturumun kimliği kara listeye yazılır.
/// </para>
/// <para>
/// <b>Neden oturum kimliği aynı zamanda oturum SABİTLEME (session fixation) kanıtıdır:</b> kimlik
/// her <c>SignInAsync</c>'te YENİDEN üretilir ve üretim noktası tektir
/// (<c>CookieSessionValidator.OnSigningInAsync</c>). Saldırganın kurbana önceden verdiği bir
/// çerezin girişten sonra da aynı oturum olarak sürmesi bu yüzden mümkün değildir: girişten sonraki
/// çerez BAŞKA bir oturumdur ve bu, bir yorumla değil bir testle ölçülebilir hâle gelir.
/// </para>
/// <para>
/// <b>Neden bellekte + dosyada:</b> denetim HER İSTEKTE çalışır (kimlik damgası denetiminin aksine
/// 60 saniyelik aralığa TAKILMAZ — çıkışın etkisi gecikmeli olamaz), yani sıcak yoldur ve disk
/// okuması kabul edilemez. Kara liste süreç ömrü boyunca bellekte tutulur; dosya yalnız yeniden
/// başlatmada okunur. Dosya şart: çerez biletleri yeniden başlatmadan SAĞ ÇIKAR (veri koruma
/// anahtarları kalıcıdır), dolayısıyla kara liste bellekte kalsaydı panelin yeniden başlaması
/// çıkış yapılmış bütün oturumları DİRİLTİRDİ.
/// </para>
/// <para>
/// <b>Neden dosya süresiz büyümez:</b> her kayıt <see cref="Retention"/> kadar tutulur ve her
/// yazımda süresi geçenler atılır. 15 gün, çerezin ulaşabileceği EN UZUN ömürden (kalıcı giriş =
/// 14 gün) daha uzundur; yani listeden düşen bir kimliğin çerezi zaten çoktan geçersizdir.
/// </para>
/// </remarks>
public sealed class SessionRevocationStore(JsonFileStore fileStore)
{
    /// <summary>Oturum kimliğini taşıyan talep adı. Çerezin içinde gider.</summary>
    public const string SessionIdClaim = "adapanel_sid";

    /// <summary>Kara listenin veri kökündeki yolu.</summary>
    public const string StorePath = "config/revoked-sessions.json";

    /// <summary>
    /// Bir kaydın kara listede kalma süresi. Kalıcı girişin çerez ömrü 14 gün; 15 gün onu aşar.
    /// </summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(15);

    private readonly SemaphoreSlim gate = new(1, 1);
    private Dictionary<string, DateTime>? cache;

    /// <summary>Yeni oturum kimliği: 128 bit kriptografik rastgelelik.</summary>
    public static string NewSessionId() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>Bu oturum kimliği çıkış yapılmış mı?</summary>
    public async ValueTask<bool> IsRevokedAsync(string? sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            // "Kimliği yok" kararını ÇAĞIRAN verir (bkz. CookieSessionValidator): bu depoda
            // "ölçemedik" ile "temiz" aynı şey değildir ve o ayrımı burada gizlemek yanlış olurdu.
            return false;
        }

        var liste = await EnsureLoadedAsync(cancellationToken);
        return liste.TryGetValue(sessionId, out var bitis) && bitis > DateTime.UtcNow;
    }

    /// <summary>Oturum kimliğini kara listeye alır (çıkış).</summary>
    public async Task RevokeAsync(string? sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var now = DateTime.UtcNow;
        await gate.WaitAsync(cancellationToken);
        try
        {
            var guncel = await fileStore.UpdateAsync(
                StorePath,
                new Store(),
                mevcut =>
                {
                    var kalan = mevcut.Entries
                        .Where(kayit => kayit.ExpiresUtc > now
                            && !string.Equals(kayit.SessionId, sessionId, StringComparison.Ordinal))
                        .ToList();
                    kalan.Add(new Entry { SessionId = sessionId, ExpiresUtc = now.Add(Retention) });
                    return new Store { Entries = kalan };
                },
                cancellationToken);

            cache = Haritala(guncel);
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask<Dictionary<string, DateTime>> EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        var mevcut = cache;
        if (mevcut is not null)
        {
            return mevcut;
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            // Kilidi bekleyen ikinci çağrı için tekrar bak: dosya bir kez okunsun.
            if (cache is null)
            {
                cache = Haritala(await fileStore.ReadAsync(StorePath, new Store(), cancellationToken));
            }

            return cache;
        }
        finally
        {
            gate.Release();
        }
    }

    private static Dictionary<string, DateTime> Haritala(Store store)
    {
        var now = DateTime.UtcNow;
        var harita = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var kayit in store.Entries)
        {
            if (!string.IsNullOrWhiteSpace(kayit.SessionId) && kayit.ExpiresUtc > now)
            {
                harita[kayit.SessionId] = kayit.ExpiresUtc;
            }
        }

        return harita;
    }

    /// <summary>Dosya biçimi.</summary>
    public sealed class Store
    {
        public List<Entry> Entries { get; set; } = [];
    }

    /// <summary>Tek bir kara liste kaydı. Oturum kimliği bir SIR DEĞİLDİR (çerezin içeriği
    /// şifrelidir ve kimlik tek başına oturum açmaya yaramaz), bu yüzden özetlenmeden saklanır —
    /// aksi hâlde her istekte bir özetleme maliyeti çıkardı ve karşılığında hiçbir şey
    /// kazanılmazdı.</summary>
    public sealed class Entry
    {
        public string SessionId { get; set; } = string.Empty;

        public DateTime ExpiresUtc { get; set; }
    }
}
