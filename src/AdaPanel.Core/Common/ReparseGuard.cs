namespace AdaPanel.Core.Common;

/// <summary>
/// Junction / sembolik bağlantı (reparse point) denetiminin TEK kaynağı.
/// </summary>
/// <remarks>
/// <para>
/// Eskiden FileService, SiteBuilderPathGuard ve belge kökü kapısı kendi kopyalarını taşıyordu ve
/// hepsi aynı iki açığa sahipti: (1) önce <c>Exists</c> sorup yoksa "bağlantı değil" demek —
/// hedefi silinmiş (dangling) bir junction'ı kaçırır, sonradan hedef yeniden yaratılınca bağlantı
/// canlanır; (2) yalnız kökün ALTINDAKİ parçaları denetlemek — kökün kendisi bağlantıysa
/// (<c>site\x → C:\ProgramData</c>) tüm sandbox diske açılır.
/// </para>
/// <para>
/// Ölçülemeyen (G/Ç ya da yetki hatası) yol "bağlantı" sayılır: kapıda ölçemediğimize izin vermeyiz.
/// Yalnız yolun GERÇEKTEN olmaması (FileNotFound/DirectoryNotFound) "bağlantı değil"dir.
/// </para>
/// </remarks>
public static class ReparseGuard
{
    /// <summary>Tek bir yol bağlantı mı? Hedefi silinmiş (dangling) junction da <c>true</c> döner.</summary>
    public static bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }

    /// <summary>
    /// <paramref name="fullPath"/>'e giden zincirde (isteğe bağlı kökün kendisi dahil) ilk bağlantıyı
    /// bulur; yoksa <c>null</c>.
    /// </summary>
    public static string? FindReparsePoint(string root, string fullPath, bool includeRoot = true)
    {
        var normalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (includeRoot && IsReparsePoint(normalRoot))
        {
            return normalRoot;
        }

        var relative = Path.GetRelativePath(normalRoot, Path.GetFullPath(fullPath));
        if (relative == ".")
        {
            return null;
        }

        var current = normalRoot;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (IsReparsePoint(current))
            {
                return current;
            }
        }

        return null;
    }

    /// <summary>
    /// <paramref name="boundary"/> (sitenin ana klasörü) doluysa ve <paramref name="path"/> onun içindeyse
    /// ana klasörden yola giden TÜM parçalar (ana klasör ve yolun kendisi dahil) denetlenir. Yol ana
    /// klasörün dışındaysa (yönetici kurulumu) yalnız yolun kendisi denetlenir.
    /// </summary>
    public static bool HasReparsePointWithin(string? boundary, string path)
    {
        if (!string.IsNullOrWhiteSpace(boundary) && PathContainment.IsRootOrInside(boundary, path))
        {
            return HasReparsePoint(boundary, path, includeRoot: true);
        }

        return IsReparsePoint(path);
    }

    public static bool HasReparsePoint(string root, string fullPath, bool includeRoot = true) =>
        FindReparsePoint(root, fullPath, includeRoot) is not null;
}
