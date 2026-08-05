using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Firewall;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/firewall")]
public sealed class FirewallController(IFirewallService firewallService) : ControllerBase
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

    [HttpPost("allow")]
    public async Task<ActionResult<ApiResponse<FirewallRuleDto>>> AllowPortAsync([FromBody] UpdateFirewallRuleRequest request, CancellationToken cancellationToken)
    {
        var rule = await firewallService.AllowPortAsync(request, cancellationToken);
        return Ok(ApiResponse<FirewallRuleDto>.Ok(rule, "firewall.rule_saved"));
    }

    [HttpPost("block")]
    public async Task<ActionResult<ApiResponse<FirewallRuleDto>>> BlockPortAsync([FromBody] UpdateFirewallRuleRequest request, CancellationToken cancellationToken)
    {
        // Panelin O ANKİ portu servise iletilir ki panel kendi kapısını kilitleyemesin.
        var rule = await firewallService.BlockPortAsync(request, HttpContext.Connection.LocalPort, cancellationToken);
        return Ok(ApiResponse<FirewallRuleDto>.Ok(rule, "firewall.rule_saved"));
    }

    /// <summary>
    /// Panelin oluşturduğu bir kuralı kaldırır — yanlışlıkla eklenen bir engellemeyi geri almanın yolu.
    /// </summary>
    [HttpDelete("rules/{ruleName}")]
    public async Task<ActionResult<ApiResponse<object>>> RemoveRuleAsync(string ruleName, CancellationToken cancellationToken)
    {
        await firewallService.RemoveRuleAsync(ruleName, cancellationToken);
        return Ok(ApiResponse<object>.Ok(new { ruleName }, "firewall.rule_removed"));
    }
}
