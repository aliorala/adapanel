namespace AdaPanel.Core.Dtos.Sites;

/// <summary>
/// Site göçü: kaynak sitenin dosyalarını içeren arşivi (http/https/ftp .zip URL'si) indirip hedef siteye açar.
/// </summary>
public sealed class ImportSiteArchiveRequest
{
    public string ArchiveUrl { get; set; } = string.Empty;

    /// <summary>True ise hedef site içeriği önce kardeş "_goc_yedek_&lt;tarih&gt;" klasörüne taşınıp temizlenir.</summary>
    public bool CleanFirst { get; set; }
}
