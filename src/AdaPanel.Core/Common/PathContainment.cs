namespace AdaPanel.Core.Common;

/// <summary>
/// "Bu yol şu klasörün altında mı?" sorusunun TEK doğru cevabı.
/// <para>
/// Panelin birden çok yerinde aynı soru sorulur: zamanlanmış betiğin site kökünde olması, belge
/// kökünün siteden dışarı taşınmaması, FTP ana dizininin kiracının alanında kalması. Her yerde
/// yeniden yazmak, birini doğru diğerini eksik yapmanın yoludur — bu depoda zaten öyle oldu.
/// </para>
/// <para>
/// <b>Sadece <c>StartsWith</c> YETMEZ:</b> "C:\sites\site1" ile "C:\sites\site1-evil" ön ek olarak
/// eşleşir. Bu yüzden kökten sonraki karakterin bir dizin ayırıcısı olduğu da doğrulanır. "..",
/// "." ve karışık ayırıcı kaçışları <see cref="Path.GetFullPath(string)"/> normalleştirmesiyle
/// kapanır.
/// </para>
/// </summary>
public static class PathContainment
{
    /// <summary>
    /// Yolu mutlak + normalleştirilmiş biçime çevirir. Biçimsel olarak geçersizse (boş, geçersiz
    /// karakter, sadece sürücü harfi) <c>null</c> döner — çağıran net bir hataya çevirir, istisna
    /// sızmaz.
    /// </summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(value.Trim());

            // GÖRELİ YOL KABUL EDİLMEZ. Neye çözüleceği çağıranın o anki çalışma dizinine bağlıdır
            // ve sunucu tarafında bu tahmin edilemez; "httpdocs" gibi bir girdi beklenmedik bir
            // klasöre işaret edebilir. Aynı karar zamanlanmış görev yolları için de verilmişti
            // (Scheduler/ScriptTaskPlanner.NormalizePath) — orası kendi bağlamına özel kaldığı
            // için birleştirilmedi, ama kural aynıdır.
            if (!Path.IsPathFullyQualified(expanded))
            {
                return null;
            }

            var full = Path.GetFullPath(expanded);
            return string.IsNullOrWhiteSpace(full) ? null : full;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// <paramref name="candidate"/> gerçekten <paramref name="root"/> klasörünün ALTINDA mı?
    /// Kökün kendisi geçersiz sayılır (bir dosya, içinde bulunduğu klasörün kendisi olamaz).
    /// </summary>
    public static bool IsInsideRoot(string? root, string? candidate)
    {
        var normalizedRoot = Normalize(root);
        var normalizedCandidate = Normalize(candidate);
        if (normalizedRoot is null || normalizedCandidate is null)
        {
            return false;
        }

        normalizedRoot = normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        normalizedCandidate = normalizedCandidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (normalizedCandidate.Length <= normalizedRoot.Length)
        {
            return false;
        }

        if (!normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var boundary = normalizedCandidate[normalizedRoot.Length];
        return boundary == Path.DirectorySeparatorChar || boundary == Path.AltDirectorySeparatorChar;
    }

    /// <summary>
    /// <see cref="IsInsideRoot"/> ile aynı, ancak KÖKÜN KENDİSİ de geçerli sayılır.
    /// <para>
    /// Klasör hedefleri için gereken kural budur: bir betik site kökünün kendisi olamaz, ama site
    /// kökü meşru bir çalışma dizini ya da belge köküdür — üstelik en olağan durumdur.
    /// </para>
    /// </summary>
    public static bool IsRootOrInside(string? root, string? candidate)
    {
        var normalizedRoot = Normalize(root);
        var normalizedCandidate = Normalize(candidate);
        if (normalizedRoot is null || normalizedCandidate is null)
        {
            return false;
        }

        var trimmedRoot = normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var trimmedCandidate = normalizedCandidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return string.Equals(trimmedRoot, trimmedCandidate, StringComparison.OrdinalIgnoreCase)
               || IsInsideRoot(root, candidate);
    }
}
