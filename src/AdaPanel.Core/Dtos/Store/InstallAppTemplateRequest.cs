namespace AdaPanel.Core.Dtos.Store;

public sealed class InstallAppTemplateRequest
{
    public string TemplateId { get; set; } = string.Empty;

    public string SiteName { get; set; } = string.Empty;

    public string TargetPath { get; set; } = string.Empty;

    /// <summary>
    /// Plesk "yedekle + temizle + kur": hedef klasör boş değilse kurulumu reddetmek yerine mevcut
    /// içeriği kardeş "_yedek_&lt;tarih&gt;" klasörüne taşıyıp klasörü boşaltır ve kuruluma devam eder.
    /// </summary>
    public bool CleanInstall { get; set; }
}
