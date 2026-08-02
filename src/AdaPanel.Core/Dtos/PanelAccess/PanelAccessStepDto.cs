namespace AdaPanel.Core.Dtos.PanelAccess;

/// <summary>
/// Panelin dışarıya açılma zincirindeki TEK bir adımın sonucu.
/// <para>
/// Neden adım adım: "kuruldu" deyip yarım bırakmak en kötü sonuçtur — yönetici panelin
/// dışarıdan çalıştığını sanar, yapay zeka istemcisi bağlanamaz ve nerede takıldığı belli olmaz.
/// Bu yüzden her adım (DNS, IIS sitesi, sertifika, ters vekil, yenileme, dış erişim) kendi
/// durumunu ve dile bağımsız teknik ayrıntısını ayrı ayrı taşır.
/// </para>
/// </summary>
public sealed class PanelAccessStepDto
{
    /// <summary>dns | site | certificate | proxy | renewal | external</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>ok | warn | fail | unknown. "unknown" = ÖLÇEMEDİK, "sorun yok" DEMEK DEĞİLDİR.</summary>
    public string Status { get; set; } = "unknown";

    /// <summary>Rozette gösterilecek kısa değer (çözülen IP, "42 gün", "HTTP 200").</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Dile bağımsız teknik ayrıntı (hata metni, sorgulanan çözümleyici, ham çıktı).</summary>
    public string Detail { get; set; } = string.Empty;
}
