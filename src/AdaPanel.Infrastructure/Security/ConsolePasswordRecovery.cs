using AdaPanel.Core.Common;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Audit;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AdaPanel.Infrastructure.Security;

/// <summary>
/// SUNUCU KONSOLUNDAN yönetici parolası belirleme (Plesk'teki <c>plesk bin admin --set-password</c>
/// karşılığı).
/// </summary>
/// <remarks>
/// <para>
/// <b>Ölçülen olay — panele giremeyen yöneticinin hiçbir çıkış yolu yoktu.</b> Var olan
/// <c>config\recover-access.flag</c> yalnız giriş kilitlerini, IP engellerini ve panelin kendi
/// engelleme kurallarını temizler; PAROLAYI SIFIRLAMAZ. Parolayı unutan kullanıcının eline kalan
/// tek çare <c>data\adapanel.db</c> dosyasını SİLMEKTİ — yani panel kullanıcıları, ayarları ve
/// kurulu bileşen kayıtları da gitti. Bir barındırma panelinde bu kabul edilemez.
/// </para>
/// <para>
/// <b>Neden HTTP ucu YOK.</b> Bu kapı, panele giremeyen kişinin sunucuda fiziksel/RDP erişimi
/// olduğunu varsayar. Ağ üzerinden çağrılabilen bir uç olsaydı kimlik doğrulamayı tamamen atlayan
/// bir arka kapı olurdu. Erişim şartı "sunucuda oturum açmış olmak"tır ve bu şart teknik olarak
/// zaten kendini uygular: veri kökü yalnız yöneticilere açıktır (K1 sertleştirmesi), veritabanını
/// açabilen kişi zaten <c>adapanel.db</c>'yi silebilir durumdadır. Yani bu yol YENİ bir yetki
/// vermez, var olan yetkiyi VERİ KAYBETMEDEN kullanılabilir hâle getirir.
/// </para>
/// <para>
/// <b>Yalnız YÖNETİCİ rolü.</b> Araç, sunucuda oturum açmış kişiye müşteri hesaplarını devralma
/// imkânı vermek için değil, panelin sahibinin kendi hesabına dönmesi için var. Müşteri/bayi
/// parolası panelden (yönetici olarak) belirlenir; oradaki kapsam denetimi bu yolla atlanamamalı.
/// </para>
/// <para>
/// <b>İki adımlı doğrulama VARSAYILAN OLARAK KORUNUR.</b> Parolayı sıfırlarken 2FA'yı da sessizce
/// kapatmak, ikinci faktörü tam da korumak için var olduğu durumda (parola ele geçirildi/kayboldu)
/// işlevsiz kılardı: sunucuya bir kez erişen herkes tek hamlede tam yetkili bir oturum açardı.
/// Doğrulayıcısını da kaybetmiş kullanıcı için kapatma AYRI ve AÇIK bir istekle yapılır
/// (<c>disableTwoFactor</c>); bedeli budur ve çağıran taraf bunu kullanıcıya yazılı olarak sorar.
/// Kapatıldığında kurtarma kodları da geçersiz kılınır — panel içindeki kapatma akışıyla aynı
/// disiplin (<see cref="TwoFactorRecoveryCodes"/>).
/// </para>
/// </remarks>
public static class ConsolePasswordRecovery
{
    /// <summary>Panelde hiç yönetici yok (kurulum tamamlanmamış olabilir).</summary>
    public const string NoAdministratorsKey = "recovery.no_administrators";

    /// <summary>Verilen kullanıcı bulunamadı.</summary>
    public const string TargetNotFoundKey = "recovery.target_not_found";

    /// <summary>Kullanıcı var ama YÖNETİCİ değil; bu araçla parolası belirlenemez.</summary>
    public const string TargetNotAdministratorKey = "recovery.target_not_administrator";

    /// <summary>
    /// Parolası belirlenebilecek hesaplar: YALNIZ yönetici rolündekiler.
    /// Pasif hesaplar da listelenir (ayrımı <see cref="ConsoleAdminAccount.IsActive"/> taşır):
    /// gizlemek, kullanıcının aradığı hesabı "yok" sanmasına yol açardı.
    /// </summary>
    public static async Task<IReadOnlyList<ConsoleAdminAccount>> ListAdministratorsAsync(
        AdaPanelDbContext dbContext,
        CancellationToken cancellationToken = default)
    {
        var users = await dbContext.Users
            .AsNoTracking()
            .Where(user => user.Role == UserRole.Administrator)
            .OrderBy(user => user.UserName)
            .ToListAsync(cancellationToken);

        return users
            .Select(user => new ConsoleAdminAccount(
                user.Id,
                user.UserName,
                user.Email,
                user.IsActive,
                user.IsTwoFactorEnabled,
                user.LastLoginAtUtc))
            .ToList();
    }

    /// <summary>
    /// Kullanıcı adı ya da e-posta ile YÖNETİCİ hesabı bulur.
    /// </summary>
    /// <remarks>
    /// Bulunamayan ile "bulundu ama yönetici değil" AYRI hatalardır: ikisini tek mesajla geçmek,
    /// müşteri hesabının adını yazan kişiye "böyle bir kullanıcı yok" demek olurdu ve kişi doğru
    /// hesabı aramaya devam ederdi.
    /// </remarks>
    public static async Task<ConsoleAdminAccount> FindAdministratorAsync(
        AdaPanelDbContext dbContext,
        string userNameOrEmail,
        CancellationToken cancellationToken = default)
    {
        var lookup = (userNameOrEmail ?? string.Empty).Trim().ToLowerInvariant();
        var user = await dbContext.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(
                candidate => candidate.UserName.ToLower() == lookup || candidate.Email.ToLower() == lookup,
                cancellationToken)
            ?? throw new AppException(TargetNotFoundKey, 404, 404, userNameOrEmail ?? string.Empty);

        if (user.Role != UserRole.Administrator)
        {
            throw new AppException(TargetNotAdministratorKey, 403, 403, $"{user.UserName} ({user.Role})");
        }

        return new ConsoleAdminAccount(
            user.Id,
            user.UserName,
            user.Email,
            user.IsActive,
            user.IsTwoFactorEnabled,
            user.LastLoginAtUtc);
    }

