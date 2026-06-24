namespace AdaPanel.Core.Dtos.Migration;

/// <summary>Göç akışının tek bir adımının sonucu (site / files / database).</summary>
public sealed class MigrationStepDto
{
    public string Step { get; set; } = string.Empty;

    public bool Success { get; set; }

    /// <summary>i18n anahtarı ya da ham hata metni.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>Site göçünün adım-adım sonucu.</summary>
public sealed class MigrateSiteResultDto
{
    public string SiteName { get; set; } = string.Empty;

    public bool OverallSuccess { get; set; }

    public List<MigrationStepDto> Steps { get; set; } = [];
}
