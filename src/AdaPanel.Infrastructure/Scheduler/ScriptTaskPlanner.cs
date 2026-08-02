namespace AdaPanel.Infrastructure.Scheduler;

/// <summary>
/// "script" görev tipinin SAF (yan etkisiz) mantığı: yol normalleştirme, uzantı beyaz listesi,
/// site kökü içinde kalma denetimi ve Task Scheduler eyleminin (Execute + Argument) üretimi.
/// <para>
/// Neden ayrı sınıf: bu üç karar (uzantı, kapsama, tırnaklama) görevin güvenlik ve doğruluk özüdür;
/// PowerShell/IIS'e dokunmadan birim testle kanıtlanabilmeleri gerekir.
/// </para>
/// </summary>
internal static class ScriptTaskPlanner
{
    /// <summary>Yalnızca bu uzantılar çalıştırılabilir. Karar KULLANICI METNİNE göre değil, <see cref="Path.GetExtension(string)"/> sonucuna göre verilir.</summary>
    internal static readonly string[] AllowedExtensions = [".bat", ".cmd", ".ps1"];

    /// <summary>
    /// Yolu mutlak + normalleştirilmiş biçime çevirir ("..", "/" ayırıcı, sondaki nokta/boşluk vb. temizlenir).
    /// Yol biçimsel olarak geçersizse (boş, geçersiz karakter, sadece sürücü harfi) null döner —
    /// çağıran net bir hata koduna çevirir, exception sızmaz.
    /// </summary>
    internal static string? NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
            if (!Path.IsPathFullyQualified(expanded))
            {
                // Göreli yol kabul edilmez: görev SYSTEM/havuz bağlamında BAŞKA bir çalışma dizininde
                // başlar, "cron.bat" gibi bir girdi sunucuda tahmin edilemeyen bir dosyaya çözülür.
                return null;
            }

