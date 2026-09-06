using AdaPanel.Core.Entities;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Services;

/// <summary>
/// Parola sıfırlama postasının TAMAMI: adresi arama, belirteç üretme, gönderme, kayda geçme.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden AuthService'in içinde değil, burada:</b> bu işin tamamı yanıt yolundan ÇIKARILDI
/// (bkz. <see cref="IArkaPlanIsKuyrugu"/> — ölçülen zamanlama sızıntısı). Arka planda çalışan kod
/// isteğin DI kapsamını KULLANAMAZ (kapsam kapanır, <c>DbContext</c> atılır), bu yüzden
/// bağımlılıklarını taze bir kapsamdan alır. Bağımlılıklar açık parametre olarak da verilebiliyor;
/// böylece iş, bir DI kabı kurmadan doğrudan test edilebiliyor.
/// </para>
/// <para>
/// <b>Adres arama da arka planda:</b> "kullanıcı var mı" sorusunu yanıt yolunda sormak, sorunun
/// kendisini geri getirirdi (var olan adreste iş kuyruklanır, olmayanda kuyruklanmaz → yine iki
/// farklı yol). Yanıt yolu artık adresi HİÇ bilmiyor: her adres için tek ve aynı iş kuyruklanır.
/// </para>
/// <para>
/// <b>FAZLA KAPATMA NÖBETÇİSİ:</b> postayı arka plana almak, gönderim hatasını görünmez kılma
/// riskini getirir. Bu yüzden üç sonuç da AYRI denetim kaydına yazılır: gönderildi
/// (<c>auth.password_reset.requested</c>), SMTP ayarsız ya da gönderim patladı
/// (<c>auth.password_reset.mail_failed</c>). Panel "istediniz" der ama posta çıkmazsa yönetici
/// bunu Etkinlik ekranında GÖRÜR.
/// </para>
/// </remarks>
public static class ParolaSifirlamaPostasi
{
    /// <summary>Belirteç deposu — <c>AuthService.ResetPasswordAsync</c> aynı yolu tüketir.</summary>
    public const string BelirtecDepoYolu = "config/password-resets.json";

    /// <summary>Bağlantının ömrü. E-posta metnindeki "1 saat" ifadesi bu değerden gelir.</summary>
    public static readonly TimeSpan BelirtecOmru = TimeSpan.FromHours(1);

