using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using AdaPanel.Core.Dtos.Common;
using Microsoft.AspNetCore.RateLimiting;

namespace AdaPanel.Api.Security;

/// <summary>
/// Panelin BÜTÜN hız sınırları — TEK KAYNAK.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden tek dosya:</b> sınır değerleri denetleyicilere dağıldığında biri güncellenir öteki
/// kalır — bu depoda defalarca ölçülmüş hata sınıfı. Denetleyiciler yalnızca politikanın ADINI
/// seçer (<see cref="EnableRateLimitingAttribute"/>), değeri BURADAN okur. Değerler ayrıca
/// <c>public const</c>: testler tahmin etmek yerine aynı sabiti okur.
/// </para>
///
/// <para><b>ÖLÇÜM — arayüz hangi ucu ne sıklıkla yokluyor</b> (fazla kapatma en büyük risk):</para>
/// <list type="table">
/// <item><c>MonitoringView.vue:495</c> — <c>GET /api/v1/monitoring/processes</c>, 2 sn → <b>30/dk</b></item>
/// <item><c>ResourceConsumers.vue:465</c> (varsayılan <c>intervalMs=3000</c>;
///   <c>MonitoringView.vue:332</c> ve <c>DatabasesView.vue:63</c>) —
///   <c>GET /api/v1/monitoring/consumers</c>, 3 sn ve tur başına İKİ istek olabiliyor
///   (IIS ilişkilendirmesi, <c>ResourceConsumers.vue:423</c>) → <b>20–40/dk</b></item>
/// <item><c>MonitoringView.vue:492</c> — <c>GET /api/v1/system/history</c>, 60 sn → <b>1/dk</b></item>
/// <item><c>SetupWizardView.vue:1698</c> — <c>GET /api/v1/system/overview</c>, 15 sn → <b>4/dk</b></item>
/// <item><c>SetupWizardView.vue:1706</c> — dikkat rozeti, 60 sn → <b>1/dk</b></item>
/// <item><c>SeoView.vue:611</c> — <c>GET /api/v1/seo/sites/{ad}</c>, 3 sn → <b>20/dk</b></item>
/// <item><c>CronView.vue:777</c> — görev durumu, 2,5 sn, <b>görev başına</b> → <b>24/dk × N</b></item>
/// <item><c>SecurityView.vue:621</c> ve <c>useRunningTask.ts:234</c> — 15 sn → <b>4/dk</b></item>
/// <item><c>UpdateView.vue:271</c> — <c>GET /api/v1/update/check</c>, kurulum sırasında 5 sn → <b>12/dk</b></item>
/// </list>
/// <para>
/// TEK SEKMEDE ÖLÇÜLEN EN KÖTÜ HÂL: İzleme ekranı = 30 + 40 + 1 = <b>71 istek/dk</b>. Sınırlar bu
/// sayıdan türetildi, tahminden değil.
/// </para>
///
/// <para><b>NEDEN İKİ AYRI BÖLÜM (yoklama / genel):</b> yoklama SÜREKLİ ve UCUZ, kullanıcı
/// eylemleri SEYREK ve PAHALI. Tek kovaya konsaydı, İzleme ekranını açık bırakan bir yönetici
/// kendi tıklama bütçesini yoklamayla tüketirdi — yani sınır, tam da hiçbir kötüye kullanım
/// olmayan durumda paneli kırardı.</para>
///
/// <para><b>NEDEN ÇEREZ YOLUNDA ARTIK "sınırsız" DEĞİL:</b> önceki hâl
/// <c>RateLimitPartition.GetNoLimiter("cookie")</c> döndürüyordu; yani 66 denetleyicinin
/// ~64'ünde hiçbir tavan yoktu ve bu bir TESTLE "beklenen davranış" diye çivilenmişti
/// (<c>PanelTokenRestAccessTests.Anahtarli_isteklerin_hiz_siniri_var</c>). Tavan artık var ama
/// ölçülen en kötü hâlin katları biçiminde: amaç arayüzü engellemek değil, sızmış bir oturumla
/// veri kazımayı ve kaba kuvvet denemesini ölçülebilir biçimde yavaşlatmak.</para>
/// </remarks>
public static class PanelRateLimits
{
    // ---------------------------------------------------------------------------------------
    // POLİTİKA ADLARI — denetleyiciler YALNIZ bunları yazar.
    // ---------------------------------------------------------------------------------------

    /// <summary>Giriş denemesi (IP başına).</summary>
    public const string Login = "login";

