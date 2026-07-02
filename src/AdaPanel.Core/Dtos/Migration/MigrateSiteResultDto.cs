namespace AdaPanel.Core.Dtos.Migration;

/// <summary>Göç akışının tek bir adımının sonucu (site / files / database).</summary>
public sealed class MigrationStepDto
{
    public string Step { get; set; } = string.Empty;

    public bool Success { get; set; }

    /// <summary>i18n anahtarı ya da ham hata metni (kategori başlığı).</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Başarısızlıkta gerçek teknik neden (ham kütüphane mesajı: "530 Login incorrect", "/httpdocs not found" …).
    /// Başlık i18n anahtarı kullanıcıya "ne" olduğunu, bu alan "neden"i söyler. Başarıda null.</summary>
    public string? Detail { get; set; }
}

/// <summary>Site göçünün adım-adım sonucu.</summary>
public sealed class MigrateSiteResultDto
{
    public string SiteName { get; set; } = string.Empty;

    public bool OverallSuccess { get; set; }

    public List<MigrationStepDto> Steps { get; set; } = [];
}
