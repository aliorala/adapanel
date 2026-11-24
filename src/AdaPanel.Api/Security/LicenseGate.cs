using AdaPanel.Core.Dtos.Licensing;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AdaPanel.Api.Security;

/// <summary>
/// M4: YENİ kaynak açan uçların lisans kapısı. <see cref="ILicenseService.EnsureCanCreateAsync"/>
/// tanımlıydı ama hiçbir yerden çağrılmıyordu; "Kısıtlı" durumda bile site/veritabanı/posta kutusu
/// açılabiliyordu.
/// </summary>
/// <remarks>
/// Servis istek kapsamından ÇÖZÜLÜR (denetleyici kurucularını değiştirmeden). Lisanslama kapalıyken
/// (<c>VerificationUrl</c> boş) servis zaten hiçbir şeyi kısıtlamaz. Servis hiç kayıtlı değilse
/// (yalnız doğrudan kurulan test denetleyicilerinde olur) kapı geçilir; üretimde DI kaydı vardır.
/// Kısıtlı durumda <c>AppException</c> (402, <c>license.*</c> anahtarı) fırlar.
/// </remarks>
internal static class LicenseGate
{
    public static Task EnsureCanCreateAsync(
        HttpContext? context,
        LicensedResource resource,
        CancellationToken cancellationToken,
        int existingCount = 0)
    {
        var license = context?.RequestServices?.GetService<ILicenseService>();
        return license is null
            ? Task.CompletedTask
            : license.EnsureCanCreateAsync(resource, existingCount, cancellationToken);
    }
}
