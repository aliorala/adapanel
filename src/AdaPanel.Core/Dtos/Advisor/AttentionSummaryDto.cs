namespace AdaPanel.Core.Dtos.Advisor;

/// <summary>
/// Menü rozetlerinde ve bildirim çekmecesinde gösterilecek TEK bir "dikkat" maddesi.
/// Metin değil ANAHTAR taşınır (<see cref="TitleKey"/>): çeviri frontend'in işidir.
/// </summary>
public sealed class AttentionItemDto
{
    /// <summary>Kararlı kimlik; aynı sorun her taramada aynı id ile gelir (frontend "okundu" tutabilir).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>critical | warning | info</summary>
    public string Severity { get; set; } = "info";

    /// <summary>Başlığın i18n ANAHTARI (metin DEĞİL).</summary>
    public string TitleKey { get; set; } = string.Empty;

    /// <summary>
    /// AÇIKLAMANIN i18n anahtarı — "ne yapılabilir"i insan diliyle söyler
    /// (ör. "PHP sürümü güncellenebilir"). Boş olabilir.
    /// <para>
    /// Neden ayrı: <see cref="TitleKey"/> yalnızca sorunun ADIDIR ve rozette/çekmecede tek başına
    /// bir şey anlatmaz. Danışman tarafında bu açıklama ZATEN üretiliyordu ama buraya kopyalanırken
    /// düşürülüyordu — yani panel elindeki bilgiyi kullanıcıya göstermiyordu.
    /// </para>
    /// </summary>
    public string DescriptionKey { get; set; } = string.Empty;

    /// <summary>Dile bağımsız teknik ayrıntı (servis adı, sürüm, IP). Boş olabilir.</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>Tıklanınca gidilecek panel bölümü (sites, software, updates, health, dns, settings...).</summary>
    public string Section { get; set; } = string.Empty;

    /// <summary>Tek-tık düzeltme ucu (ör. /api/v1/dns/server/repair). Boşsa düzeltme butonu gösterilmez.</summary>
    public string ActionEndpoint { get; set; } = string.Empty;

    /// <summary>Düzeltme ucunun HTTP metodu (POST). ActionEndpoint boşsa anlamsızdır.</summary>
    public string ActionMethod { get; set; } = string.Empty;

    /// <summary>Düzeltme çağrısının gövdesi gerekiyorsa hazır JSON alanları (ör. {"name":"W3SVC"}).</summary>
    public IReadOnlyDictionary<string, string> ActionPayload { get; set; } = new Dictionary<string, string>();
}

/// <summary>
/// Panelin TÜM dikkat sinyallerinin tek noktada toplanmış hâli (menü rozetleri + bildirim çekmecesi).
/// Yeni veri üretmez; Danışman, bileşen/panel güncellemeleri, DNS bind sağlığı ve servis durumlarını
/// birleştirir. Her kaynak ayrı try/catch ile okunur — bir kaynak patlarsa rozet yine de dolar.
/// </summary>
public sealed class AttentionSummaryDto
{
    public int CriticalCount { get; set; }

    public int WarningCount { get; set; }

    public int InfoCount { get; set; }

    public int TotalCount { get; set; }

    public DateTime CheckedAtUtc { get; set; }

    /// <summary>Bölüm başına madde sayısı (menü rozetleri bunu kullanır): {"software":2,"health":1,...}</summary>
    public IReadOnlyDictionary<string, int> SectionCounts { get; set; } = new Dictionary<string, int>();

    /// <summary>Okunamayan kaynakların adları (advisor, components, panelUpdate, dns, services) — sessiz kayıp olmasın.</summary>
    public IReadOnlyList<string> FailedSources { get; set; } = [];

    /// <summary>Öncelik sırasına (critical → warning → info) dizilmiş maddeler.</summary>
    public IReadOnlyList<AttentionItemDto> Items { get; set; } = [];
}
