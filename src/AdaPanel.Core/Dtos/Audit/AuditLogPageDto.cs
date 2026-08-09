namespace AdaPanel.Core.Dtos.Audit;

/// <summary>
/// Sayfalanmış denetim kaydı sonucu.
/// </summary>
/// <remarks>
/// <see cref="TotalCount"/> olmadan arayüz sayfalayamaz: "sonraki" düğmesinin ne zaman
/// kapanacağını ancak toplam bilinirse söyleyebiliriz.
/// </remarks>
public sealed class AuditLogPageDto
{
    public IReadOnlyCollection<AuditLogDto> Items { get; init; } = Array.Empty<AuditLogDto>();

    /// <summary>Filtreye uyan TOPLAM kayıt sayısı (sayfa değil).</summary>
    public int TotalCount { get; init; }

    public int Page { get; init; }

    public int PageSize { get; init; }

    /// <summary>
    /// Kapsam dahilinde geçen ayrık eylem türleri — arayüzdeki "eylem" açılır listesi için.
    /// Sabit bir liste gömmek yerine VERİDEN okunur; yeni bir eylem eklendiğinde filtre kendiliğinden öğrenir.
    /// </summary>
    public IReadOnlyCollection<string> AvailableActions { get; init; } = Array.Empty<string>();
}