    /// <summary>Parola değişimi — mevcut parolayı doğruladığı için tahmin yüzeyi (IP başına).</summary>
    public const string PasswordChange = "password-change";

    /// <summary>İlk kurulum sihirbazı (IP başına).</summary>
    public const string Setup = "setup";

    /// <summary>MCP ucu (anahtar başına).</summary>
    public const string Mcp = "mcp";

    /// <summary>
    /// KİMLİKSİZ ve POSTA GÖNDEREN uçlar: dağıtım listesinden çıkma isteği, site kurucu iletişim
    /// formu. Panelin en pahalı anonim eylemi budur — sunucunun posta itibarını yakabilir.
    /// </summary>
    public const string PublicMailSend = "public-mail-send";

    /// <summary>
    /// KİMLİKSİZ ama posta GÖNDERMEYEN uçlar: abonelikten çıkma sayfaları/onayı, autodiscover,
    /// marka bilgisi.
    /// </summary>
    public const string PublicRead = "public-read";

    /// <summary>Git sağlayıcısının anonim webhook tetikleyicisi (IP başına).</summary>
    public const string GitWebhook = "git-webhook";

    // ---------------------------------------------------------------------------------------
    // DEĞERLER — hepsi burada, gerekçesiyle.
    // ---------------------------------------------------------------------------------------

    /// <summary>Giriş: 5 deneme / dakika / IP. (Mevcut değer korundu.)</summary>
    public const int LoginPermitPerMinute = 5;

    /// <summary>Parola değişimi: 5 deneme / 15 dakika / IP. (Mevcut değer korundu.)</summary>
    public const int PasswordChangePermitPerWindow = 5;

    public const int PasswordChangeWindowMinutes = 15;

    /// <summary>Kurulum: 3 deneme / 10 dakika / IP. (Mevcut değer korundu.)</summary>
    public const int SetupPermitPerWindow = 3;

    public const int SetupWindowMinutes = 10;

    /// <summary>MCP: 120 çağrı / dakika / anahtar. (Mevcut değer korundu.)</summary>
    public const int McpPermitPerMinute = 120;

    /// <summary>Kişisel API anahtarı: 300 istek / dakika / anahtar. (Mevcut değer korundu.)</summary>
    public const int TokenPermitPerMinute = 300;

    /// <summary>
    /// Kimlik doğrulamalı GENEL trafik: 600 istek / dakika / oturum.
    /// </summary>
    /// <remarks>
    /// Yoklama AYRI kovada (bkz. <see cref="PolledPermitPerMinute"/>), yani bu bütçe tamamen
    /// kullanıcı eylemlerine kalır. Ölçülen en yoğun eylem dizisi tek sayfa açılışıdır
    /// (SPA + statik varlıklar + ekranın ilk yüklemeleri ≈ 40 istek); 600 bunun 15 katı.
    /// Yoklamalardan bu kovaya düşenler de var (SEO 20/dk, cron görev başına 24/dk, güncelleme
    /// 12/dk) — beşi birden aynı anda koşsa ~150/dk eder, hâlâ dörtte bir.
    /// </remarks>
    public const int SessionPermitPerMinute = 600;

    /// <summary>
    /// Yoklanan uçlar: 900 istek / dakika / oturum.
    /// </summary>
    /// <remarks>
    /// Ölçülen tek sekme en kötü hâli 71/dk (İzleme ekranı). 900, aynı anda ~12 sekme demek.
    /// Bu kova AYRI olduğu için buradaki tüketim kullanıcının tıklama bütçesini yemez.
    /// </remarks>
    public const int PolledPermitPerMinute = 900;

    /// <summary>
    /// Kimliksiz POSTA GÖNDERİMİ: 10 istek / 15 dakika / IP.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden bu kadar dar:</b> abonelikten çıkma akışı ölçüldü — meşru bir insan tek bir
    /// istek yapar (bağlantıya tıklar, adresini yazar, gönderir). Yanlış yazıp tekrar denemesi
    /// için bile 10 fazlasıyla yeter. Buna karşılık sınırsız hâli, tek satırlık bir betikle
    /// sunucunun giden kuyruğunu doldurup İTİBARINI yakmaya yetiyordu; bir sunucunun kara listeye
    /// düşmesi ÜZERİNDEKİ BÜTÜN kiracıları etkiler.
    /// </para>
    /// <para>
    /// <b>Site kurucu formunun kendi sınırı BOZULMADI:</b> <c>SiteBuilderContactRateLimiter</c>
    /// zaten IP başına 5/15dk uyguluyor. Buradaki 10, ondan GEVŞEK bir dış tavandır — yani o
    /// ucun davranışı değişmez, ama abonelikten çıkma ucu (tek koruması bu olan uç) kapanır.
    /// </para>
    /// <para>
    /// <b>Onay adımı bu politikada DEĞİL</b> (bkz. <see cref="PublicRead"/>): onay posta
    /// GÖNDERMEZ ve 256 bitlik belirteç ister. İkisini aynı kovaya koymak, isteği yenilemekle
    /// bütçesini tüketen gerçek kullanıcıyı kendi onayından dışarıda bırakırdı.
    /// </para>
    /// </remarks>
    public const int PublicMailSendPermitPerWindow = 10;

