namespace AdaPanel.Core.Dtos.Seo;

/// <summary>
/// Tarama başlatma isteği.
/// </summary>
/// <remarks>
/// <para>
/// <b>Burada ADRES alanı YOKTUR ve olmamalıdır.</b> Taranacak konak, site adından ve o sitenin IIS
/// bağlamalarından türetilir; istek gövdesi hedefi HİÇ belirlemez. Adres alanı eklenseydi panel bir
/// SSRF aracına dönerdi: kiracı <c>http://169.254.169.254/latest/meta-data/</c> ya da iç ağdaki bir
/// yönetim arayüzünü panele taratabilirdi. Bu sınırın testi:
/// <c>SeoSsrfGuardTests</c>.
/// </para>
/// <para>
/// <paramref name="StartPath"/> yalnız site İÇİNDE bir başlangıç noktasıdır (ör. <c>/blog</c>) ve
/// katı biçim denetiminden geçer (bkz. <c>SeoLinkPolicy.TryNormalizeStartPath</c>).
/// </para>
/// </remarks>
public sealed class StartSeoScanRequest
{
    /// <summary>Site içi başlangıç yolu; boş bırakılırsa kök (<c>/</c>).</summary>
    public string StartPath { get; init; } = string.Empty;

    /// <summary>Sayfa üst sınırı; 0/aralık dışı ise varsayılana kırpılır (bkz. <c>SeoCrawlLimits</c>).</summary>
    public int MaxPages { get; init; }
}
