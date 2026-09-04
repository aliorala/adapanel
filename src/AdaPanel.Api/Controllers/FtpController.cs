using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Ftp;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

// Reseller (Bayi) yalnız kendi müşterilerinin sitelerine FTP kullanıcısı yönetebilir. FTP sunucusu
// KURULUMU (ensure) server-level → admin-only. Liste, etkin sitelere göre filtrelenir; parola/silme
// işleminde kullanıcının ait olduğu site çözülüp kapsam doğrulanır.
//
// DENETİM KAYDI: buradaki uçlar GERÇEK BİR WINDOWS YEREL HESABI açar/siler/parolasını değiştirir ve
// bir klasör ağacına yazma izni verir. Yani panel dışında da sonucu olan işlemlerdir; iz bırakmazsa
// sunucuda "bu hesabı kim açtı" sorusunun cevabı Windows tarafında da yoktur. Parola DEĞERİ asla
// yazılmaz — yalnızca "değiştirildi" olgusu.
[ApiController]
[Authorize(Roles = "Administrator,Reseller")]
[Route("api/v1/ftp")]
public sealed class FtpController(
    IFtpService ftpService,
    ICustomerScopeService customerScopes,
    IIisSiteService iisSiteService,
    IAuditService auditService) : ControllerBase
{
    [HttpPost("ensure")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<FtpServerStatusDto>>> EnsureServerAsync(CancellationToken cancellationToken)
    {
        var status = await ftpService.EnsureServerAsync(cancellationToken);
        await auditService.AuditAsync(HttpContext, "ftp.server.ensured", "ftp-server", "FTP server installation/configuration ensured.");

        // MESAJ ÖLÇÜLEN DURUMDAN GELİR. Burada sabit "ftp.server_ready" yazıyordu: kurulum betiği
        // patlamadığı sürece panel, hiç kimsenin giriş yapamadığı bir sunucu için de "hazır" diyordu
        // (bkz. FtpServerStatusDto.IsConfigured — 19.08.2026 ölçümü).
        return Ok(ApiResponse<FtpServerStatusDto>.Ok(status, status.StatusMessage));
    }

    /// <summary>
    /// FTP'nin GERÇEK durumunu ÖLÇER (kurmaz, değiştirmez).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Durumu öğrenmenin tek yolu <c>POST /ftp/ensure</c>'dü: IIS sitesini kuran, FTPSVC'yi yeniden
    /// başlatan ve yalnız yöneticiye açık bir MUTASYON. "Neden kimse bağlanamıyor" sorusunun bedeli
    /// servisi yeniden başlatmak olmamalı.
    /// </para>
    /// <para>
    /// <b>Yönetici-özel</b>, çünkü yanıt sunucudaki YABANCI süreçlerin adlarını ve PID'lerini
    /// içerir (<c>controlPortOwner</c>) — bayiye sunucu envanteri sızdırmak bu ekranın işi değil.
    /// <c>POST /ftp/ensure</c> de aynı sebeple zaten admin-only.
    /// </para>
    /// </remarks>
    [HttpGet("status")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<FtpServerStatusDto>>> GetStatusAsync(CancellationToken cancellationToken)
    {
        var status = await ftpService.GetServerStatusAsync(cancellationToken);
        return Ok(ApiResponse<FtpServerStatusDto>.Ok(status, status.StatusMessage));
    }

    // M5 (panel "yalnız güvenli FTP"): FTPS zorunluluğu server-level → admin-only.
    [HttpGet("tls")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<FtpTlsStatusDto>>> GetTlsAsync(CancellationToken cancellationToken)
    {
        var requireSsl = await ftpService.GetRequireSslAsync(cancellationToken);
        return Ok(ApiResponse<FtpTlsStatusDto>.Ok(new FtpTlsStatusDto { RequireSsl = requireSsl }));
    }

    [HttpPut("tls")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<FtpTlsStatusDto>>> SetTlsAsync([FromBody] FtpTlsRequest request, CancellationToken cancellationToken)
    {
        // ÖLÇÜLEN sonuç. Eskiden burası servisin döndürdüğü ISTENEN bayraktan besleniyordu ve
        // kullanıcıya "FTPS artık zorunlu; düz FTP bağlantıları reddedilecek" deniyordu —
        // IIS'e hiç bakmadan. Gerekçe: FtpTlsMeasurement.
        var olcum = await ftpService.SetRequireSslAsync(request.RequireSsl, cancellationToken);
        var requireSsl = request.RequireSsl;

        // FTPS zorunluluğunun KAPATILMASI, bütün müşterilerin parolasını düz metne indirir —
        // sunucu genelinde güvenlik seviyesini düşüren bir karardır ve kim aldığı bilinmelidir.
        //
        // Kayıt artık İSTENENİ değil ÖLÇÜLENİ yazar. "set to True" satırı, politika hiç
        // uygulanmamışken de yazılıyordu; denetim kaydını okuyan kişi sunucunun düz FTP'yi
        // reddettiğini sanırdı.
        var durum = olcum.Verified switch
        {
            true => "enforced (verified)",
            false => "NOT APPLIED (measured)",
            _ => "unverified (could not be measured)"
        };

        await auditService.AuditAsync(
            HttpContext,
            "ftp.tls.updated",
            "ftp-server",
            $"FTPS requirement set to {requireSsl}: {durum}. {olcum.Detail}".TrimEnd());

        var status = new FtpTlsStatusDto
        {
            RequireSsl = requireSsl,
            Verified = olcum.Verified,
            Detail = olcum.Detail
        };

        // Üç ayrı cümle, üç ayrı gerçek. Sabit yeşil "artık zorunlu" yalnız ÖLÇÜLDÜĞÜNDE çıkar.
        var mesaj = olcum.Verified switch
        {
            true => requireSsl ? "ftp.tls_required" : "ftp.tls_optional",
            false => "ftp.tls_not_applied",
            _ => "ftp.tls_unverified"
        };

        return Ok(ApiResponse<FtpTlsStatusDto>.Ok(status, mesaj));
    }

    [HttpGet("users")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<FtpUserDto>>>> GetUsersAsync(CancellationToken cancellationToken)
    {
        var users = await ftpService.GetFtpUsersAsync(cancellationToken);

        // Bayi/müşteri yalnız etkin sitelerinin FTP kullanıcılarını görür.
        var allowed = await CustomerAccess.GetEffectiveSitesAsync(customerScopes, User, cancellationToken);
        if (allowed is not null)
        {
            users = users
                .Where(item => allowed.Contains(item.SiteName, StringComparer.OrdinalIgnoreCase))
                .ToArray();
        }

        return Ok(ApiResponse<IReadOnlyCollection<FtpUserDto>>.Ok(users));
    }

    [HttpPost("users")]
    public async Task<ActionResult<ApiResponse<FtpUserDto>>> CreateUserAsync([FromBody] CreateFtpUserRequest request, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, request.SiteName, cancellationToken);

        // GÜVENLİK (çapraz-kiracı): admin DIŞINDAKİ kullanıcılar ana dizini kendi sitelerinin
        // klasörünün dışına gösteremez.
        //
        // Bu alan boş bırakılabilir ve o zaman sitenin kökü kullanılır (olağan durum). Ama
        // doldurulduğunda hiç doğrulanmıyordu: verilen yola FTP hesabı için DEĞİŞTİRME izni
        // uygulanıyor (icacls ... /T). Yani "C:\" ya da başka bir müşterinin klasörü yazan bir
        // kiracı, o ağacın tamamına yazma hakkı olan bir FTP hesabı elde ederdi.
        if (!User.IsInRole("Administrator") && !string.IsNullOrWhiteSpace(request.HomePath))
        {
            var site = await iisSiteService.GetSiteAsync(request.SiteName, cancellationToken);
            var kok = string.IsNullOrWhiteSpace(site.ApplicationRootPath)
                ? site.PhysicalPath
                : site.ApplicationRootPath;

            // Kökün kendisi geçerlidir: en olağan seçim sitenin ta kendisidir.
            if (!PathContainment.IsRootOrInside(kok, request.HomePath))
            {
                return StatusCode(403, ApiResponse<FtpUserDto>.Fail("ftp.home_outside_site", 403));
            }
        }

        var user = await ftpService.CreateFtpUserAsync(request, cancellationToken);

        // PAROLA YAZILMAZ: request.Password kayda hiç girmez. Yazılan şey hesabın kimliği,
        // hangi siteye bağlandığı ve hangi ana dizine yazma hakkı aldığıdır — bir olaydan sonra
        // "bu hesap neye erişebiliyordu" sorusunu cevaplayan alanlar bunlar.
        // KISMİ BAŞARI ADIYLA YAZILIR. Hesap gerçekten açıldı; eksik olan hesap süre dolumunun
        // kapatılmasıdır ve bu eksiklik SESSİZDİR: makinenin yaş sınırı işlerse FTP kırk iki gün
        // sonra kendiliğinden ölür, müşteri "dün çalışıyordu" der.
        //
        // NOT (nöbetçi): bu ifade denetim çağrısının İÇİNDE değil, ÖNCESİNDE hesaplanır.
        // "Denetim_cagrilarina_PAROLA_gecirilmez" nöbetçisi çağrı argümanlarındaki metin DIŞI kodu
        // tarar ve alan adında geçen sözcüğü sır sanır. Nöbetçiyi gevşetmek yerine ifade dışarı
        // alındı: koruma aynı sıkılıkta kalsın.
        var sureNotu = user.PasswordNeverExpires switch
        {
            true => string.Empty,
            false => " WARNING: account expiry could NOT be disabled (measured); the account may stop working after the machine's credential age limit.",
            _ => " NOTE: account expiry state could not be measured."
        };

        await auditService.AuditAsync(
            HttpContext,
            "ftp.user.created",
            user.UserName,
            $"FTP account created for site '{user.SiteName}' with home '{user.HomePath}'." + sureNotu,
            await SiteSahibiAsync(user.SiteName, cancellationToken));

        // Her şey yolundaysa cümle SADE kalır — kullanıcı gereksiz uyarıya boğulmaz.
        var mesaj = user.PasswordNeverExpires switch
        {
            true => "ftp.user_created",
            false => "ftp.user_created_password_expires",
            _ => "ftp.user_created_expiry_unverified"
        };

        return Ok(ApiResponse<FtpUserDto>.Ok(user, mesaj));
    }

    [HttpPost("users/password")]
    public async Task<ActionResult<ApiResponse<object>>> SetPasswordAsync([FromBody] SetFtpPasswordRequest request, CancellationToken cancellationToken)
    {
        await EnsureFtpUserScopeAsync(request.UserName, cancellationToken);
        await ftpService.SetFtpPasswordAsync(request, cancellationToken);

        // Yalnız OLGU yazılır ("değiştirildi"), değer değil.
        await auditService.AuditAsync(
            HttpContext,
            "ftp.user.password_changed",
            request.UserName,
            "FTP account password was changed.",
            await FtpKullanicisininSahibiAsync(request.UserName, cancellationToken));

        return Ok(ApiResponse<object>.Ok(new { request.UserName }, "ftp.password_changed"));
    }

    [HttpDelete("users/{userName}")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteUserAsync(string userName, CancellationToken cancellationToken)
    {
        await EnsureFtpUserScopeAsync(userName, cancellationToken);

        // SAHİP SİLMEDEN ÖNCE ÇÖZÜLÜR: hesap kaldırıldıktan sonra hangi siteye ait olduğu artık
        // hiçbir yerden okunamaz; kaydı sonradan kiracıya bağlamak İMKÂNSIZ olurdu.
        var sahip = await FtpKullanicisininSahibiAsync(userName, cancellationToken);

        await ftpService.DeleteFtpUserAsync(userName, cancellationToken);
        await auditService.AuditAsync(HttpContext, "ftp.user.deleted", userName, "FTP account was removed.", sahip);
        return Ok(ApiResponse<object>.Ok(new { userName }, "ftp.user_deleted"));
    }

    /// <summary>
    /// Sitenin ABONELİK SAHİBİ — kaydın kiracıya görünmesini sağlayan alan.
    /// </summary>
    /// <remarks>
    /// Kapsam denetimi ÇAĞIRANIN siteye erişebildiğini doğrular; buradaki soru başka: kaynak KİME
    /// ait. İkisi ayrıldığı an (yönetici/bayi kiracının sitesine dokunur) tam da kaydın en çok
    /// gerektiği andır. Bu alan olmadan kiracı, kendi sitesinde yapılan işlemi denetim ekranında
    /// HİÇ göremezdi. Site sahipsizse (yöneticinin kendi sitesi) boş kalır; tahmin edilmez.
    /// </remarks>
    private Task<Guid?> SiteSahibiAsync(string? siteName, CancellationToken cancellationToken)
        => AuditSubject.SiteOwnerAsync(customerScopes, siteName, cancellationToken);

    /// <summary>
    /// FTP hesabının bağlı olduğu sitenin abonelik sahibi.
    /// </summary>
    /// <remarks>
    /// <b>Neden <see cref="EnsureFtpUserScopeAsync"/> ile aynı çözüm ama ayrı metot:</b> o metot
    /// yalnız KAPSAMLI rollerde çalışır (yöneticide erken döner) çünkü işi yetkilendirmedir.
    /// Buradaki soru yetkiyle ilgili değil: kaydın konusu kim. En çok yönetici işlemlerinde
    /// gerekir — tam da ötekinin çalışmadığı yerde.
    /// </remarks>
    private async Task<Guid?> FtpKullanicisininSahibiAsync(string userName, CancellationToken cancellationToken)
    {
        try
        {
            var users = await ftpService.GetFtpUsersAsync(cancellationToken);
            var siteName = users.FirstOrDefault(item =>
                string.Equals(item.UserName, userName, StringComparison.OrdinalIgnoreCase))?.SiteName;
            return await SiteSahibiAsync(siteName, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    // Bayi/müşteri için: FTP kullanıcısının ait olduğu siteyi çözüp kapsamı doğrula. Kullanıcı
    // bulunamazsa (ya da başka sitenin kullanıcısıysa) EnsureSiteAsync 403 atar. Admin'de no-op.
    private async Task EnsureFtpUserScopeAsync(string userName, CancellationToken cancellationToken)
    {
        if (!CustomerAccess.IsScoped(User))
        {
            return;
        }

        var users = await ftpService.GetFtpUsersAsync(cancellationToken);
        var siteName = users
            .FirstOrDefault(item => string.Equals(item.UserName, userName, StringComparison.OrdinalIgnoreCase))?.SiteName
            ?? string.Empty;
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
    }
}

/// <summary>FTPS zorunluluğu toggle isteği (panel "yalnız güvenli FTP").</summary>
public sealed class FtpTlsRequest
{
    public bool RequireSsl { get; set; }
}

/// <summary>FTPS zorunluluğu durumu.</summary>
public sealed class FtpTlsStatusDto
{
    public bool RequireSsl { get; set; }

    /// <summary>
    /// Politika IIS'te GERÇEKTEN yürürlükte mi. <c>true</c> ölçüldü ve yürürlükte, <c>false</c>
    /// ölçüldü ve UYGULANMADI, <c>null</c> ÖLÇÜLEMEDİ.
    /// <para>
    /// Okuma ucunda (<c>GET /ftp/tls</c>) her zaman <c>null</c>'dır: orası yalnız panelin kendi
    /// kaydını okur, IIS'e hiç bakmaz. "Ne istendi" ile "ne yürürlükte" ayrı sorulardır.
    /// </para>
    /// </summary>
    public bool? Verified { get; set; }

    /// <summary>Ölçümün ham ayrıntısı (hangi ayar hangi değerde okundu).</summary>
    public string Detail { get; set; } = string.Empty;
}
