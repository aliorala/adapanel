using AdaPanel.Core.Common;

namespace AdaPanel.Infrastructure.SiteBuilder;

/// <summary>
/// Yayının site kökünün DIŞINA yazmasını engelleyen kapı.
/// </summary>
/// <remarks>
/// <para>
/// İki ayrı saldırı/kaza yolu vardır ve ikisi de burada kapanır:
/// </para>
/// <list type="number">
/// <item>
/// <b>Yol kaçışı</b> (<c>..</c>, mutlak yol, karışık ayırıcı): <see cref="PathContainment"/>
/// normalleştirmesiyle çözülür. Yalnız <c>StartsWith</c> yetmez — "C:\sites\site1" ile
/// "C:\sites\site1-evil" ön ek olarak eşleşir.
/// </item>
/// <item>
/// <b>Yeniden ayrıştırma noktası</b> (junction / sembolik bağlantı): yol dizge olarak kökün
/// altında görünür ama dosya sistemi onu BAŞKA bir yere çözer. Dizge denetimi bunu göremez;
/// bu yüzden kökten hedefe kadar HER segment ayrıca yoklanır. Bu depoda FTP tarafında tam da
/// klasör bağlantısı üzerinden müşteri sitesi silinebiliyordu — sınıf aynı sınıf.
/// </item>
/// </list>
/// <para>
/// Kökün KENDİSİ de yoklanır: site kökü bir junction ise, altındaki her yazma zaten başka bir
/// diske gidiyordur ve tek tek segment yoklamak bunu yakalamaz.
/// </para>
/// </remarks>
public static class SiteBuilderPathGuard
{
    /// <summary>
    /// Yayın hedefinin kendisini doğrular: mutlak, var olan (ya da oluşturulabilir) ve bağlantı
    /// noktası OLMAYAN bir klasör.
    /// </summary>
    /// <returns>Normalleştirilmiş kök yol.</returns>
    public static string EnsureUsableRoot(string? siteRoot)
    {
        var root = PathContainment.Normalize(siteRoot);
        if (root is null)
        {
            throw new AppException("sitebuilder.target_invalid", 400, 400);
        }

        if (IsReparsePoint(root))
        {
            throw new AppException("sitebuilder.target_is_link", 400, 400);
        }

        return root;
    }

    /// <summary>
    /// Kök altındaki göreli bir yolu, yazılabilir mutlak yola çevirir. Kaçış ya da bağlantı
    /// noktası varsa istisna atar — "en iyi çaba" ile atlamak, tam da korumanın işe yaraması
    /// gereken durumda yazmaya devam etmek olurdu.
    /// </summary>
    public static string ResolveWriteTarget(string normalizedRoot, string relativeUrlPath)
    {
        if (SiteBuilderLayout.IsProtected(relativeUrlPath))
        {
            throw new AppException("sitebuilder.target_protected", 400, 400);
        }

        var relative = SiteBuilderLayout.ToFileSystemPath(relativeUrlPath.Replace('\\', '/').Trim().Trim('/'));
        if (relative.Length == 0)
        {
            throw new AppException("sitebuilder.target_invalid", 400, 400);
        }

        var candidate = Path.GetFullPath(Path.Combine(normalizedRoot, relative));
        if (!PathContainment.IsInsideRoot(normalizedRoot, candidate))
        {
            throw new AppException("sitebuilder.target_outside_root", 400, 400);
        }

        EnsureNoReparsePointOnPath(normalizedRoot, candidate);
        return candidate;
    }

    /// <summary>
    /// Kökten hedefe kadar her ara segmenti (ve hedefin kendisini) yoklar.
    /// </summary>
    /// <param name="denetlenenOnekler">
    /// Verilirse, "temiz" çıkan her ara yol buraya yazılır ve aynı çağrı kümesi içinde bir daha
    /// sorulmaz.
    /// <para>
    /// <b>Neden var:</b> ARŞİV çıkarma bu denetimi GİRDİ BAŞINA çağırır ve bir arşivde aynı klasör
    /// öneki binlerce girdide tekrarlanır (bir WordPress paketinde <c>wp-content/plugins/...</c>
    /// tek başına binlerce girdinin önekidir). Önbelleksiz hâl, meşru bir paketin geri
    /// yüklenmesine önek parçası başına üç dosya sistemi sorgusu eklerdi.
    /// </para>
    /// <para>
    /// <b>Doğruluğu düşürmez:</b> bir arşiv girdisi bağlantı OLUŞTURAMAZ (girdi ya dosya ya
    /// klasördür), yani çıkarma sürerken "temiz" damgalanmış bir önek bağlantıya dönüşemez.
    /// Kümeyi ÇAĞRAN tutar; her çıkarma işi kendi taze kümesiyle başlar, yani damga işler arasına
    /// TAŞINMAZ. <c>null</c> ise (tek yol çözümleyen bütün çağrılar) davranış birebir eskisidir.
    /// </para>
    /// </param>
    public static void EnsureNoReparsePointOnPath(
        string normalizedRoot,
        string fullPath,
        HashSet<string>? denetlenenOnekler = null)
    {
        var relative = Path.GetRelativePath(normalizedRoot, fullPath);
        var current = normalizedRoot;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (denetlenenOnekler is not null && !denetlenenOnekler.Add(current))
            {
                continue;
            }

            if (IsReparsePoint(current))
            {
                throw new AppException("sitebuilder.target_is_link", 400, 400);
            }
        }
    }

    /// <summary>
    /// Var olan bir yolun junction/sembolik bağlantı olup olmadığı. Yol yoksa <c>false</c>:
    /// henüz oluşturulmamış bir klasör bağlantı olamaz.
    /// </summary>
    public static bool IsReparsePoint(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return false;
            }

            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // ÖLÇEMEDİK. Bunu "bağlantı değil" saymak, denetimi sessizce kapatmak olurdu; yazma
            // kapısında ölçemediğimiz şeye izin vermeyiz.
            return true;
        }
    }
}
