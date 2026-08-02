namespace AdaPanel.Core.Entities;

/// <summary>
/// Panelde oluşan bir hatanın MASKELENMİŞ kaydı. Kurulumda bir kez alınan onaya karşılık
/// gönderilen veri kasten küçüktür: müşteriyi/veriyi tanımlayan hiçbir alan tutulmaz
/// (istek gövdesi, sorgu değerleri, parola/anahtar, müşteri adı, e-posta, IP yok).
/// </summary>
public sealed class ErrorReport
{
    public long Id { get; set; }

    /// <summary>
    /// Tip + mesaj + yığın izinin ilk kareleri üzerinden hesaplanan tekilleştirme anahtarı.
    /// NEDEN: döngüye giren bir hata saniyede yüzlerce kayıt üretip veritabanını şişirebilir;
    /// aynı parmak izi tekrar geldiğinde yeni satır değil <see cref="OccurrenceCount"/> artar.
    /// </summary>
    public string Fingerprint { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Aynı hatanın en son görüldüğü an; tekilleştirmede her tekrarda güncellenir.</summary>
    public DateTime LastSeenAtUtc { get; set; } = DateTime.UtcNow;

    public string PanelVersion { get; set; } = string.Empty;

    public string OsVersion { get; set; } = string.Empty;

    public string ExceptionType { get; set; } = string.Empty;

    /// <summary>Maskelenmiş hata mesajı (yol/alan adı/IP/e-posta/parola temizlenmiş).</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Maskelenmiş yığın izi; kullanıcı adı ve ev dizini sembolik hâle getirilmiştir.</summary>
    public string StackTrace { get; set; } = string.Empty;

    /// <summary>İstek yolu ŞABLON hâliyle (ör. "POST /api/v1/sites/{site}/quota") — değerler yok.</summary>
    public string RoutePattern { get; set; } = string.Empty;

    public string CorrelationId { get; set; } = string.Empty;

    public int OccurrenceCount { get; set; } = 1;

    /// <summary>Toplayıcıya gönderim zamanı; null ise henüz gönderilmedi.</summary>
    public DateTime? SentAtUtc { get; set; }
}
