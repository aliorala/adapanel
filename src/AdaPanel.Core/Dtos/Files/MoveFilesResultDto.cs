namespace AdaPanel.Core.Dtos.Files;

/// <summary>Tek bir ögenin taşıma sonucu.</summary>
public sealed class MoveFileResultDto
{
    /// <summary>Kaynağın sanal yolu.</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>Başarılıysa ögenin YENİ sanal yolu; başarısızsa boş.</summary>
    public string TargetPath { get; set; } = string.Empty;

    public bool Moved { get; set; }

    /// <summary>
    /// Başarısızlık sebebinin çeviri anahtarı (ör. <c>files.move_target_exists</c>). Serbest metin
    /// değil anahtar döner: arayüz sebebi kullanıcının dilinde gösterebilsin.
    /// </summary>
    public string ErrorKey { get; set; } = string.Empty;
}

/// <summary>
/// Çoklu taşımanın DÜRÜST raporu.
/// </summary>
/// <remarks>
/// Yalnız "başarılı" dönmek ya da ilk hatada patlayıp geri kalanı sessizce bırakmak, kullanıcıya
/// dosyalarının nerede olduğunu söylemez. Bu yüzden öge başına sonuç + sayaçlar döner:
/// "8 ögeden 6'sı taşındı, 2'si taşınamadı (sebep)".
/// </remarks>
public sealed class MoveFilesResultDto
{
    public int MovedCount { get; set; }

    public int FailedCount { get; set; }

    public MoveFileResultDto[] Items { get; set; } = [];

    /// <summary>İşlem sonrası HEDEF klasörün listesi (arayüz yeniden okumak zorunda kalmasın).</summary>
    public FileWorkspaceDto Workspace { get; set; } = new();
}
