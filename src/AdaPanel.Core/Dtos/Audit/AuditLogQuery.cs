namespace AdaPanel.Core.Dtos.Audit;

/// <summary>
/// Denetim kaydı arama ölçütleri (tarih aralığı, kullanıcı, eylem, kaynak, serbest metin) + sayfalama.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden tek nesne:</b> aynı ölçütler ÜÇ yerde uygulanır — sayfa listesi, toplam sayı ve CSV
/// akışı. Üçü ayrı ayrı yazılsaydı biri güncellenip diğeri unutulurdu ve "ekranda gördüğüm 12 kayıt"
/// ile "dışa aktardığım 40 kayıt" birbirini tutmazdı. Filtre tek yerde
/// (<c>AuditLogQueryEngine</c>) uygulanır.
/// </para>
/// <para>
/// Boş bırakılan alan "bu ölçüte bakma" demektir; hiçbiri doldurulmazsa kapsam dahilindeki tüm
/// kayıtlar döner.
/// </para>
/// </remarks>
public sealed class AuditLogQuery
{
    /// <summary>Bu andan (UTC) itibaren; null = sınırsız geçmiş.</summary>
    public DateTime? FromUtc { get; init; }

    /// <summary>Bu ana (UTC) kadar; null = sınırsız gelecek.</summary>
    public DateTime? ToUtc { get; init; }

    /// <summary>Kullanıcı adı (kısmi eşleşme). Kayıtta kimlik tutulur; ad → kimlik çözümü serviste yapılır.</summary>
    public string UserName { get; init; } = string.Empty;

    /// <summary>Eylem türü; nokta içermeyen değer ÖN EK olarak da eşleşir ("mail" → "mail.account.created").</summary>
    public string Action { get; init; } = string.Empty;

    /// <summary>Kaynak/hedef (kısmi eşleşme): dosya yolu, posta adresi, veritabanı adı…</summary>
    public string Resource { get; init; } = string.Empty;

    /// <summary>Serbest metin: eylem + kaynak + ayrıntı + IP alanlarında aranır.</summary>
    public string Search { get; init; } = string.Empty;

    /// <summary>1'den başlar.</summary>
    public int Page { get; init; } = 1;

    /// <summary>Sayfa başına kayıt.</summary>
    public int PageSize { get; init; } = 25;
}