    public const int PublicMailSendWindowMinutes = 15;

    /// <summary>
    /// Kimliksiz OKUMA: 300 istek / dakika / IP.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden cömert — ÖLÇÜLEN vekil etkisi:</b> autodiscover uçları istemcilere doğrudan
    /// gelmiyor; müşterinin IIS sitesindeki URL Rewrite/ARR kuralı panele İLETİYOR
    /// (<c>MailAutodiscoverRewriteRules.cs:27</c>, <c>MailAutodiscoverPublisher.cs:76</c>).
    /// Yani bu isteklerin <c>RemoteIpAddress</c>'i 127.0.0.1'dir ve sunucudaki BÜTÜN posta
    /// istemcileri TEK bölüme düşer. Dar bir sınır, sunucudaki herkesin posta kurulumunu
    /// kırardı. 300/dk, tek bir kovadan bile onlarca istemcinin aynı anda hesap kurmasına yeter.
    /// </para>
    /// <para>
    /// Bu, <c>UseForwardedHeaders</c>'ın BİLEREK açılmamasının ölçülmüş bedelidir; gerekçe
    /// <c>Program.cs</c>'te yazılı.
    /// </para>
    /// </remarks>
    public const int PublicReadPermitPerMinute = 300;

    /// <summary>
    /// Git webhook: 30 tetikleme / dakika / IP.
    /// </summary>
    /// <remarks>
    /// Sağlayıcı push başına BİR istek gönderir; dakikada 30 push yapan bir depo yoktur. Sınırın
    /// işi, geçerli bir jetonu ele geçirenin (ya da jetonsuz gürültü yapanın) dağıtımı sürekli
    /// tetiklemesini kesmek: her tetikleme <c>git fetch</c> + <c>reset --hard</c> çalıştırır ve
    /// <c>concurrencyLock</c> yüzünden SERİdir, yani sıraya giren istekler siteyi meşgul eder.
    /// </remarks>
    public const int GitWebhookPermitPerMinute = 30;

    /// <summary>
    /// AYRI kovaya alınan (sık yoklanan) yol önekleri.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Liste KASITEN kısa ve yalnız SALT-OKUR izleme uçlarını içeriyor. "Yoklanan her ucu buraya
    /// ekle" kuralı olsaydı, listede unutulan yeni bir yoklama ucu paneli kırardı; oysa şimdi
    /// unutulan uç <see cref="SessionPermitPerMinute"/> kovasına düşer ve o kova ölçülen bütün
    /// diğer yoklamaları (SEO 20/dk, cron 24/dk×N, güncelleme 12/dk) rahatça taşıyacak
    /// genişlikte seçildi. Yani listenin bayatlaması PANELİ KIRMAZ, yalnız daha dar bir kovaya
    /// düşürür.
    /// </para>
    /// <para>
    /// Buradaki iki önek, tur başına en çok isteği üreten ve en ucuz olan uçlardır
    /// (<c>/monitoring/processes</c> 30/dk, <c>/monitoring/consumers</c> 40/dk).
    /// </para>
    /// </remarks>
    public static readonly string[] PolledPathPrefixes =
    [
        "/api/v1/monitoring",
        "/api/v1/system"
    ];

