namespace AdaPanel.Core.Dtos.Files;

/// <summary>Tek bir ögenin kopyalama sonucu.</summary>
public sealed class CopyFileResultDto
{
    /// <summary>Kaynağın sanal yolu.</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>Başarılıysa KOPYANIN sanal yolu; başarısızsa boş.</summary>
    public string TargetPath { get; set; } = string.Empty;

    public bool Copied { get; set; }

    /// <summary>
    /// Sebebin çeviri anahtarı (ör. <c>files.copy_target_exists</c>). Serbest metin değil anahtar
    /// döner: arayüz sebebi kullanıcının dilinde gösterebilsin.
    /// <para>
    /// <b>Dolu olması "başarısız" demek DEĞİLDİR:</b> özyinelemeli kopyalama sırasında bir alt öge
    /// bağlantı (junction/symlink) olduğu için atlandıysa <see cref="Copied"/> <c>true</c> kalır ama
    /// anahtar <c>files.copy_link_skipped</c> olur — kopya oluştu, ama EKSİK. "Tamamlandı" deyip
    /// atlanan bağlantıyı susmak, kullanıcının eksik bir kopyayı tam sanmasına yol açardı.
    /// </para>
    /// </summary>
    public string ErrorKey { get; set; } = string.Empty;
}

/// <summary>
/// Çoklu kopyalamanın DÜRÜST raporu.
/// </summary>
/// <remarks>
/// Yalnız "başarılı" dönmek ya da ilk hatada patlayıp geri kalanı sessizce bırakmak, kullanıcıya
/// dosyalarının kopyalanıp kopyalanmadığını söylemez. Bu yüzden öge başına sonuç + sayaçlar döner:
/// "8 ögeden 6'sı kopyalandı, 2'si kopyalanamadı (sebep)".
/// </remarks>
public sealed class CopyFilesResultDto
{
    public int CopiedCount { get; set; }

    public int FailedCount { get; set; }

    public CopyFileResultDto[] Items { get; set; } = [];

    /// <summary>İşlem sonrası HEDEF klasörün listesi (arayüz yeniden okumak zorunda kalmasın).</summary>
    public FileWorkspaceDto Workspace { get; set; } = new();
}
