using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AdaPanel.Core.Common;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
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
    internal const string SourceActorParameter = "adapanel_source_actor";
    internal const string DelegatingCredentialStampClaim = "adapanel_impersonator_cred";
    internal const string DelegatingSessionIdClaim = "adapanel_impersonator_sid";

    // A delegation request has two different identities: the accepted target and the
    // cookie actor that requested it. Carry an immutable snapshot, never HttpContext.User.
    internal sealed record DelegationAuthority(Guid UserId, string UserName, string CredentialStamp, string SessionId);
    internal sealed record SourceActor(Guid UserId, string Role, string CredentialStamp, string SessionId,
        DelegationAuthority? Delegation = null);

    internal static SourceActor CaptureSourceActor(ClaimsPrincipal principal)
    {
        var identity = principal.Identities.FirstOrDefault(candidate => candidate.IsAuthenticated &&
            candidate.AuthenticationType == CookieAuthenticationDefaults.AuthenticationScheme);
        if (identity is null || !Guid.TryParse(identity.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId) || userId == Guid.Empty ||
            identity.FindFirst(ClaimTypes.Role)?.Value is not { Length: > 0 } role ||
            identity.FindFirst(CredentialStampClaim)?.Value is not { Length: > 0 } stamp ||
            identity.FindFirst(SessionRevocationStore.SessionIdClaim)?.Value is not { Length: > 0 } sessionId)
            throw new AppException("auth.unauthorized", 401, 401);
        if (!TryReadDelegation(identity.Claims, out var delegation))
            throw new AppException("auth.unauthorized", 401, 401);
        return new(userId, role, stamp, sessionId, delegation);
    }

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
        var delegatedSession = context.Properties.Parameters.TryGetValue(SourceActorParameter, out var sourceActorValue);
        var sourceActor = sourceActorValue as SourceActor;
        // Parameters are transient, but explicitly remove our handoff fields as well. They
        // must never become ticket state or survive into a later renewal.
        context.Properties.Parameters.Remove(AcceptedIdentityParameter);
        context.Properties.Parameters.Remove(SourceSessionParameter);
        context.Properties.Parameters.Remove(SourceActorParameter);
        if (delegatedSession && (sourceActor is null || sourceActor.UserId == Guid.Empty ||
            string.IsNullOrWhiteSpace(sourceActor.Role) || string.IsNullOrWhiteSpace(sourceActor.CredentialStamp) ||
            string.IsNullOrWhiteSpace(sourceActor.SessionId)))
            throw new AppException("auth.unauthorized", 401, 401);
        var dbContext = context.HttpContext.RequestServices.GetRequiredService<AdaPanelDbContext>();
        var sourceUserId = sourceActor?.UserId ?? userId;
        var delegatingUserId = sourceActor?.Delegation?.UserId ?? userId;
        // Read both rows in one statement. In a return request the source is the
        // impersonated customer; the administrator is only the destination identity.
        var users = await dbContext.Users.AsNoTracking()
            .Where(candidate => candidate.Id == userId || candidate.Id == sourceUserId || candidate.Id == delegatingUserId).ToArrayAsync(token);
        var user = users.FirstOrDefault(candidate => candidate.Id == userId);
        if (user is null || !user.IsActive || identity.FindFirst(ClaimTypes.Role)?.Value != user.Role.ToString() ||
            !AcceptedIdentityEvidence.Matches(user, evidence))
            throw new AppException("auth.unauthorized", 401, 401);
        if (sourceActor is not null)
        {
            var source = users.FirstOrDefault(candidate => candidate.Id == sourceActor.UserId);
            if (source is null || !source.IsActive || source.Role.ToString() != sourceActor.Role ||
                ComputeStamp(source.PasswordHash) != sourceActor.CredentialStamp ||
                await context.HttpContext.RequestServices.GetRequiredService<SessionRevocationStore>()
                    .IsRevokedAsync(sourceActor.SessionId, token))
                throw new AppException("auth.unauthorized", 401, 401);
            // Returning to an administrator or renewing a delegated factor cookie must
            // not adopt a newer administrator credential merely because its target is valid.
            if (sourceActor.Delegation is { } delegation &&
                (!IsDelegatingAdministrator(users.FirstOrDefault(candidate => candidate.Id == delegation.UserId), delegation) ||
                 await context.HttpContext.RequestServices.GetRequiredService<SessionRevocationStore>()
                     .IsRevokedAsync(delegation.SessionId, token)))
                throw new AppException("auth.unauthorized", 401, 401);
        }
        if (renewingSession)
        {
            var sessionId = sourceSession as string;
            var revocations = context.HttpContext.RequestServices.GetRequiredService<SessionRevocationStore>();
            if (string.IsNullOrWhiteSpace(sessionId) || await revocations.IsRevokedAsync(sessionId, token))
                throw new AppException("auth.unauthorized", 401, 401);
        }
        token.ThrowIfCancellationRequested();

        var impersonators = identity.FindAll("impersonator_id").ToArray();
        var impersonatorNames = identity.FindAll("impersonator_name").ToArray();
        foreach (var claim in identity.FindAll(DelegatingCredentialStampClaim).Concat(identity.FindAll(DelegatingSessionIdClaim)).ToArray())
            identity.RemoveClaim(claim);
        if (impersonators.Length > 0 || impersonatorNames.Length > 0)
        {
            if (impersonators.Length != 1 || impersonatorNames.Length != 1 ||
                string.IsNullOrWhiteSpace(impersonatorNames[0].Value) ||
                !Guid.TryParse(impersonators[0].Value, out var impersonatorId) || sourceActor is null || impersonatorId == userId)
                throw new AppException("auth.unauthorized", 401, 401);
            DelegationAuthority acceptedDelegation;
            if (sourceActor.Delegation is { } existingDelegation)
            {
                // A factor change renews the customer's cookie without discarding its
                // administrator binding. It cannot create a different delegation.
                if (sourceActor.UserId != userId || existingDelegation.UserId != impersonatorId ||
                    existingDelegation.UserName != impersonatorNames[0].Value)
                    throw new AppException("auth.unauthorized", 401, 401);
                acceptedDelegation = existingDelegation;
            }
            else
            {
                if (sourceActor.UserId != impersonatorId || sourceActor.Role != nameof(UserRole.Administrator))
                    throw new AppException("auth.unauthorized", 401, 401);
                acceptedDelegation = new(impersonatorId, impersonatorNames[0].Value, sourceActor.CredentialStamp, sourceActor.SessionId);
            }
            identity.AddClaim(new(DelegatingCredentialStampClaim, acceptedDelegation.CredentialStamp));
            identity.AddClaim(new(DelegatingSessionIdClaim, acceptedDelegation.SessionId));
        }

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

        // Source logout and legacy delegation rejection cannot wait for the SQL cache.
        // Ordinary cookies still use their existing validation interval.
        if (!TryReadDelegation(principal.Claims, out var delegation) ||
            delegation is not null && await kaldirilanlar.IsRevokedAsync(delegation.SessionId, context.HttpContext.RequestAborted))
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
        var delegatingUserId = delegation?.UserId ?? userId;
        var users = await dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == userId || user.Id == delegatingUserId)
            .ToArrayAsync(context.HttpContext.RequestAborted);
        var kullanici = users.FirstOrDefault(user => user.Id == userId);

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

        if (delegation is not null && (delegation.UserId == userId ||
            !IsDelegatingAdministrator(users.FirstOrDefault(user => user.Id == delegation.UserId), delegation)))
        {
            await ReddetAsync(context);
            return;
        }

        context.Properties.Items[LastValidatedKey] = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        context.ShouldRenew = true; // damgalı/zaman damgalı çerez tarayıcıya geri yazılsın
    }

    private static bool IsDelegatingAdministrator(PanelUser? user, DelegationAuthority delegation) =>
        user is { IsActive: true, Role: UserRole.Administrator } && user.Id == delegation.UserId &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(delegation.CredentialStamp),
            Encoding.UTF8.GetBytes(ComputeStamp(user.PasswordHash)));

    private static bool TryReadDelegation(IEnumerable<Claim> claims, out DelegationAuthority? delegation)
    {
        delegation = null;
        var snapshot = claims.ToArray();
        var ids = snapshot.Where(claim => claim.Type == "impersonator_id").Take(2).ToArray();
        var names = snapshot.Where(claim => claim.Type == "impersonator_name").Take(2).ToArray();
        var stamps = snapshot.Where(claim => claim.Type == DelegatingCredentialStampClaim).Take(2).ToArray();
        var sessions = snapshot.Where(claim => claim.Type == DelegatingSessionIdClaim).Take(2).ToArray();
        if (ids.Length == 0 && names.Length == 0 && stamps.Length == 0 && sessions.Length == 0) return true;
        if (ids.Length != 1 || names.Length != 1 || stamps.Length != 1 || sessions.Length != 1 ||
            !Guid.TryParse(ids[0].Value, out var userId) || userId == Guid.Empty ||
            string.IsNullOrWhiteSpace(names[0].Value) || string.IsNullOrWhiteSpace(stamps[0].Value) ||
            string.IsNullOrWhiteSpace(sessions[0].Value)) return false;
        delegation = new(userId, names[0].Value, stamps[0].Value, sessions[0].Value);
        return true;
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
