using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AdaPanel.Core.Common;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace AdaPanel.Api.Security;

/// <summary>
/// Oturum çerezini HER İSTEKTE DEĞİL ama DÜZENLİ olarak gerçekle karşılaştırır.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden gerekliydi:</b> çerez kimlik doğrulamasında <c>OnValidatePrincipal</c> HİÇ YOKTU.
/// Çerez bir kez verildikten sonra 12 saat boyunca (kalıcı seçilirse 14 gün) sorgusuz geçerliydi.
/// Sonuç: parola değiştirmek, kullanıcıyı pasife almak, rolünü düşürmek ve kullanıcıyı SİLMEK
/// açık oturumu düşürmüyordu. Yani "parolamı değiştirdim" demek, parolayı ele geçirmiş birini
/// dışarı atmıyordu — güvenlik olayına verilebilecek ilk ve en temel yanıt çalışmıyordu.
/// API anahtarı yolunda (<see cref="PanelTokenAuthenticationHandler"/>) aynı kontrol zaten vardı;
/// eksik olan yalnızca çerezdi.
/// </para>
/// <para>
/// <b>Neden "kimlik damgası", neden <c>UpdatedAtUtc</c> DEĞİL:</b> ilk akla gelen çözüm, parolanın
/// değişme zamanını çerezin <c>IssuedUtc</c>'siyle karşılaştırmaktı. Ama <c>PanelUser</c>'da
/// parola değişim zamanı diye bir alan YOK; en yakın aday <c>UpdatedAtUtc</c> ve o alan BAŞARILI
/// GİRİŞTE de, BAŞARISIZ GİRİŞTE de güncelleniyor (AuthService). Onu ölçüt yapsaydık:
/// (a) ikinci bir cihazdan giriş yapmak birinci cihazı atardı, (b) daha kötüsü, saldırganın
/// kurbanın hesabına yaptığı BAŞARISIZ bir deneme kurbanı oturumdan atardı — yani düzeltme,
/// herkesin tetikleyebildiği bir hizmet kesintisine dönüşürdü.
/// </para>
/// <para>
/// Bunun yerine ASP.NET Identity'nin "security stamp" fikri, VAR OLAN veriden türetilerek
/// uygulanıyor: girişte parola karmasının kısa bir özeti çereze yazılır, doğrulamada veritabanındaki
/// karmadan yeniden hesaplanıp karşılaştırılır. Parola her değiştiğinde karma değişir, dolayısıyla
/// eski çerez kendiliğinden geçersizleşir. EF göçü, yeni sütun ve yazma yollarına dokunma gerekmez —
/// yani düzeltme, parolayı değiştiren BÜTÜN yolları (kendi değiştirme, yönetici sıfırlama, kurulum)
/// tek noktadan kapsar ve ileride eklenecek yeni bir yol da otomatik kapsanır.
/// </para>
/// <para>
/// <b>ÇIKIŞ da burada uygulanır.</b> Çerez kendi kendine yeterli olduğu için
/// <c>SignOutAsync</c> yalnız TARAYICIDAKİ kopyayı siler; çerezin kendisi geçerli kalırdı. Girişte
/// üretilen oturum kimliği (<see cref="SessionRevocationStore.SessionIdClaim"/>) çıkışta kara
/// listeye yazılır ve burada HER İSTEKTE — 60 saniyelik tazelik kısayoluna takılmadan — sorulur.
/// Gerekçenin tamamı <see cref="SessionRevocationStore"/>'da.
/// </para>
/// <para>
/// <b>Damgası olmayan çerez REDDEDİLİR.</b> Bu sürüme geçildiğinde açık oturumlar bir kez düşer ve
/// kullanıcılar yeniden giriş yapar. Bilinçli: "damga yoksa geçerli say" demek, tam da kapatmaya
/// çalıştığımız durumu (eski çerez sorgusuz geçer) kalıcılaştırırdı.
/// </para>
/// <para>
/// <b>Neden her istekte değil:</b> doğrulama bir veritabanı okuması yapar ve panelin arayüzü tek bir
/// sayfa açılışında onlarca istek atar. Aralık <see cref="ValidationInterval"/> = 60 saniye seçildi:
/// ASP.NET Identity'nin 30 dakikalık varsayılanından çok daha sıkı (bir hesabı kapatmanın en geç bir
/// dakikada etkili olması bir hosting panelinde makul üst sınırdır), ama sayfa başına tek okuma
/// düşecek kadar da geniş. Zaman damgası çerezin kendi <c>AuthenticationProperties</c>'inde tutulur;
/// sunucuda durum tutulmaz, dolayısıyla yeniden başlatma da bu kararı bozmaz.
/// </para>
/// </remarks>
public static class CookieSessionValidator
{
    /// <summary>Çereze yazılan kimlik damgası (parola karmasının kısaltılmış özeti).</summary>
    public const string CredentialStampClaim = "adapanel_cred";

