namespace AdaPanel.Core.Dtos.Advisor;

/// <summary>Plesk "Advisor" karşılığı tek öneri: durum + önem + düzeltme bölümü.</summary>
public sealed class AdvisorItemDto
{
    public string Id { get; set; } = string.Empty;

    /// <summary>critical | warning | info</summary>
    public string Severity { get; set; } = "info";

    /// <summary>Başlık i18n anahtarı (frontend çevirir).</summary>
    public string TitleKey { get; set; } = string.Empty;

    /// <summary>Açıklama i18n anahtarı (opsiyonel; boşsa yalnız başlık gösterilir).</summary>
    public string DetailKey { get; set; } = string.Empty;

    /// <summary>Dile bağımsız teknik ayrıntı (host, sürüm, "%96", tarih) — başlığın yanında ham gösterilir.</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>"Düzelt" butonunun gideceği panel bölümü (boşsa buton gösterilmez).</summary>
    public string ActionSection { get; set; } = string.Empty;
}

/// <summary>Advisor taramasının tüm bulguları + önem sayıları.</summary>
public sealed class AdvisorReportDto
{
    public int CriticalCount { get; set; }

    public int WarningCount { get; set; }

    public int InfoCount { get; set; }

    public DateTime CheckedAtUtc { get; set; }

    /// <summary>
    /// ÇALIŞTIRILAMAYAN kontrollerin adları. Boş liste = tüm kontroller çalıştı.
    /// <para>
    /// Gerekli: her kontrol ayrı try/catch içinde ve hata yutulduğunda o kontrolün önerileri listeye
    /// hiç eklenmiyor. Bu alan olmadan ekran, hiçbir şey ölçülememişken bile yeşil tik ve
    /// "Her şey yolunda" gösteriyordu — üstelik tarama zamanı taze olduğu için inandırıcı görünüyordu.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> FailedChecks { get; set; } = [];

    public IReadOnlyList<AdvisorItemDto> Items { get; set; } = [];
}
