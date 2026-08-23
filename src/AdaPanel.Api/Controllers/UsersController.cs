using AdaPanel.Api.Extensions;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator,Reseller")]
[Route("api/v1/users")]
public sealed class UsersController(IUserManagementService userManagementService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<UserSummaryDto>>>> GetUsersAsync(CancellationToken cancellationToken)
    {
        // Bayi (Reseller) ise servis yalnızca kendi müşterilerini döner.
        var users = await userManagementService.GetUsersAsync(User.GetRequiredUserId(), cancellationToken);
        return Ok(ApiResponse<IReadOnlyCollection<UserSummaryDto>>.Ok(users));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<UserSummaryDto>>> CreateUserAsync(
        [FromBody] CreateUserRequest request,
        CancellationToken cancellationToken)
    {
        var user = await userManagementService.CreateUserAsync(
            request,
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        return Ok(ApiResponse<UserSummaryDto>.Ok(user, "users.create.success"));
    }

    /// <summary>
    /// Kullanıcıyı siler. removeSites=true (yalnız admin): atanan siteler dosya + eşleşen DB'lerle
    /// cascade silinir; aksi halde siteler/veriler sunucuda kalır ve yönetime devredilmiş olur.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>YARIM KALAN SİLME 2xx DÖNMEZ (denetim Y7).</b> Uç eskiden her koşulda <c>200 OK</c>
    /// dönüyordu: veritabanı düşürülememiş, mail alan adı kalmış, dosyaları yarım silinmiş bir site
    /// sunucuda SAHİPSİZ kalıyordu ve WHMCS bu yanıta "success" deyip hizmeti iptal ediyordu —
    /// kimse bir daha bakmıyordu.
    /// </para>
    /// <para>
    /// Durum kodu <b>409</b> ve gövde <c>success=false</c>: WHMCS modülü (<c>AdaPanelClient</c>)
    /// zarftaki <c>success</c> alanına baktığı için bu yanıtı istisnaya çevirir ve hizmeti iptal
    /// EDİLMİŞ saymaz. Kalıntılar hem yapısal olarak (<c>data.leftovers</c>) hem de okunabilir
    /// biçimde (<c>details</c>) döner; <c>data.userDeleted</c> "iş yarım kaldı" ile "hiçbir şey
    /// olmadı" arasındaki farkı korur.
    /// </para>
    /// </remarks>
    [HttpDelete("{userId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<UserDeletionResultDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<UserDeletionResultDto>), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<UserDeletionResultDto>>> DeleteUserAsync(
        Guid userId,
        [FromQuery] bool removeSites,
        CancellationToken cancellationToken)
    {
        if (removeSites && !User.IsInRole("Administrator"))
        {
            throw new AdaPanel.Core.Common.AppException("users.forbidden", 403, 403);
        }

        var result = await userManagementService.DeleteUserAsync(
            userId,
            User.GetRequiredUserId(),
            removeSites,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        if (!result.Completed)
        {
            return StatusCode(
                StatusCodes.Status409Conflict,
                ApiResponse<UserDeletionResultDto>.Fail(
                    "users.delete.incomplete",
                    409,
                    result,
                    KalintilariYaz(result)));
        }

        return Ok(ApiResponse<UserDeletionResultDto>.Ok(result, "users.delete.success"));
    }

    /// <summary>
    /// Kalıntıları okunabilir satırlara çevirir — <c>details</c> alanına gider.
    /// </summary>
    /// <remarks>
    /// NEDEN METİN DE ÜRETİYORUZ: yapısal liste (<c>data.leftovers</c>) doğru yoldur ama onu okuyan
    /// bir istemci yazana kadar hiçbir insan görmez. WHMCS modülü ve panelin hata şeridi <c>details</c>
    /// alanını olduğu gibi basıyor; kalıntıların ADI (hangi site, hangi veritabanı, hangi alan adı)
    /// böylece HEMEN gözle görünür oluyor. Bilinçli bırakılanlar da yazılır ama "bilerek" diye
    /// işaretlenir: yönetici neyin arıza, neyin karar olduğunu ayırt edebilmeli.
    /// </remarks>
    private static string KalintilariYaz(UserDeletionResultDto result)
    {
        var satirlar = result.Leftovers.Select(leftover =>
        {
            var site = string.IsNullOrWhiteSpace(leftover.SiteName) ? string.Empty : $"{leftover.SiteName} › ";
            var isaret = leftover.Deliberate ? " [bilerek bırakıldı]" : string.Empty;
            return $"- {site}{leftover.Kind}: {leftover.Name} — {leftover.Reason}{isaret}";
        });

        return
            $"Kullanıcı kaydı silindi ({result.UserName}) ama sunucuda temizlenemeyen kaynaklar kaldı. " +
            "Hizmeti TAMAMEN kaldırılmış saymayın; aşağıdakileri elle temizleyin:\n" +
            string.Join('\n', satirlar);
    }

    [HttpPut("{userId:guid}")]
    public async Task<ActionResult<ApiResponse<UserSummaryDto>>> UpdateUserAsync(
        Guid userId,
        [FromBody] UpdateUserRequest request,
        CancellationToken cancellationToken)
    {
        var user = await userManagementService.UpdateUserAsync(
            userId,
            request,
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        return Ok(ApiResponse<UserSummaryDto>.Ok(user, "users.update.success"));
    }

    /// <summary>
    /// Hatalı giriş denemeleri yüzünden kilitlenmiş kullanıcının kilidini açar. Kilitli kullanıcı
    /// süre dolana kadar (varsayılan 15 dk) giriş yapamaz; bu uç beklemeden açar. Bayi yalnız kendi
    /// müşterisini açabilir. HİÇBİR yönetici giremiyorsa: veri kökünde config\recover-access.flag.
    /// </summary>
    [HttpPost("{userId:guid}/unlock")]
    public async Task<ActionResult<ApiResponse<UserSummaryDto>>> UnlockUserAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var user = await userManagementService.UnlockUserAsync(
            userId,
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        return Ok(ApiResponse<UserSummaryDto>.Ok(user, "users.unlock.success"));
    }
}