    /// <summary>Kuyruğa verilen iş: bağımlılıklar TAZE kapsamdan çözülür.</summary>
    public static Task GonderAsync(IServiceProvider saglayici, string normalizedEmail, CancellationToken cancellationToken) =>
        GonderAsync(
            saglayici.GetRequiredService<AdaPanelDbContext>(),
            saglayici.GetRequiredService<INotificationService>(),
            saglayici.GetRequiredService<IPanelPreferencesService>(),
            saglayici.GetRequiredService<IPanelSecurityService>(),
            saglayici.GetRequiredService<IAuditService>(),
            saglayici.GetRequiredService<JsonFileStore>(),
            saglayici.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ParolaSifirlamaPostasi)),
            normalizedEmail,
            cancellationToken);

    /// <summary>İşin kendisi. Bağımlılıklar açık: test bir DI kabı kurmadan çağırabilir.</summary>
    public static async Task GonderAsync(
        AdaPanelDbContext dbContext,
        INotificationService notificationService,
        IPanelPreferencesService preferencesService,
        IPanelSecurityService securityService,
        IAuditService auditService,
        JsonFileStore fileStore,
        ILogger logger,
        string normalizedEmail,
        CancellationToken cancellationToken)
    {
        var aranan = (normalizedEmail ?? string.Empty).Trim().ToLowerInvariant();
        if (aranan.Length == 0)
        {
            return;
        }

        var user = await dbContext.Users.FirstOrDefaultAsync(
            candidate => candidate.Email.ToLower() == aranan, cancellationToken);

        // Adres panelde yok ya da hesap pasif: HİÇBİR ŞEY yapılmaz ve HİÇBİR ŞEY kaydedilmez.
        // (Kaydetmek, denetim kaydını "hangi adresler denendi" listesine çevirirdi.) Bu karar artık
        // yanıt süresine yansımıyor: karar arka planda veriliyor.
        if (user is null || !user.IsActive)
        {
            return;
        }

        if (!await notificationService.IsEmailConfiguredAsync(cancellationToken))
        {
            // SMTP yoksa link gönderilemez; belirteç üretmek de anlamsız (kullanıcıya ulaşmayacak
            // bir bağlantı üretip öncekini geçersiz kılardı).
            logger.LogWarning(
                "Parola sifirlama postasi GONDERILEMEDI: panelde SMTP yapilandirilmamis ({Email}).",
                user.Email);
            await KaydetAsync(
                auditService,
                user,
                $"Password reset email for '{user.UserName}' was NOT sent: SMTP is not configured in the panel.");
            return;
        }

        var (token, _) = await SingleUseTokenStore.IssueAsync(
            fileStore, BelirtecDepoYolu, user.Id, BelirtecOmru, cancellationToken);

        try
        {
            var preferences = await preferencesService.GetAsync(cancellationToken);
            var securePath = await securityService.GetSecureEntryPathAsync(cancellationToken);
            var panelName = string.IsNullOrWhiteSpace(preferences.PanelName) ? "AdaPanel" : preferences.PanelName;
            // Adres kurgusu TEK yerde (PanelAddress): aynı üç satır hoş geldin e-postasında da vardı.
            var resetUrl = PanelAddress.BuildEntryUrl(preferences.ServerAddress, preferences.PreferredHttpsPort, securePath)
                + $"?resetToken={Uri.EscapeDataString(token)}";
            var isTurkish = user.PreferredLanguage.StartsWith("tr", StringComparison.OrdinalIgnoreCase);

            var subject = isTurkish ? $"{panelName} parola sıfırlama" : $"{panelName} password reset";
            var body = isTurkish
                ? $"""
                  Merhaba {user.UserName},

                  Parolanızı sıfırlamak için aşağıdaki bağlantıyı açın (1 saat geçerlidir):
                  {resetUrl}

                  Bu talebi siz yapmadıysanız bu e-postayı yok sayabilirsiniz; parolanız değişmez.
                  """
                : $"""
                  Hello {user.UserName},

                  Open the link below to reset your password (valid for 1 hour):
                  {resetUrl}

                  If you did not request this, you can ignore this email; your password stays unchanged.
                  """;

            await notificationService.SendEmailToAsync(user.Email, subject, body, cancellationToken);
        }
        catch (Exception exception)
        {
            // GÖNDERİM PATLADI. Eskiden burada yalnız bir LogWarning vardı ve panel hiçbir şey
            // söylemiyordu; "sıfırlama postası hiç gelmiyor" arızası ancak kullanıcı şikâyet
            // edince ortaya çıkıyordu. Artık denetim kaydına da düşer.
            logger.LogError(exception, "Parola sifirlama postasi gonderilemedi ({Email}).", user.Email);
            await KaydetAsync(
                auditService,
                user,
                $"Password reset email could not be delivered to '{user.UserName}': {exception.Message}");
            return;
        }

        await auditService.WriteAsync(
            "auth.password_reset.requested",
            "auth",
            $"Password reset requested for '{user.UserName}'.",
            user.Id,
            null,
            cancellationToken);
    }

    /// <summary>
    /// Başarısızlık kaydı. İptal belirteci KULLANILMAZ: panel kapanırken ya da iş zaman aşımına
    /// uğrarken tam da bu kaydın düşmesi gerekir; iptal edilmiş bir belirteçle yazmaya çalışmak
    /// arızayı görünmez kılardı.
    /// </summary>
    private static Task KaydetAsync(IAuditService auditService, PanelUser user, string details) =>
        auditService.WriteAsync("auth.password_reset.mail_failed", "auth", details, user.Id, null, CancellationToken.None);
}
