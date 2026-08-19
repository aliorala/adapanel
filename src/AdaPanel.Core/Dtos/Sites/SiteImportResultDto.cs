namespace AdaPanel.Core.Dtos.Sites;

public sealed class SiteImportResultDto
{
    public string SiteName { get; set; } = string.Empty;

    public int FilesCopied { get; set; }

    /// <summary>
    /// "Önce temizle" seçiliyken sitenin ESKİ içeriğinin taşındığı kardeş klasör (boşsa yana alma
    /// yapılmadı demektir).
    /// <para>
    /// NEDEN GEREKLİ: içe aktarma sitenin tüm kökünü kardeş bir "_goc_yedek_&lt;tarih&gt;" klasörüne
    /// taşıyor ama panel bunu HİÇ SÖYLEMİYORDU. Kullanıcı site kökünü yeni içerikle dolu görünce
    /// eski dosyalarının SİLİNDİĞİNİ sanıyordu; oysa yanı başındaki klasörde duruyorlardı.
    /// </para>
    /// </summary>
    public string BackupPath { get; set; } = string.Empty;
}
