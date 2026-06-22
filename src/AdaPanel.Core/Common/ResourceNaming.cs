namespace AdaPanel.Core.Common;

/// <summary>
/// Site adından türetilen kaynak adları için tek kaynak. Birleşik oluşturma (SitesController)
/// ile cascade silmenin (SiteCascadeDeletionService) AYNI adı üretmesi şarttır; aksi halde
/// silme, oluşturulan veritabanını bulamaz.
/// </summary>
public static class ResourceNaming
{
    /// <summary>Site adından geçerli bir veritabanı adı türetir (küçük harf, harf/rakam dışı '_').</summary>
    public static string DatabaseNameFromSite(string siteName)
    {
        var chars = (siteName ?? string.Empty).Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '_')
            .ToArray();
        return new string(chars);
    }
}
