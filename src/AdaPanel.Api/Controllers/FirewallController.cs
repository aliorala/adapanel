using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Firewall;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

// DENETİM KAYDI: buradaki üç uç, sunucunun DIŞARIYA AÇIK YÜZEYİNİ değiştirir — bir portu internete
// açar, kapatır ya da açılmış bir kuralı kaldırır. Sonucu panel dışında yaşar: kural Windows Defender
// Firewall'da kalır ve panel silinse bile durur. İz bırakılmazsa "3389 dün geceden beri dünyaya açık,
// bunu kim yaptı" sorusunun cevabı ne panelde ne Windows tarafında bulunur (Windows kural oluşturmayı
// varsayılan olarak günlüklemez). Bu yüzden port/protokol/kapsam DEĞERLERİ de kayda yazılır: hangi
// portun açıldığını bilmeyen bir kayıt, olayı yeniden kurmaya yetmez.
[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/firewall")]
public sealed class FirewallController(
    IFirewallService firewallService,
    IIpBlockService ipBlockService,
    AdaPanel.Data.Persistence.AdaPanelDbContext db,
    IAuditService auditService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<FirewallRuleDto>>>> GetRulesAsync(CancellationToken cancellationToken)
    {
        var rules = await firewallService.GetRulesAsync(cancellationToken);
        return Ok(ApiResponse<IReadOnlyCollection<FirewallRuleDto>>.Ok(rules));
    }

    [HttpGet("ports")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<PortStatusDto>>>> GetPortStatusAsync(CancellationToken cancellationToken)
    {
        // Panelin gerçek dinlediği port: katalog doğru panel portunu göstersin.
        var panelPort = HttpContext.Connection.LocalPort;
        var status = await firewallService.GetPortStatusAsync(panelPort, cancellationToken);
        return Ok(ApiResponse<IReadOnlyCollection<PortStatusDto>>.Ok(status));
    }

    [HttpGet("ip-blocks")]
    public async Task<ActionResult<ApiResponse<IpBlockListDto>>> GetIpBlocksAsync(CancellationToken cancellationToken) =>
        Ok(ApiResponse<IpBlockListDto>.Ok(await ipBlockService.GetAsync(cancellationToken)));

    /// <summary>
    /// IP/CIDR engeli ekler (istege bagli sureli). Kilitlenme korumasi: istegi yapan yoneticinin IP'si,
    /// son 12 saatte basarili giris yapmis IP'ler ve panelin kendi adresleri ASLA engellenmez.
    /// </summary>
    [HttpPost("ip-blocks")]
    public async Task<ActionResult<ApiResponse<IpBlockEntryDto>>> AddIpBlockAsync(
        [FromBody] AddIpBlockRequest request, CancellationToken cancellationToken)
    {
        var since = DateTime.UtcNow.AddHours(-12);
        var recentAdminIps = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToArrayAsync(
            db.AuditLogs.Where(log => log.Action == "auth.login.succeeded" && log.CreatedAtUtc >= since && log.IpAddress != null)
                .Select(log => log.IpAddress!).Distinct().Take(200), cancellationToken);
        var protectedAddresses = recentAdminIps
            .Append(HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty)
            .Append(HttpContext.Connection.LocalIpAddress?.ToString() ?? string.Empty)
            .Where(value => value.Length > 0).ToArray();

        try
        {
            var entry = await ipBlockService.AddAsync(request, User.Identity?.Name ?? "unknown", protectedAddresses, cancellationToken);
            await auditService.AuditAsync(HttpContext, "firewall.ipblock.added", $"firewall:{entry.RuleName}",
                $"target={entry.Target}; expires={(entry.ExpiresAtUtc is null ? "never" : entry.ExpiresAtUtc.Value.ToString("O"))}; " +
                $"rule={entry.RuleState}" + (entry.RuleState == "applied" ? string.Empty : " (NOT enforced on the network)"));
            return Ok(ApiResponse<IpBlockEntryDto>.Ok(entry, entry.RuleState == "applied" ? "firewall.ipblock_saved" : "firewall.ipblock_rule_failed"));
        }
        catch (AppException exception) when (exception.MessageKey == "firewall.ipblock_would_lock_out")
        {
            // Reddedilen girisim de kayda deger: yonetici kendini ya da paneli engellemeye calisti.
            await auditService.AuditAsync(HttpContext, "firewall.ipblock.refused", "firewall:ip-block",
                $"target={request.Target}; reason=lock-out protection");
            throw;
        }
    }

    [HttpDelete("ip-blocks/{id}")]
    public async Task<ActionResult<ApiResponse<object>>> RemoveIpBlockAsync(string id, CancellationToken cancellationToken)
    {
        var entry = await ipBlockService.RemoveAsync(id, cancellationToken);
        await auditService.AuditAsync(HttpContext, "firewall.ipblock.removed", $"firewall:{entry.RuleName}",
            $"target={entry.Target}; rule removed and verified.");
        return Ok(ApiResponse<object>.Ok(new { id }, "firewall.ipblock_removed"));
    }

    [HttpPost("allow")]
    public async Task<ActionResult<ApiResponse<FirewallRuleDto>>> AllowPortAsync([FromBody] UpdateFirewallRuleRequest request, CancellationToken cancellationToken)
    {
        var rule = await firewallService.AllowPortAsync(request, cancellationToken);

        // Bir portu AÇMAK, sunucuya yeni bir saldırı yüzeyi eklemektir; ihlal sonrası incelemenin ilk
        // sorusu "bu port ne zaman ve kim tarafından açıldı" olur.
        await auditService.AuditAsync(
            HttpContext,
            "firewall.rule.allowed",
            $"firewall:{rule.DisplayName}",
            $"Port {request.Port}/{request.Protocol} allowed (rule '{rule.DisplayName}', direction {rule.Direction}, {DescribeRuleState(rule)}).");

        return Ok(ApiResponse<FirewallRuleDto>.Ok(rule, "firewall.rule_saved"));
    }

    [HttpPost("block")]
    public async Task<ActionResult<ApiResponse<FirewallRuleDto>>> BlockPortAsync([FromBody] UpdateFirewallRuleRequest request, CancellationToken cancellationToken)
    {
        // Panelin O ANKİ portu servise iletilir ki panel kendi kapısını kilitleyemesin.
        var rule = await firewallService.BlockPortAsync(request, HttpContext.Connection.LocalPort, cancellationToken);

        // Engelleme de kayda değer: bir portun KAPANMASI, o portu kullanan hizmeti (posta, FTP, RDP)
        // dışarıdan erişilemez kılar. "Sitem/postam çalışmıyor" teşhisinin ilk bakılacak yeri budur.
        await auditService.AuditAsync(
            HttpContext,
            "firewall.rule.blocked",
            $"firewall:{rule.DisplayName}",
            $"Port {request.Port}/{request.Protocol} blocked (rule '{rule.DisplayName}', direction {rule.Direction}, {DescribeRuleState(rule)}).");

        return Ok(ApiResponse<FirewallRuleDto>.Ok(rule, "firewall.rule_saved"));
    }

    /// <summary>
    /// Denetim kaydına yazılacak kural durumu — <b>ÖLÇÜMDEN</b>, istekten değil.
    /// </summary>
    /// <remarks>
    /// İki kayıt cümlesi de <c>enabled {rule.Enabled}</c> yazıyordu ve o alan servis tarafında
    /// KOŞULSUZ <c>true</c> atanıyordu; yani kural hiç oluşmamış olsa bile kayıt "enabled True"
    /// diyordu. Kural artık güvenlik duvarından geri okunuyor; okunamadığında kayıt bunu
    /// SÖYLER — uydurmaz. TEK KAYNAK: iki uç da bu cümleyi buradan alır.
    /// </remarks>
    private static string DescribeRuleState(FirewallRuleDto rule) =>
        rule.Verified ? $"enabled {rule.Enabled}" : "state UNVERIFIED (rule could not be read back)";

    /// <summary>
    /// Panelin oluşturduğu bir kuralı kaldırır — yanlışlıkla eklenen bir engellemeyi geri almanın yolu.
    /// </summary>
    [HttpDelete("rules/{ruleName}")]
    public async Task<ActionResult<ApiResponse<object>>> RemoveRuleAsync(string ruleName, CancellationToken cancellationToken)
    {
        await firewallService.RemoveRuleAsync(ruleName, cancellationToken);

        // Kural KALDIRMA, kaydın en kritik hâlidir: kuralın kendisi delil olmaktan çıkar. Kaldırılan
        // kuralın adı yazılmazsa, ortadan kalkmış bir korumanın izi hiçbir yerde kalmaz.
        await auditService.AuditAsync(
            HttpContext,
            "firewall.rule.removed",
            $"firewall:{ruleName}",
            $"Firewall rule '{ruleName}' removed.");

        return Ok(ApiResponse<object>.Ok(new { ruleName }, "firewall.rule_removed"));
    }
}
