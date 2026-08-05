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

    /// <summary>
    /// Bir portu dışarıya kapatır.
    /// <para>
    /// <paramref name="panelPort"/> panelin O ANKİ dinlediği porttur ve KORUNUR: panelin kendi
    /// kapısını kilitlemesine izin verilmez. Windows'ta engelleme kuralı izin kuralını ezer,
    /// dolayısıyla panel kapandıktan sonra panelden geri açmak da mümkün olmaz — çıkış yolu
    /// yalnızca sunucu konsolu kalırdı. Uzak masaüstü ve web portları da aynı sebeple korunur.
    /// </para>
    /// </summary>
    Task<FirewallRuleDto> BlockPortAsync(UpdateFirewallRuleRequest request, int panelPort, CancellationToken cancellationToken = default);

    /// <summary>
    /// Panelin oluşturduğu bir kuralı kaldırır.
    /// <para>
    /// Kural yazabilip silememek TEK YÖNLÜ BİR KAPIDIR: yanlışlıkla eklenen bir engelleme
    /// kuralı panelden geri alınamıyordu.
    /// </para>
    /// </summary>
    Task RemoveRuleAsync(string ruleName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Panelin oluşturduğu TÜM engelleme kurallarını kaldırır — erişim kurtarma kolu.
    /// <para>
    /// Yönetici yanlışlıkla panel portunu ya da uzak masaüstünü engellemişse panele bir daha
    /// ulaşılamaz; Windows'ta engelleme izni ezdiği için panelden geri almak da mümkün olmaz.
    /// Sunucu konsolundan atılan <c>recover-access.flag</c> bu yüzden o kuralları da temizler.
    /// </para>
    /// <para>Kaldırılan kural sayısını döner.</para>
    /// </summary>
    Task<int> ClearManagedBlockRulesAsync(CancellationToken cancellationToken = default);
}
