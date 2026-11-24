namespace AdaPanel.Core.Dtos.Backup;

/// <summary>
/// Yalitilmis TEST geri yuklemesinin sonucu. Canli site/veritabani/IIS/posta'ya HICBIR sey yazilmaz;
/// arsiv gecici bir klasore acilir, sonra klasor silinir. <see cref="LiveDataTouched"/> daima false'dur.
/// </summary>
public sealed class BackupTestRestoreResultDto
{
    public string BackupId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>passed | failed | not_tested</summary>
    public string Outcome { get; set; } = "not_tested";

    /// <summary>Neden test edilemedi (outcome=not_tested icin i18n anahtari).</summary>
    public string NotTestedReason { get; set; } = string.Empty;

    public int EntryCount { get; set; }

    /// <summary>Gecici klasore acilip dogrulanan bayt.</summary>
    public long ExtractedBytes { get; set; }

    /// <summary>Diske yazilmadan okunup CRC'si dogrulanan bayt (gecici alan tavani asilinca).</summary>
    public long StreamVerifiedBytes { get; set; }

    public int FileFailures { get; set; }

    public int DatabaseDumpsChecked { get; set; }

    public int DatabaseDumpFailures { get; set; }

    /// <summary>Dogrulanamayan (ornegin .bak) veritabani dokumu sayisi: "gecti" sayilmaz, ayri soylenir.</summary>
    public int DatabaseDumpsUnchecked { get; set; }

    /// <summary>Gecici klasor silindi ve yoklugu OLCULDU.</summary>
    public bool CleanedUp { get; set; }

    public bool LiveDataTouched { get; set; }

    public long DurationMs { get; set; }

    /// <summary>Etiket-deger ayrintilari (cevrilmez): entry=...; reason=...</summary>
    public List<string> Issues { get; set; } = [];
}

public sealed class BackupTestRestoreRequest
{
    public string? EncryptionPassword { get; set; }
}
