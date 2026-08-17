using AdaPanel.Core.Common;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using Microsoft.AspNetCore.Identity;

namespace AdaPanel.Infrastructure.Security;

/// <summary>
/// Panel parolasını VERİTABANINA YAZAN tek nokta.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden ayrı bir yer:</b> parolayı yazmak tek bir alanı değiştirmek değildir; yanında her
/// seferinde yapılması gereken üç iş daha vardır ve üçü de unutulduğu anda sessizce delik açar:
/// </para>
/// <list type="number">
/// <item><b>Uzunluk kuralı</b> (<see cref="PasswordPolicy"/>) — veri açıklamaları yalnız MVC
/// bağlama yolunda çalışır; sunucu konsolundan ve MCP araçlarından gelen çağrılarda hiç
/// değerlendirilmez.</item>
/// <item><b>Giriş kilidinin açılması</b> — parolayı belirlemenin amacı kullanıcıyı içeri sokmaktır;
/// hatalı denemelerden kalan kilit yerinde kalırsa kişi yeni parolayla da giremez.</item>
/// <item><b>API anahtarlarının düşürülmesi</b> — anahtarlar paroladan BAĞIMSIZ kimlik
/// bilgileridir. Düşmezlerse "parolayı değiştirdim" demek erişimi kesmez, yalnız kesildiği
/// izlenimini verir.</item>
/// </list>
/// <para>
/// Bu üçlü daha önce üç ayrı gövdede (kendi parolasını değiştirme, yöneticinin başkası için
/// belirlemesi) elle tekrarlanıyordu; sunucu konsolundan kurtarma yolu eklenirken dördüncü bir
/// kopya çıkacaktı. Kopya sayısı arttıkça birinde unutulması an meselesidir — bu yüzden tek
/// gövdeye indirildi. Yetki/kapsam denetimi BİLEREK dışarıda bırakıldı: onun kuralı her çağrı
/// yolunda farklıdır (kendi parolası mevcut parolayı ister, yönetici kapsam kanıtı ister, konsol
/// yolu sunucuda oturum açmış olmayı ister) ve burada toplanamaz.
/// </para>
/// <para>
/// <b>Çerez oturumları burada ayrıca düşürülmez</b> çünkü zaten düşer:
/// <c>CookieSessionValidator</c> çerezdeki kimlik damgasını parola KARMASINDAN türetir; karma
/// değişince eski çerez en geç bir dakika içinde reddedilir. İkinci bir mekanizma yazmak, ikisinin
/// birbirinden ayrı düşmesi demekti.
/// </para>
/// </remarks>
internal static class PasswordAssignment
{
    /// <summary>
    /// Parolayı kurala uydurup yazar, kilidi açar ve kullanıcının API anahtarlarını düşürür.
    /// </summary>
    /// <returns>Bu değişiklik yüzünden geçersiz kılınan API anahtarı sayısı.</returns>
    public static async Task<int> ApplyAsync(
        AdaPanelDbContext dbContext,
        IPasswordHasher<PanelUser> passwordHasher,
        IApiTokenService apiTokenService,
        PanelUser target,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        PasswordPolicy.Ensure(newPassword);

        target.PasswordHash = passwordHasher.HashPassword(target, newPassword);
        target.UpdatedAtUtc = DateTime.UtcNow;
        target.FailedLoginCount = 0;
        target.LockedUntilUtc = null;

        // AÇIKÇA kaydet. Çağıran taraf denetim kaydını aynı DbContext üzerinden yazdığı için yazım
        // tesadüfen kalıcı olurdu; kayıt bir gün başka bir yere taşınırsa parola sessizce
        // değişmemiş olurdu.
        await dbContext.SaveChangesAsync(cancellationToken);

        return await apiTokenService.RevokeAllForUserAsync(target.Id, cancellationToken);
    }
}
