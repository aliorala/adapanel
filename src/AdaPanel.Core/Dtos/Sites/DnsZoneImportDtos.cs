namespace AdaPanel.Core.Dtos.Sites;

/// <summary>BIND bölge dosyası dışa aktarımı: metin + önerilen dosya adı.</summary>
public sealed class DnsZoneExportDto
{
    public string ZoneName { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string ZoneText { get; set; } = string.Empty;
}

/// <summary>Önizleme isteği: yalnız metin. Hiçbir şey yazılmaz.</summary>
public sealed class DnsZoneImportPreviewRequest
{
    public string ZoneText { get; set; } = string.Empty;
}

/// <summary>
/// Uygulama isteği. <see cref="PreviewHash"/> önizlenen metnin özetidir; farklı bir metin
/// uygulanamaz. <see cref="ConfirmOverwriteManaged"/> false iken panel-yönetilen kayıtlarla
/// çakışan satırlar ATLANIR ve raporlanır.
/// </summary>
public sealed class DnsZoneImportApplyRequest
{
    public string ZoneText { get; set; } = string.Empty;

    public bool ConfirmOverwriteManaged { get; set; }

    public string PreviewHash { get; set; } = string.Empty;
}

/// <summary>Ayrıştırma/doğrulama hatası: satır numarası + çeviri anahtarı (+ ayrıntı).</summary>
public sealed class DnsZoneImportIssueDto
{
    public int Line { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;
}

/// <summary>Önizleme/uygulama tablosundaki tek kayıt. Action: add | change | unchanged.</summary>
public sealed class DnsZoneImportItemDto
{
    public string Action { get; set; } = "add";

    public string Type { get; set; } = "A";

    public string Host { get; set; } = "@";

    public string Value { get; set; } = string.Empty;

    public int Ttl { get; set; } = 3600;

    public int? Priority { get; set; }

    /// <summary>change durumunda üzerine yazılacak mevcut kaydın kimliği.</summary>
    public string? ExistingId { get; set; }

    public string? ExistingValue { get; set; }

    public int? ExistingTtl { get; set; }

    /// <summary>Posta ile ilgili kayıt (MX, SPF, DKIM, DMARC, posta konakları) — yalnız etiket.</summary>
    public bool IsMailRelated { get; set; }

    /// <summary>Panel-yönetilen bir kaydı ezer ya da onunla çakışır.</summary>
    public bool ConflictsWithManaged { get; set; }
}

public sealed class DnsZoneImportPreviewDto
{
    public string ZoneName { get; set; } = string.Empty;

    public string PreviewHash { get; set; } = string.Empty;

    public DnsZoneImportItemDto[] Items { get; set; } = [];

    public int AddCount { get; set; }

    public int ChangeCount { get; set; }

    public int UnchangedCount { get; set; }

    public int ManagedConflictCount { get; set; }

    /// <summary>Bilerek içeri alınmayan satırlar (SOA, bölge kökü NS).</summary>
    public DnsZoneImportIssueDto[] Ignored { get; set; } = [];

    /// <summary>Boş değilse uygulanamaz.</summary>
    public DnsZoneImportIssueDto[] Errors { get; set; } = [];

    public bool IsSecondary { get; set; }

    public bool CanApply { get; set; }
}

public sealed class DnsZoneImportFailureDto
{
    public string Type { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;

    public string Error { get; set; } = string.Empty;
}

public sealed class DnsZoneImportResultDto
{
    public string ZoneName { get; set; } = string.Empty;

    public int Added { get; set; }

    public int Changed { get; set; }

    public int Unchanged { get; set; }

    /// <summary>confirmOverwriteManaged=false olduğu için atlanan, panel-yönetilen çakışmalar.</summary>
    public DnsZoneImportItemDto[] SkippedManaged { get; set; } = [];

    public DnsZoneImportFailureDto[] Failed { get; set; } = [];

    /// <summary>Hata sonrası hiç denenmeyen kayıt sayısı (ilk hatada durulur).</summary>
    public int NotAttempted { get; set; }

    /// <summary>Bir kısmı yazıldı, bir kısmı yazılamadı/atlandı.</summary>
    public bool IsPartial { get; set; }

    public SiteDnsZoneDto? Zone { get; set; }
}