    internal const string AcceptedIdentityParameter = "adapanel_accepted_identity";
    internal const string SourceSessionParameter = "adapanel_source_session";

    /// <summary>Son doğrulama zamanı — çerezin kendi özelliklerinde taşınır.</summary>
    private const string LastValidatedKey = "adapanel_validated_utc";

    /// <summary>İki doğrulama arasındaki en uzun süre. Gerekçesi sınıf açıklamasında.</summary>
    public static readonly TimeSpan ValidationInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Girişte kimlik damgasını çereze ekler. Burada olması şart: <c>SignInAsync</c> çağrıları
    /// AuthController'da (giriş, iki adımlı doğrulama, kimliğe bürünme ve geri dönüş) beş ayrı
    /// yerde; olay üzerinden bağlanınca hepsi tek noktadan kapsanır ve yeni bir giriş yolu
    /// eklendiğinde damgayı eklemeyi unutmak mümkün olmaz.
    /// </summary>
    public static async Task OnSigningInAsync(CookieSigningInContext context)
    {
        var identity = context.Principal?.Identities.FirstOrDefault(item => item.IsAuthenticated);
        if (identity is null || !Guid.TryParse(identity.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
        {
            throw new AppException("auth.unauthorized", 401, 401);
        }

        var token = context.HttpContext.RequestAborted;
        token.ThrowIfCancellationRequested();
        context.Properties.Parameters.TryGetValue(AcceptedIdentityParameter, out var acceptedIdentity);
        var evidence = acceptedIdentity as string;
        var renewingSession = context.Properties.Parameters.TryGetValue(SourceSessionParameter, out var sourceSession);
        // Parameters are transient, but explicitly remove our handoff fields as well. They
        // must never become ticket state or survive into a later renewal.
        context.Properties.Parameters.Remove(AcceptedIdentityParameter);
        context.Properties.Parameters.Remove(SourceSessionParameter);
        var dbContext = context.HttpContext.RequestServices.GetRequiredService<AdaPanelDbContext>();
        var user = await dbContext.Users.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.Id == userId, token);
        if (user is null || !user.IsActive || identity.FindFirst(ClaimTypes.Role)?.Value != user.Role.ToString() ||
            !AcceptedIdentityEvidence.Matches(user, evidence))
            throw new AppException("auth.unauthorized", 401, 401);
        if (renewingSession)
        {
            var sessionId = sourceSession as string;
            var revocations = context.HttpContext.RequestServices.GetRequiredService<SessionRevocationStore>();
            if (string.IsNullOrWhiteSpace(sessionId) || await revocations.IsRevokedAsync(sessionId, token))
                throw new AppException("auth.unauthorized", 401, 401);
        }
        token.ThrowIfCancellationRequested();

        // OTURUM KİMLİĞİ HER GİRİŞTE YENİDEN ÜRETİLİR.
        //
        // İki işi birden yapar: (1) çıkışın sunucu tarafında da bir karşılığı olmasını sağlar —
        // kara listeye yazılacak bir tutamak olmadan "çıkış" yalnız tarayıcıdaki çerezi silmekti,
        // (2) oturum sabitlemeyi ÖLÇÜLEBİLİR biçimde imkânsız kılar: girişten sonraki oturum,
        // girişten öncekiyle aynı kimliği taşıyamaz. Varsa eski kimlik ÖNCE atılır; ikinci bir
        // talep eklemek, hangisinin geçerli olduğunu belirsiz bırakırdı.
        foreach (var eski in identity.FindAll(SessionRevocationStore.SessionIdClaim).ToList())
        {
            identity.RemoveClaim(eski);
        }

        identity.AddClaim(new Claim(SessionRevocationStore.SessionIdClaim, SessionRevocationStore.NewSessionId()));

        foreach (var previous in identity.FindAll(CredentialStampClaim).ToList()) identity.RemoveClaim(previous);
        identity.AddClaim(new Claim(CredentialStampClaim, ComputeStamp(user.PasswordHash)));
    }

    /// <summary>Çerezin hâlâ gerçeğe karşılık gelip gelmediğini denetler.</summary>
    public static async Task OnValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return;
        }