    /// <summary>
    /// Yöneticinin parolasını belirler: parola yazılır, giriş kilidi açılır, API anahtarları düşer,
    /// istenirse iki adımlı doğrulama kapatılır ve işlem denetim kaydına yazılır.
    /// </summary>
    /// <param name="operatorName">
    /// İşlemi yapan WINDOWS hesabı (denetim kaydına yazılır). Panel kullanıcısı yok — kaydın
    /// "kim yaptı" sorusuna verebileceği tek doğru yanıt sunucudaki oturumdur.
    /// </param>
    public static async Task<ConsolePasswordResetOutcome> SetPasswordAsync(
        AdaPanelDbContext dbContext,
        IPasswordHasher<PanelUser> passwordHasher,
        JsonFileStore fileStore,
        Guid targetUserId,
        string newPassword,
        bool disableTwoFactor,
        string operatorName,
        CancellationToken cancellationToken = default)
    {
        var target = await dbContext.Users.FirstOrDefaultAsync(user => user.Id == targetUserId, cancellationToken)
            ?? throw new AppException(TargetNotFoundKey, 404, 404, targetUserId.ToString());

        // ROL DENETİMİ BURADA, veriyi yazan noktanın yanında. Çağıranın önceden seçim yaptığına
        // güvenmek yetmez: seçim listesi ile yazma arasında rol değişmiş olabilir ve bu araç
        // ileride başka bir yerden de çağrılabilir.
        if (target.Role != UserRole.Administrator)
        {
            throw new AppException(TargetNotAdministratorKey, 403, 403, $"{target.UserName} ({target.Role})");
        }

        // Parola yazma disiplininin tamamı (uzunluk kuralı, kilit açma, anahtar düşürme) panelin
        // kendi yollarıyla AYNI gövdeden geçer; ikinci bir kural yazılmadı.
        // Anahtar servisi burada DOĞRUDAN kuruluyor: kurtarma aracı, panelin servis grafiğini ayağa
        // kaldırmadan çalışmak zorunda (panel açılamıyor olabilir). Servisin kendisi yalnız
        // DbContext ister, dolayısıyla bu bir kısayol değil, bağımlılığın gerçek boyutudur.
        var revoked = await PasswordAssignment.ApplyAsync(
            dbContext, passwordHasher, new ApiTokenService(dbContext), target, newPassword, cancellationToken);

        var twoFactorDisabled = false;
        if (disableTwoFactor && target.IsTwoFactorEnabled)
        {
            target.IsTwoFactorEnabled = false;
            target.TwoFactorSecretProtected = null;
            target.PendingTwoFactorSecretProtected = null;
            target.UpdatedAtUtc = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);

            // Kurtarma kodları da düşer: 2FA kapatılırken eski kodların yaşamaya devam etmesi,
            // kullanıcı 2FA'yı yeniden açtığında ARTIK KİMSENİN BİLMEDİĞİ (ya da eski bir
            // yazıcı çıktısında duran) kodların hâlâ geçerli olması demekti.
            await TwoFactorRecoveryCodes.ClearAsync(fileStore, target.Id, cancellationToken);
            twoFactorDisabled = true;
        }

        // DENETİM KAYDI ŞART: bu, panelin dışından yapılan en yüksek etkili müdahaledir ve iz
        // bırakmadan yapılabiliyorsa denetlenebilir değildir.
        //
        // PAROLA ASLA YAZILMAZ: denetim kaydı yedeklere yayılır ve panelde okuma ucu vardır; kayıt,
        // korumaya çalıştığı sırrın ikinci deposu olamaz.
        await AuditWriter.WriteAsync(
            dbContext,
            "auth.password_set_from_console",
            "auth",
            $"Password set from the server console for '{target.UserName}' by Windows user '{operatorName}'. " +
            $"{revoked} API token(s) revoked. Two-factor: {(twoFactorDisabled ? "disabled" : "unchanged")}.",
            target.Id,
            null,
            null,
            cancellationToken);

        return new ConsolePasswordResetOutcome(target.UserName, revoked, twoFactorDisabled, target.IsActive);
    }
}

/// <summary>Konsol aracının listelediği yönetici hesabı.</summary>
public sealed record ConsoleAdminAccount(
    Guid Id,
    string UserName,
    string Email,
    bool IsActive,
    bool IsTwoFactorEnabled,
    DateTime? LastLoginAtUtc);

/// <summary>Konsoldan yapılan parola belirlemenin sonucu.</summary>
/// <param name="IsActive">
/// Hesap pasifse parola belirlendi ama GİRİŞ YİNE ENGELLENİR. Araç bunu sessizce geçemez; kullanıcı
/// "parolayı değiştirdim, hâlâ giremiyorum" duvarına toslar. Hesabı burada kendiliğinden aktifleştirmek
/// ise kapatma kararını sunucudan sessizce ezmek olurdu — durum bildirilir, karar kullanıcınındır.
/// </param>
public sealed record ConsolePasswordResetOutcome(
    string UserName,
    int RevokedApiTokens,
    bool TwoFactorDisabled,
    bool IsActive);
