namespace AdaPanel.Core.Dtos.Migration;

/// <summary>Göç öncesi tek bir ön-kontrolün sonucu (site adı, dosya kaynağı erişimi, DB kaynağı erişimi).</summary>
public sealed class MigrationCheckDto
{
    public string Name { get; set; } = string.Empty;

    public bool Ok { get; set; }

    /// <summary>i18n anahtarı ya da ham metin.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>Göç öncesi doğrulama sonucu: göç başlamadan bağlantı/uygunluk kontrolleri.</summary>
public sealed class MigrationValidationResultDto
{
    public bool AllPassed { get; set; }

    public List<MigrationCheckDto> Checks { get; set; } = [];
}
