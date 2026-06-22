using AdaPanel.Core.Dtos.Firewall;

namespace AdaPanel.Core.Interfaces;

public interface IFirewallService
{
    Task<IReadOnlyCollection<FirewallRuleDto>> GetRulesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Panelin bildiği hizmet portlarının sunucu-yerel durumunu döner: her port için dinleme
    /// (ve kapsam) + Windows Firewall izni + gerekli/opsiyonel. <paramref name="panelPort"/> panelin
    /// o anki dinlediği porttur (HttpContext'ten gelir) ki katalog gerçek panel portunu yansıtsın.
    /// </summary>
    Task<IReadOnlyCollection<PortStatusDto>> GetPortStatusAsync(int panelPort, CancellationToken cancellationToken = default);

    Task<FirewallRuleDto> AllowPortAsync(UpdateFirewallRuleRequest request, CancellationToken cancellationToken = default);

    Task<FirewallRuleDto> BlockPortAsync(UpdateFirewallRuleRequest request, CancellationToken cancellationToken = default);
}