            return Path.GetFullPath(expanded);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>Uzantı beyaz listesi denetimi; girdi <see cref="NormalizePath"/> çıktısı olmalıdır.</summary>
    internal static bool IsAllowedExtension(string fullPath) =>
        AllowedExtensions.Contains(Path.GetExtension(fullPath), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="candidate"/> gerçekten <paramref name="root"/> klasörünün ALTINDA mı?
    /// <para>
    /// Sadece StartsWith YETMEZ: "C:\sites\site1" ile "C:\sites\site1-evil" ön ek olarak eşleşir.
    /// Bu yüzden kökten sonraki karakterin bir dizin AYIRICISI olduğu da doğrulanır. "..", "." ve
    /// karışık ayırıcı kaçışları <see cref="Path.GetFullPath(string)"/> normalleştirmesiyle kapanır.
    /// Kökün kendisi (dosya değil, klasörün ta kendisi) da geçersiz sayılır.
    /// </para>
    /// </summary>
    internal static bool IsInsideRoot(string? root, string? candidate)
    {
        var normalizedRoot = NormalizePath(root);
        var normalizedCandidate = NormalizePath(candidate);
        if (normalizedRoot is null || normalizedCandidate is null)
        {
            return false;
        }

        // Her iki tarafın da sondaki ayırıcısı atılır: "C:\sites\site1\" ile "C:\sites\site1" AYNI
        // klasördür ve kökün kendisi (dosya değil) geçerli bir betik hedefi sayılmamalıdır.
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
    /// Klasör denetimi: <see cref="IsInsideRoot"/> ile aynı, ancak KÖKÜN KENDİSİ de geçerli sayılır.
    /// <para>
    /// Çalışma dizini için ayrı bir kural gerekir. Bir BETİK site kökünün kendisi olamaz (klasör
    /// çalıştırılamaz), ama site kökü bir çalışma dizini olarak tamamen meşrudur — üstelik en olağan
    /// durumdur: kullanıcı alanı boş bırakır, çalışma dizini betiğin klasörüne düşer ve site kökünde
    /// duran bir .bat için o klasör kökün ta kendisidir. Bu ayrım yapılmazsa site kökündeki hiçbir
    /// betik zamanlanamaz; canlı testte tam olarak bu yaşandı.
    /// </para>
    /// </summary>
    internal static bool IsRootOrInsideRoot(string? root, string? candidate)
    {
        var normalizedRoot = NormalizePath(root);
        var normalizedCandidate = NormalizePath(candidate);
        if (normalizedRoot is null || normalizedCandidate is null)
        {
            return false;
        }

        var trimmedRoot = normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var trimmedCandidate = normalizedCandidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return string.Equals(trimmedRoot, trimmedCandidate, StringComparison.OrdinalIgnoreCase)
               || IsInsideRoot(root, candidate);
    }

    /// <summary>
    /// Betiği çalıştıracak sarmalayıcı komutu üretir.
    /// <list type="bullet">
    /// <item>.bat/.cmd → <c>cmd.exe /c ""&lt;yol&gt;" &lt;arg&gt;"</c></item>
    /// <item>.ps1 → <c>powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "&lt;yol&gt;" &lt;arg&gt;</c></item>
    /// </list>
    /// <para>
    /// Neden sarmalayıcı: Task Scheduler'a .bat'ı DOĞRUDAN vermek boşluklu yollarda ve çıkış kodu
    /// raporlamada güvenilmez; cmd /c ile betiğin ERRORLEVEL'i görevin "son sonuç" alanına düzgün yansır.
    /// </para>
    /// <para>
    /// Neden DIŞ tırnak (<c>/c ""..." ..."</c>): cmd, /c'den sonra tırnak sayısı TAM İKİ ise ilk ve son
    /// tırnağı SİLER. Boşluklu bir yolda <c>/c "C:\a b\x.bat"</c> bu kurala takılır ve tırnaksız,
    /// bölünmüş bir yol kalır. Dış tırnakla toplam tırnak sayısı dörde çıkar, silme kuralı devreye
    /// girmez ve cmd satırı <c>"C:\a b\x.bat" arg</c> olarak doğru ayrıştırır.
    /// </para>
    /// <para>
    /// powershell.exe için -File son parametredir; ondan sonrası betiğin kendi argümanlarıdır.
    /// -NoProfile: yönetici profili betiği etkilemesin. -NonInteractive: görev bir isteme takılıp asılmasın.
    /// -ExecutionPolicy Bypass: imzasız panel betiği makine ilkesinden bağımsız çalışsın.
    /// </para>
    /// </summary>
    internal static (string Execute, string Arguments) BuildAction(string fullScriptPath, string? arguments)
    {
        var extraArguments = arguments?.Trim() ?? string.Empty;
        if (string.Equals(Path.GetExtension(fullScriptPath), ".ps1", StringComparison.OrdinalIgnoreCase))
        {
            var powerShellArguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{fullScriptPath}\"";
            if (extraArguments.Length > 0)
            {
                powerShellArguments += " " + extraArguments;
            }

            return ("powershell.exe", powerShellArguments);
        }

        var inner = $"\"{fullScriptPath}\"";
        if (extraArguments.Length > 0)
        {
            inner += " " + extraArguments;
        }

        return ("cmd.exe", $"/c \"{inner}\"");
    }

    /// <summary>
    /// Sınırlı yetkili görev kimliği. Site betikleri SYSTEM yerine BU hesapla çalışır.
    /// <para>
    /// Neden uygulama havuzu hesabı DEĞİL: "IIS APPPOOL\&lt;havuz&gt;" (S-1-5-82-…) bir ada karşılık
    /// SID'e çözülse bile Task Scheduler bir prensipal olarak KABUL ETMİYOR. Bu makinede dört yol da
    /// denendi ve dördü de "hesap adı-SID eşlemesi yapılamadı" (0x80070534) verdi:
    /// New-ScheduledTaskPrincipal ile LogonType Password / ServiceAccount / S4U / Interactive.
    /// schtasks.exe /RU "IIS APPPOOL\…" /NP ise DAHA KÖTÜSÜNÜ yapıyor: "SUCCESS" yazdırıp görevi
    /// UserId'si BOŞ, LogonType'ı Group olarak kaydediyor; görev sessizce hiç çalışmıyor (0x41303).
    /// Yani havuz kimliği seçeneği hiçbir koşulda çalışamazdı.
    /// </para>
    /// <para>
    /// LOCAL SERVICE canlı olarak doğrulandı: görev kaydoluyor, çalışıyor (sonuç 0x0) ve betik içinden
    /// okunan kimlik "NT AUTHORITY\Local Service". Yönetici yetkisi yoktur — amaç buydu.
    /// </para>
    /// </summary>
    internal const string LimitedUserId = @"NT AUTHORITY\LOCAL SERVICE";

    /// <summary>Kayıtlı bir görevin eyleminden "script" tipini geri türetir (liste ekranı için).</summary>
    internal static bool IsScriptAction(string? execute, string? arguments)
    {
        var command = execute ?? string.Empty;
        var argument = arguments ?? string.Empty;
        if (command.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase))
        {
            return argument.StartsWith("/c", StringComparison.OrdinalIgnoreCase);
        }

        return command.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase) &&
               argument.Contains("-File ", StringComparison.OrdinalIgnoreCase);
    }
}
