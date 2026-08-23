namespace AdaPanel.Core.Dtos.Htaccess;

/// <summary>
/// Panelin "bu sitenin <c>.htaccess</c> dosyaları ne diyor?" ekranı için TEK kaynak.
/// </summary>
/// <remarks>
/// Kullanıcıya gösterilecek asıl değer <see cref="Unsupported"/> listesidir: hangi satırın
/// çalışMADIĞI ve NEDEN. Panelin sessizce "tamam" demesi, bu depoda tekrar tekrar bedeli
/// ödenmiş bir hatadır.
/// </remarks>
public sealed class HtaccessInspectionResult
{
    public string SiteRootPath { get; init; } = string.Empty;

    /// <summary>İncelenen dizinin site köküne göre yolu (<c>/</c>, <c>/blog/</c>).</summary>
    public string VirtualPath { get; init; } = "/";

    /// <summary>Kökten içeriye doğru bulunan dosyalar. Apache basamaklandırma sırası budur.</summary>
    public IReadOnlyList<HtaccessParseResult> Files { get; init; } = [];

    /// <summary>Bütün dosyaların bulguları, sırayla.</summary>
    public IReadOnlyList<HtaccessDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>Yalnızca "bu satır uygulanMIYOR" diyen bulgular — panelin uyarı listesi.</summary>
    public IReadOnlyList<HtaccessDiagnostic> Unsupported =>
        Diagnostics.Where(t => t.Level == HtaccessDiagnosticLevel.Unsupported).ToList();

    /// <summary>Uygulanamayan (ölümcül hatalı) dosya var mı?</summary>
    public bool HasFatal => Diagnostics.Any(t => t.Level == HtaccessDiagnosticLevel.Fatal);

    /// <summary><c>RewriteEngine On</c> etkin mi? Değilse mod_rewrite kuralları HİÇ çalışmaz.</summary>
    public bool RewriteEngineEnabled { get; init; }

    /// <summary>Basamaklandırma birleştirildikten sonra etkin kural sayısı.</summary>
    public int RewriteRuleCount { get; init; }

    /// <summary>
    /// Motorun uyguladığı yapılandırmanın özeti (dizin indeksi, hata belgeleri, seçenekler...).
    /// İstek bağımlı olmayan her şey burada.
    /// </summary>
    public HtaccessEvaluationResult Effective { get; init; } = new();
}
