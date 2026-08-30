using AdaPanel.Api.Security;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

/// <summary>
/// Oturumdaki kullanıcının rolüne göre erişebildiği siteleri döner:
/// Administrator tümünü, Customer ve Reseller yalnızca kendi etkin kapsamını görür.
/// Dosya/yedek/e-posta ekranlarındaki site listeleri bu ucu kullanır.
/// </summary>
/// <remarks>
/// <para>
/// <b>Kapsam BURADA HESAPLANMAZ, <see cref="CustomerAccess.GetEffectiveSitesAsync"/>'e sorulur.</b>
/// Eskiden kural burada elle tekrarlanıyordu (<c>IsCustomer</c> değilse boş liste) ve ÖLÇÜLDÜ:
/// bayi, <c>GET /api/v1/sites</c> ve <c>GET /api/v1/files</c> ile kendi sitesini görebilirken bu
/// uç ona boş liste dönüyordu. Panelin bütün site seçicileri (dosya, yedek, e-posta, FTP, DNS,
/// PHP, günlükler…) tek bir depodan bu ucu okuduğu için bayinin ekranlarında seçenek HİÇ
/// çıkmıyordu — yani bayi, erişim hakkı olan sitesini arayüzden seçemiyordu.
/// </para>
/// <para>
/// Aynı sorunun iki cevabı olmamalı: kapsam sorusunun panelde tek adresi
/// <see cref="CustomerAccess"/>'tir ve kopya, kopyalardan birinin yanlış olması demektir.
/// </para>
/// <para>
/// <b>Kapsamsız rol boş liste alır — bu KASITLI ve kapalı taraftadır.</b>
/// <see cref="CustomerAccess.GetEffectiveSitesAsync"/> "kapsam yok" anlamında <c>null</c> döner ve
/// bu, yönetici DIŞINDA <c>Operator</c> gibi kapsamı tanımlanmamış roller için de geçerlidir.
/// <c>null</c>'ı "hepsi" saymak, bu ucu bir yetki genişletmesine çevirirdi; yönetici bu yüzden
/// yukarıda AYRI ve AÇIKÇA ele alınıyor.
/// </para>
/// </remarks>
[ApiController]
[Authorize]
[Route("api/v1/scope")]
public sealed class ScopeController(
    IIisSiteService iisSiteService,
    ICustomerScopeService customerScopes) : ControllerBase
{
    [HttpGet("sites")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<SiteDto>>>> GetSitesAsync(CancellationToken cancellationToken)
    {
        if (User.IsInRole("Administrator"))
        {
            var all = await iisSiteService.GetSitesAsync(cancellationToken);
            return Ok(ApiResponse<IReadOnlyCollection<SiteDto>>.Ok(all));
        }

        // KAPSAMSIZ ROL (yönetici hariç) BOŞ LİSTE ALIR: gerekçe sınıf açıklamasında.
        var assigned = await CustomerAccess.GetEffectiveSitesAsync(customerScopes, User, cancellationToken);
        if (assigned is null)
        {
            return Ok(ApiResponse<IReadOnlyCollection<SiteDto>>.Ok([]));
        }

        var sites = new List<SiteDto>(assigned.Count);
        foreach (var siteName in assigned)
        {
            try
            {
                sites.Add(await iisSiteService.GetSiteAsync(siteName, cancellationToken));
            }
            catch
            {
                // Site IIS'te artık yoksa listeden düşer.
            }
        }

        return Ok(ApiResponse<IReadOnlyCollection<SiteDto>>.Ok(sites));
    }
}
