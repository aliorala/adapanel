using AdaPanel.Core.Dtos.Proxy;

namespace AdaPanel.Core.Interfaces;

public interface IReverseProxyService
{
    Task<ReverseProxySiteConfigurationDto> GetSiteConfigurationAsync(string siteName, CancellationToken cancellationToken = default);

    Task<ReverseProxySiteConfigurationDto> EnableProxyAsync(
        string siteName,
        EnableReverseProxyRequest request,
        CancellationToken cancellationToken = default);

    Task<ReverseProxySiteConfigurationDto> RemoveProxyAsync(string siteName, CancellationToken cancellationToken = default);

    Task<ReverseProxySiteConfigurationDto> AddRewriteRuleAsync(
        string siteName,
        AddRewriteRuleRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Bir sitede vekil KURALI YAZMADAN, yalnızca vekilliğin ön koşullarını sağlar:
    /// URL Rewrite + ARR bileşenleri kurulur ve <c>system.webServer/proxy</c> açılır.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden ayrı bir metot:</b> panelin kendi yazdığı bazı vekil kurallarının kural GÖVDESİ
    /// buradaki genel kalıba uymuyor (koşullu eşleşme, kural sırasının EN BAŞTA olması gibi). O
    /// kuralları yazan servisler ön koşulları da kendileri kurmaya kalksaydı, ARR bootstrap'i
    /// ikinci kez yazılmış olurdu — ve bileşen kimliği (<c>iis-arr</c>) değiştiğinde kopyalardan
    /// biri güncellenmeden kalırdı.
    /// </para>
    /// <para>
    /// Çağıran, IIS kilidini TUTMADAN çağırmalıdır: bu metot kendi içinde IIS kuyruğunu alır ve
    /// kilit yeniden girişe kapalıdır.
    /// </para>
    /// </remarks>
    Task EnsureArrPrerequisitesAsync(string siteName, CancellationToken cancellationToken = default);
}
