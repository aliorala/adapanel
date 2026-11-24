using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AdaPanel.Api.Security;

/// <summary>
/// M3: panelin kendi başlattığı disk yazımlarının kota kapısı (bkz. <see cref="IPanelWriteQuotaGuard"/>).
/// </summary>
/// <remarks>
/// <b>Yönetici muaf DEĞİL (bilinçli):</b> dosya yöneticisinde yönetici kotasızdır çünkü orada elle,
/// tek tek dosya yazıyor. Burada ise panel kendi başına (Git dağıtımı, uygulama kurulumu, site klonu,
/// anonim webhook) bir müşterinin sitesine yüzlerce MB yazar; yönetici düğmeye bassa da yazılan şey
/// müşterinin tavanına sayılır. Tavan doluysa çare tavanı yükseltmek ya da yer açmaktır.
/// Tavan yoksa (0) ya da sahip bulunamazsa kapı sessizce geçer. Servis kayıtlı değilse (yalnız doğrudan
/// kurulan test denetleyicilerinde) kapı geçilir; üretimde DI kaydı vardır.
/// </remarks>
internal static class DiskQuotaGate
{
    public static Task EnsureForSiteAsync(HttpContext? context, string siteName, long incomingBytes, CancellationToken cancellationToken)
        => context?.RequestServices?.GetService<IPanelWriteQuotaGuard>()?.EnsureRoomForSiteAsync(siteName, incomingBytes, cancellationToken)
           ?? Task.CompletedTask;

    public static Task EnsureForCopyAsync(HttpContext? context, string siteName, CancellationToken cancellationToken)
        => context?.RequestServices?.GetService<IPanelWriteQuotaGuard>()?.EnsureRoomForCopyOfSiteAsync(siteName, cancellationToken)
           ?? Task.CompletedTask;
}
