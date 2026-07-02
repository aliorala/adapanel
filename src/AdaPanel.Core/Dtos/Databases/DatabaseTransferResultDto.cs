namespace AdaPanel.Core.Dtos.Databases;

public sealed class DatabaseTransferResultDto
{
    public string Engine { get; set; } = string.Empty;

    public string SourceDatabase { get; set; } = string.Empty;

    public string TargetDatabase { get; set; } = string.Empty;

    /// <summary>Aktarılan dökümün bayt cinsinden boyutu (bilgi amaçlı).</summary>
    public long Size { get; set; }

    /// <summary>Hedef DB'ye tam yetkiyle oluşturulan/ayarlanan kullanıcı (uygulamanın bağlanacağı).</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>Parola ÜRETİLDİYSE gösterilir (kullanıcı kendi verdiyse boş — zaten biliyor).</summary>
    public string Password { get; set; } = string.Empty;
}