        // ÇIKIŞ DENETİMİ HER İSTEKTE, ARALIK BEKLEMEDEN.
        //
        // Aşağıdaki 60 saniyelik tazelik kısayolunun ÜSTÜNDE durması zorunlu: çıkışın etkisi
        // gecikmeli olamaz. "Çıkış yaptım" diyen kullanıcının çerezi bir dakika daha çalışıyorsa
        // çıkış, tam da anlamlı olduğu durumda (paylaşılan bilgisayardan kalkmak, çalınmış bir
        // çerezi düşürmek) işe yaramaz. Denetim BELLEKTEN yapılır — veritabanı/disk okuması yok —
        // dolayısıyla her isteğe uygulanmasının ölçülebilir bir bedeli yoktur.
        //
        // KİMLİĞİ OLMAYAN ÇEREZ REDDEDİLİR: kimlik damgasındaki kuralın aynısı. "Kimlik yoksa
        // geçerli say" demek, çıkışı sunucu tarafında hiç uygulayamamak demekti. Bedeli, bu sürüme
        // geçildiğinde açık oturumların bir kez düşmesidir.
        var oturumKimligi = principal.FindFirst(SessionRevocationStore.SessionIdClaim)?.Value;
        var kaldirilanlar = context.HttpContext.RequestServices.GetRequiredService<SessionRevocationStore>();
        if (string.IsNullOrEmpty(oturumKimligi) ||
            await kaldirilanlar.IsRevokedAsync(oturumKimligi, context.HttpContext.RequestAborted))
        {
            await ReddetAsync(context);
            return;
        }

        if (SonDogrulamaTaze(context.Properties))
        {
            return;
        }

        if (!Guid.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
        {
            await ReddetAsync(context);
            return;
        }

        var dbContext = context.HttpContext.RequestServices.GetRequiredService<AdaPanelDbContext>();
        var kullanici = await dbContext.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(user => user.Id == userId, context.HttpContext.RequestAborted);

        // 1) Kullanıcı silinmiş ya da pasife alınmış mı.
        if (kullanici is null || !kullanici.IsActive)
        {
            await ReddetAsync(context);
            return;
        }

        // 2) Rol çerezdekiyle aynı mı. Rol yetkilendirmenin tamamıdır: bir yöneticiyi Customer'a
        //    düşürmek, o kişi çıkış yapana kadar hiçbir şey ifade etmiyordu.
        if (!string.Equals(principal.FindFirst(ClaimTypes.Role)?.Value, kullanici.Role.ToString(), StringComparison.Ordinal))
        {
            await ReddetAsync(context);
            return;
        }

        // 3) Parola çerez verildikten sonra değişmiş mi (kimlik damgası).
        var damga = principal.FindFirst(CredentialStampClaim)?.Value;
        if (string.IsNullOrEmpty(damga) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(damga),
                Encoding.UTF8.GetBytes(ComputeStamp(kullanici.PasswordHash))))
        {
            await ReddetAsync(context);
            return;
        }

        context.Properties.Items[LastValidatedKey] = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        context.ShouldRenew = true; // damgalı/zaman damgalı çerez tarayıcıya geri yazılsın
    }

    private static bool SonDogrulamaTaze(AuthenticationProperties properties)
    {
        return properties.Items.TryGetValue(LastValidatedKey, out var ham) &&
               DateTimeOffset.TryParse(ham, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var son) &&
               DateTimeOffset.UtcNow - son < ValidationInterval;
    }

    private static async Task ReddetAsync(CookieValidatePrincipalContext context)
    {
        // İkisi birlikte olmalı: RejectPrincipal isteği kimliksiz bırakır, SignOutAsync ise ÇEREZİ
        // tarayıcıdan siler. Yalnız birincisi yapılsaydı istemci her istekte ölü bir çerez gönderip
        // 401 alır ve neden çıkış yapamadığını anlamazdı.
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// Parola karmasının kimlik damgası. Karmanın KENDİSİ çereze yazılmaz: çerez şifreli olsa da
    /// doğrulayıcı bir değeri istemciye taşımak gereksiz bir risktir.
    /// </summary>
    internal static string ComputeStamp(string passwordHash) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(passwordHash ?? string.Empty)))[..16];
}
