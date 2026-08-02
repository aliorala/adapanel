using System.Security.Claims;
using System.Text.Encodings.Web;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AdaPanel.Api.Security;

/// <summary>
/// <c>Authorization: Bearer adap_…</c> başlığıyla gelen sunucudan sunucuya istekleri doğrular.
/// <para>
/// Neden çerez yerine bu: panelin tek kimlik yolu çerezdi ve tarayıcı dışı bir istemci için uygun
/// değil (12 saatlik oturum, dakikada 5 giriş denemesi sınırı, iki adımlı doğrulama açıksa otomatik
/// giriş imkânsız, parolanın saklanması gerekir). Anahtar bunların hiçbirini taşımaz ve tek tek
/// iptal edilebilir.
/// </para>
/// <para>
/// Üretilen talepler ÇEREZ ŞEMASIYLA AYNI (NameIdentifier / Name / Email / Role) — böylece mevcut
/// yetkilendirme, kapsam denetimi (CustomerAccess) ve denetim kaydı kodu hiç değişmeden çalışır.
/// Ek olarak anahtarın yetenekleri talep olarak eklenir; yıkıcı araçlar bunlara bakar.
/// </para>
/// </summary>
public sealed class PanelTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IApiTokenService apiTokenService,
    AdaPanelDbContext dbContext) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "PanelToken";

    /// <summary>Anahtar YIKICI araçlara izin veriyor mu ("true"/"false").</summary>
    public const string AllowDestructiveClaim = "adapanel_allow_destructive";

    /// <summary>Anahtar sunucuda komut çalıştırmaya izin veriyor mu ("true"/"false").</summary>
    public const string AllowTerminalClaim = "adapanel_allow_terminal";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Başlık yoksa BAŞARISIZ değil "sonuç yok" dönülür: aynı istek başka bir şemayla
        // (çerez) doğrulanabilir ve buradan hata dönmek onu engellerdi.
        if (!Request.Headers.TryGetValue("Authorization", out var header))
        {
            return AuthenticateResult.NoResult();
        }

        var raw = header.ToString();
        if (!raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var presented = raw["Bearer ".Length..].Trim();
        var token = await apiTokenService.ValidateAsync(presented, Context.RequestAborted);
        if (token is null)
        {
            // Ayrıntı VERİLMEZ: "süresi dolmuş" ile "yok" arasındaki fark saldırgana bilgi verir.
            return AuthenticateResult.Fail("apitoken.invalid");
        }

        var user = await dbContext.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == token.UserId, Context.RequestAborted);

        // Kullanıcı silinmiş ya da pasife alınmışsa anahtar da geçersizdir; aksi hâlde iptal
        // edilmiş bir hesap anahtarıyla çalışmaya devam ederdi.
        if (user is null || !user.IsActive)
        {
            return AuthenticateResult.Fail("apitoken.owner_inactive");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.UserName),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Role, user.Role.ToString()),
            new("preferred_language", user.PreferredLanguage),
            new(AllowDestructiveClaim, token.AllowDestructive ? "true" : "false"),
            // Komut çalıştırma yalnız YÖNETİCİ anahtarında anlamlı: rol yükseltmesi olmasın diye
            // burada rolle de kesiştiriliyor.
            new(AllowTerminalClaim, token.AllowTerminal && user.Role == Core.Enums.UserRole.Administrator ? "true" : "false")
        };

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }
}
