namespace AdaPanel.Core.Dtos.Seo;

/// <summary>
/// Günlük analizi isteği.
/// </summary>
/// <remarks>
/// <para>
/// <b>DOSYA YOLU ALANI YOKTUR ve olmamalıdır.</b> Okunacak klasör site adından türetilir
/// (<c>IisLogPaths.ResolveSiteDirectory(site.Id)</c>) ve site adı denetleyicide
/// <c>CustomerAccess.EnsureSiteAsync</c> ile kapsam doğrulamasından geçer. İstek gövdesine bir yol
/// alanı konsaydı kiracı BAŞKA bir kiracının erişim günlüğünü — ziyaretçi IP'leri, oturum
/// çerezleri, yönetim panelinin adresleri dahil — kendi panelinden okutabilirdi. Bu, tarama
/// hedefinin gövdeden alınmaması kuralının (<see cref="StartSeoScanRequest"/>) günlük tarafındaki
/// aynısıdır. Testi: <c>SeoLogTenantIsolationTests</c>.
/// </para>
/// </remarks>
public sealed class StartSeoLogAnalysisRequest
{
    /// <summary>Kaç günlük pencere okunacak; aralık dışı ise kırpılır (bkz. <c>SeoLogLimits</c>).</summary>
    public int Days { get; init; }

    /// <summary>
    /// Bot kimliği ters DNS ile doğrulansın mı.
    /// <para>
    /// Kapalıyken rapor her IP için "doğrulanmadı" der — <b>"sahte" demez</b>. Açıkken de yalnız
    /// ters DNS ÇÖZÜLEN ve motora ait ÇIKMAYAN IP'ler sahte sayılır.
    /// </para>
    /// </summary>
    public bool VerifyBots { get; init; }
}