    /// <summary>Yol, ayrı yoklama kovasına mı ait?</summary>
    public static bool IsPolledPath(PathString path) =>
        PolledPathPrefixes.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Bölümleme anahtarı: ÖNCE oturum, olmazsa IP.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden çerez, neden IP DEĞİL:</b> hız sınırlayıcı ara katmanı <c>UseAuthentication</c>'dan
    /// ÖNCE koşar (429'un güvenlik başlıklarını kaybetmemesi için sıralama sabit), dolayısıyla
    /// <c>context.User</c> daha boştur. Elde kalan tek oturum tanımlayıcısı çerezin KENDİSİDİR.
    /// Yalnız IP kullanılsaydı, aynı ofisten (tek NAT adresi) çalışan on yönetici tek kovayı
    /// paylaşır ve biri diğerlerini kilitlerdi.
    /// </para>
    /// <para>
    /// <b>Çerezin değeri değil ÖZETİ:</b> bölümleme anahtarları tanılama yüzeylerinde görünebilir;
    /// oraya geçerli bir oturum çerezi taşımak, hız sınırlamak için oturum çalınabilir hâle
    /// getirmek olurdu. Aynı gerekçe <see cref="PanelAuthentication.RateLimitPartitionKey"/>'de
    /// anahtar için yazılı.
    /// </para>
    /// </remarks>
    public static string SessionPartitionKey(HttpContext context, string authCookieName)
    {
        var cookie = context.Request.Cookies[authCookieName];
        if (!string.IsNullOrEmpty(cookie))
        {
            return "session:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cookie)))[..16];
        }

        return "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    }

    /// <summary>IP tabanlı politikaların ortak bölümleme anahtarı.</summary>
    public static string IpPartitionKey(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>
    /// KÜRESEL sınırlayıcının bölümleme kararı — saf fonksiyon.
    /// </summary>
    /// <remarks>
    /// Karar <c>Configure</c>'un içine gömülü OLSAYDI, ancak tam bir HTTP boru hattı ayağa
    /// kaldırılarak sınanabilirdi; bu depoda o, pratikte "hiç sınanmadı" demektir. Testler bu
    /// fonksiyonu <c>PartitionedRateLimiter.Create</c> ile sararak ara katmanın kullandığı
    /// KODUN AYNISINI ölçer.
    /// </remarks>
    public static RateLimitPartition<string> GlobalPartition(HttpContext context, string authCookieName)
    {
        // 1) Kişisel API anahtarı taşıyan istekler: anahtar başına. Dış entegrasyon
        //    (WHMCS eşitlemesi, izleme aracı) tek turda onlarca uç çağırır.
        if (PanelAuthentication.CarriesToken(context.Request))
        {
            return RateLimitPartition.GetFixedWindowLimiter(
                "token|" + PanelAuthentication.RateLimitPartitionKey(context),
                _ => Dakikalik(TokenPermitPerMinute));
        }

        var kimlik = SessionPartitionKey(context, authCookieName);

        // 2) Sık yoklanan salt-okur izleme uçları: AYRI kova, geniş tavan.
        if (IsPolledPath(context.Request.Path))
        {
            return RateLimitPartition.GetFixedWindowLimiter(
                "poll|" + kimlik,
                _ => Dakikalik(PolledPermitPerMinute));
        }

        // 3) Geri kalan her şey (arayüz eylemleri, statik varlıklar, anonim uçlar).
        return RateLimitPartition.GetFixedWindowLimiter(
            "session|" + kimlik,
            _ => Dakikalik(SessionPermitPerMinute));
    }

    /// <summary>
    /// ADLANDIRILMIŞ POLİTİKALARIN TAMAMI — kayıt da denetim de bu tek sözlükten okur.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden sözlük, neden art arda <c>AddPolicy</c> çağrısı DEĞİL:</b> politika adı bir
    /// <c>const</c> olarak tanımlanıp kayıt satırı unutulduğunda, denetleyicideki
    /// <c>[EnableRateLimiting(PanelRateLimits.X)]</c> DERLENİR ama çalışma zamanında istisna
    /// atar — yani hata ancak canlıda görünür. Sözlük, "tanımlı politika" ile "kayıtlı politika"
    /// arasındaki farkı imkânsız kılar ve testin sayması için tek bir yer bırakır.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, Func<HttpContext, RateLimitPartition<string>>> Policies =
        new Dictionary<string, Func<HttpContext, RateLimitPartition<string>>>(StringComparer.Ordinal)
        {
            [Login] = context => RateLimitPartition.GetFixedWindowLimiter(
                IpPartitionKey(context),
                _ => Dakikalik(LoginPermitPerMinute)),

            // Parola degisimi: mevcut parolayi dogruluyoruz, yani bu uc bir parola tahmin
            // yuzeyidir. Cerez calinmis olsa bile mevcut parola deneme yanilmayla asilamasin.
            [PasswordChange] = context => RateLimitPartition.GetFixedWindowLimiter(
                IpPartitionKey(context),
                _ => Pencere(PasswordChangePermitPerWindow, TimeSpan.FromMinutes(PasswordChangeWindowMinutes))),

            [Setup] = context => RateLimitPartition.GetFixedWindowLimiter(
                IpPartitionKey(context),
                _ => Pencere(SetupPermitPerWindow, TimeSpan.FromMinutes(SetupWindowMinutes))),

            // MCP: yapay zeka istemcisi otomatik ve hızlı çağırır; anahtar sızarsa da ilk fark
            // edilen şey bu uçtan gelen yığın olur. Bölümleme ANAHTARA göre (IP'ye göre değil):
            // sağlayıcı paylaşımlı çıkış adresleri kullandığı için bir müşterinin trafiği
            // diğerini kilitlememeli.
            [Mcp] = context => RateLimitPartition.GetFixedWindowLimiter(
                PanelAuthentication.RateLimitPartitionKey(context),
                _ => Dakikalik(McpPermitPerMinute)),

            [PublicMailSend] = context => RateLimitPartition.GetFixedWindowLimiter(
                IpPartitionKey(context),
                _ => Pencere(PublicMailSendPermitPerWindow, TimeSpan.FromMinutes(PublicMailSendWindowMinutes))),

            [PublicRead] = context => RateLimitPartition.GetFixedWindowLimiter(
                IpPartitionKey(context),
                _ => Dakikalik(PublicReadPermitPerMinute)),

            [GitWebhook] = context => RateLimitPartition.GetFixedWindowLimiter(
                IpPartitionKey(context),
                _ => Dakikalik(GitWebhookPermitPerMinute))
        };

    /// <summary>
    /// Bütün sınırlayıcıyı kurar. <c>Program.cs</c>'teki TEK çağrı noktası.
    /// </summary>
    /// <param name="authCookieName">Oturum çerezinin adı (yapılandırmadan; sabit yazılamaz).</param>
    public static Action<RateLimiterOptions> Configure(string authCookieName) => options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        // 429 YANITI DA PANELİN ZARFINI KULLANIR.
        //
        // Varsayılan hâl gövdesiz bir 429'du: arayüzün hata çözücüsü (utils/apiError.ts) zarf
        // bulamayınca "HTTP 429" gibi ham bir metne düşerdi ve kullanıcı ne olduğunu anlamazdı.
        // Zarf + çeviri anahtarı ("rate_limit.exceeded") ile ekranda ne olduğu YAZILI çıkar.
        //
        // GÜVENLİK BAŞLIKLARI BURADA KAYBOLMAZ: PanelSecurityHeaders zaten bu ara katmandan
        // DIŞTA uygulanıyor (Program.cs), yani başlıklar yanıt nesnesinde hazır. Sıralama testle
        // sabitlendi (PanelRateLimitWiringTests).
        options.OnRejected = async (context, cancellationToken) =>
        {
            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            {
                context.HttpContext.Response.Headers.RetryAfter =
                    ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            }

            context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await context.HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<object?>.Fail("rate_limit.exceeded", StatusCodes.Status429TooManyRequests),
                cancellationToken);
        };

        // KÜRESEL TAVAN — üç bölüm, üç ayrı gerekçe.
        //
        // Küresel olmasının sebebi ölçüldü: 66 denetleyici var ve politikayı tek tek eklemek
        // unutulacağı için fiilen eklememekle aynı şey. Yeni bir denetleyici hiçbir şey yazmadan
        // tavanın altına girer.
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
            context => GlobalPartition(context, authCookieName));

        // Politikalar TEK sözlükten kaydedilir: "tanımlı ama kayıtsız politika" (derlenen ama
        // çalışma zamanında patlayan denetleyici) durumu yapısal olarak imkânsız olsun.
        foreach (var (ad, bolumleyici) in Policies)
        {
            options.AddPolicy(ad, bolumleyici);
        }
    };

    private static FixedWindowRateLimiterOptions Dakikalik(int izin) =>
        Pencere(izin, TimeSpan.FromMinutes(1));

    /// <summary>
    /// Ortak pencere ayarı. <c>QueueLimit = 0</c>: sınır aşıldığında istek BEKLETİLMEZ, hemen
    /// 429 alır — kuyruk, engellemeye çalıştığımız yükü sunucunun belleğinde biriktirmek olurdu.
    /// </summary>
    private static FixedWindowRateLimiterOptions Pencere(int izin, TimeSpan pencere) => new()
    {
        PermitLimit = izin,
        Window = pencere,
        QueueLimit = 0
    };
}
