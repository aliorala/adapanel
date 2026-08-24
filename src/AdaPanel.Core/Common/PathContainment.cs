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
#if NET48
            // BU DOSYA IKI HEDEFTE DERLENIYOR. Panel net8.0-windows'tur; AdaPanel.HtaccessModule
            // ise net48 OLMAK ZORUNDA (System.Web/IHttpModule yalniz orada var) ve bu dosyayi
            // KAYNAK OLARAK derliyor. Path.IsPathFullyQualified .NET Core 2.1 ile geldi, net48'de
            // YOK.
            //
            // NEDEN BURADA `#if`, motorun geri kalaninda DEGIL: bu sinif panelin ORTAK guvenlik
            // yardimcisidir -- silme, belge koku, FTP ana dizini ve zamanlanmis betik yollari buna
            // dayanir. Panelin CALISTIRDIGI kodu tek satir bile degistirmemek, "tek yazim" kuralindan
            // daha agir basti: net8 tarafi bugunku BCL cagrisini AYNEN yapmaya devam ediyor.
            //
            // Karsiligin ayni olmasi VARSAYILMIYOR: PortableIsPathFullyQualified, net8 testlerinde
            // BCL uyesiyle yan yana kosturulup denetleniyor (PathContainmentTests).
            if (!PortableIsPathFullyQualified(expanded))
#else
            if (!Path.IsPathFullyQualified(expanded))
#endif
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
    /// <see cref="Path.IsPathFullyQualified(string)"/>'in hedeften bağımsız karşılığı (Windows).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Bu üye ÖLÜ KOD DEĞİL:</b> net48 derlemesinde (IIS <c>.htaccess</c> modülü) çağrılan
    /// gerçek uygulamadır; net8 derlemesinde ise BCL üyesiyle EŞDEĞERLİĞİNİ ölçen testin ölçtüğü
    /// şeydir. <b>Panel kodu bunu ÇAĞIRMAMALI</b> — her yol <see cref="Normalize"/> üzerinden
    /// geçmeli, çünkü asıl güvenlik kararı orada verilen normalleştirmedir. Görünürlüğü yalnızca
    /// eşdeğerlik testinin okuyabilmesi için <c>public</c>: <c>AdaPanel.Core</c>'da
    /// <c>InternalsVisibleTo</c> yok ve ölçülmeyen bir karşılık, ölçülmemiş bir güvenlik
    /// kontrolüdür.
    /// </para>
    /// <para>
    /// Mantık .NET'in kendi <c>PathInternal.IsPartiallyQualified</c> uygulamasının tersidir ve
    /// TAHMİN DEĞİL: "tam nitelikli" demek, yolun neye çözüleceğinin o anki çalışma dizininden ve
    /// sürücü başına tutulan geçerli dizinden BAĞIMSIZ olması demektir. Üç durum vardır:
    /// </para>
    /// <list type="bullet">
    /// <item><c>\\sunucu\pay</c>, <c>//sunucu/pay</c>, <c>\\?\C:\x</c> — ayırıcıyla başlayan UNC ya
    /// da aygıt yolu: tam nitelikli.</item>
    /// <item><c>C:\x</c>, <c>C:/x</c> — sürücü harfi + iki nokta + ayırıcı: tam nitelikli.</item>
    /// <item><c>C:x</c> (sürücüye GÖRELİ — o sürücünün geçerli dizinine bağlıdır), <c>\x</c>
    /// (kökten ama SÜRÜCÜSÜZ), <c>x\y</c>: tam nitelikli DEĞİL.</item>
    /// </list>
    /// </remarks>
    public static bool PortableIsPathFullyQualified(string path)
    {
        if (path is null || path.Length < 2)
        {
            // "" ve "C" gibi girdiler; "C:" bile (uzunluk 2) sürücüye görelidir ve aşağıda elenir.
            return false;
        }

        if (IsDirectorySeparator(path[0]))
        {
            // "\\x" (UNC) ve "\\?\x" (aygıt) tam niteliklidir; tek "\x" DEĞİLDİR.
            return path[1] == '?' || IsDirectorySeparator(path[1]);
        }

        return path.Length >= 3
               && path[1] == Path.VolumeSeparatorChar
               && IsDirectorySeparator(path[2])
               && IsValidDriveChar(path[0]);
    }

    private static bool IsDirectorySeparator(char value) =>
        value == Path.DirectorySeparatorChar || value == Path.AltDirectorySeparatorChar;

    private static bool IsValidDriveChar(char value) =>
        (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z');

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
