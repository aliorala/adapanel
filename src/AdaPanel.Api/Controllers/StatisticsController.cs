using AdaPanel.Api.Security;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Statistics;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

// MÜŞTERİ KENDİ TRAFİĞİNİ GÖREBİLMELİ.
//
// Bu uç müşteriye kapalıyken panel tuhaf bir çelişki üretiyordu: abonelik AYLIK TRAFİK KOTASI
// dolduğu için otomatik askıya alınıyor (SubscriptionEnforcementService, ölçüm kaynağı tam da
// bu servis), ama müşteri kotasını neyin doldurduğunu göremiyordu. Yaptırımı gördüğü hâlde
// ölçümü göremeyen kullanıcı, sorunu çözemez.
//
// Yalnız okuma yapan bir uç; kapsam denetimi EnsureSiteAsync ile.
[ApiController]
[Authorize(Roles = "Administrator,Reseller,Customer")]
[RequireSubUserSection("sites")]
[Route("api/v1/sites/{siteName}/statistics")]
public sealed class StatisticsController(
    IWebStatisticsService webStatisticsService,
    ICustomerScopeService customerScopes) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<WebStatisticsDto>>> GetAsync(
        string siteName,
        [FromQuery] int days = 7,
        CancellationToken cancellationToken = default)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var statistics = await webStatisticsService.GetSiteStatisticsAsync(siteName, days, cancellationToken);
        return Ok(ApiResponse<WebStatisticsDto>.Ok(statistics));
    }
}
