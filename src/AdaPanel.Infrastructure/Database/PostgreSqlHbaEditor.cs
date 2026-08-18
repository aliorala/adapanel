using System.Text.RegularExpressions;
using AdaPanel.Core.Common;

namespace AdaPanel.Infrastructure.Database;

/// <summary>Tek bir <c>pg_hba.conf</c> "host" satırı.</summary>
internal sealed record HbaEntry(string Database, string User, string Address);

/// <summary>
/// <c>pg_hba.conf</c> dosyasının panel tarafından yönetilen bölümünü kurar.
/// </summary>
/// <remarks>
/// <para>
/// PostgreSQL'de uzak erişimin karşılığı kullanıcı host'u değil, bu dosyadır: hangi
/// veritabanına, hangi rolle, hangi ağdan bağlanılabileceği burada yazar ve İLK EŞLEŞEN SATIR
/// KAZANIR.
/// </para>
/// <para>
/// Bütün dosyayı yeniden yazmak yerine iki işaretçi arasındaki blok yönetilir: yöneticinin elle
/// eklediği satırlar korunur, panelin bıraktığı satırlar da temizlenebilir. Blok her seferinde
/// KOMPLE değiştirilir; "kapat" demek satırların gerçekten yok olması demektir.
/// </para>
/// </remarks>
internal static partial class PostgreSqlHbaEditor
{
    public const string BeginMarker = "# >>> AdaPanel uzak veritabani erisimi - elle duzenlemeyin >>>";
    public const string EndMarker = "# <<< AdaPanel uzak veritabani erisimi <<<";

    /// <summary>
    /// Yönetilen bloğu yeni girdilerle değiştirir; girdi yoksa bloğu tamamen kaldırır.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Blok, dosyadaki İLK <c>host</c> ailesinden satırın ÖNÜNE konur. Sona eklemek görünüşte
    /// daha kibardır ama işe yaramaz: kurulumların çoğunda daha yukarıda geniş bir
    /// <c>host all all ... </c> satırı bulunur ve ilk eşleşen kazandığı için panelin satırı hiç
    /// okunmazdı — panel "açtım" derken erişim açılmamış olurdu. Panelin satırları veritabanı,
    /// rol ve ağ bakımından zaten dar olduğundan öne alınmaları başka bir kuralı genişletmez.
    /// </para>
    /// <para>
    /// Bloktan önce yer alan <c>local</c> satırları etkilenmez (onlar TCP'yi değil yerel soketi
    /// tanımlar).
    /// </para>
    /// </remarks>
    public static string Apply(string? content, IReadOnlyList<HbaEntry> entries, string authMethod)
    {
        var newline = (content ?? string.Empty).Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = StripManagedBlock((content ?? string.Empty).Replace("\r\n", "\n").Split('\n'));

        if (entries.Count == 0)
        {
            return string.Join(newline, lines).TrimEnd('\n', '\r') + newline;
        }

        var method = NormalizeAuthMethod(authMethod);
        var block = new List<string> { BeginMarker };
        foreach (var entry in entries)
        {
            block.Add(FormatEntry(entry, method));
        }

        block.Add(EndMarker);

        var insertAt = lines.FindIndex(IsHostRule);
        if (insertAt < 0)
        {
            insertAt = lines.Count;
        }

        lines.InsertRange(insertAt, block);
        return string.Join(newline, lines).TrimEnd('\n', '\r') + newline;
    }

    /// <summary>Yönetilen bloktaki adresleri okur (panel "şu an ne yazıyor" diye motora sorabilsin).</summary>
    public static IReadOnlyList<HbaEntry> Read(string? content)
    {
        var result = new List<HbaEntry>();
        var inside = false;

        foreach (var raw in (content ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith(BeginMarker, StringComparison.Ordinal))
            {
                inside = true;
                continue;
            }

            if (line.StartsWith(EndMarker, StringComparison.Ordinal))
            {
                inside = false;
                continue;
            }

            if (!inside || line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 4 && string.Equals(parts[0], "host", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new HbaEntry(parts[1], parts[2], parts[3]));
            }
        }

        return result;
    }

    /// <summary>
    /// Dosyanın kendi kullandığı kimlik doğrulama yöntemini bulur.
    /// </summary>
    /// <remarks>
    /// Sabit bir yöntem yazmak (örneğin <c>scram-sha-256</c>) kurulumun geri kalanıyla
    /// uyuşmayabilir: <c>md5</c> ile kurulmuş bir sunucuda kullanıcı parolası scram karşılığıyla
    /// saklanmadığı için bağlantı reddedilir ve panel "erişim verdim" derken kullanıcı
    /// giremezdi. Mevcut <c>host</c> satırlarındaki yöntem neyse o kullanılır.
    /// </remarks>
    public static string DetectAuthMethod(string? content)
    {
        foreach (var raw in (content ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || !IsHostRule(line))
            {
                continue;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 5 && IsSafeAuthMethod(parts[4]))
            {
                return parts[4].ToLowerInvariant();
            }
        }

        return "scram-sha-256";
    }

    private static string FormatEntry(HbaEntry entry, string authMethod)
    {
        // SATIR ENJEKSİYONU: veritabanı/rol adı bu dosyaya boşlukla ayrılmış bir alan olarak
        // yazılır. İçinde boşluk ya da satır sonu olan bir ad, tek bir alan olmaktan çıkıp
        // KENDİ KURALINI yazardı — örneğin bir satır sonu + "host all all 0.0.0.0/0 trust".
        // Adlar panelin kendi kuralıyla zaten sınırlı; burada bir kez daha doğrulanır çünkü bu
        // dosyaya yazan tek kapı burasıdır.
        EnsureIdentifier(entry.Database);
        EnsureIdentifier(entry.User);
        EnsureAddress(entry.Address);

        return $"host    {entry.Database}    {entry.User}    {entry.Address}    {authMethod}";
    }

    private static void EnsureIdentifier(string value)
    {
        if (!IdentifierPattern().IsMatch(value ?? string.Empty))
        {
            throw new AppException("database.remote_invalid_origin", 400, 400);
        }
    }

    private static void EnsureAddress(string value)
    {
        if (!AddressPattern().IsMatch(value ?? string.Empty))
        {
            throw new AppException("database.remote_invalid_origin", 400, 400);
        }
    }

    private static string NormalizeAuthMethod(string? value)
    {
        var method = (value ?? string.Empty).Trim().ToLowerInvariant();
        return IsSafeAuthMethod(method) ? method : "scram-sha-256";
    }

    /// <summary>
    /// Yazılmasına izin verilen yöntemler.
    /// </summary>
    /// <remarks>
    /// <c>trust</c> ve <c>peer</c> BİLEREK dışarıda: <c>trust</c>, uzak bir ağa parolasız erişim
    /// vermek demektir. Dosyadan okunan yöntem bunlardan biriyse (yerel satırlarda sık görülür)
    /// yok sayılır ve parolalı varsayılana düşülür.
    /// </remarks>
    private static bool IsSafeAuthMethod(string? value) =>
        (value ?? string.Empty).ToLowerInvariant() is "scram-sha-256" or "md5" or "password";

    private static List<string> StripManagedBlock(IEnumerable<string> lines)
    {
        var result = new List<string>();
        var inside = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(BeginMarker, StringComparison.Ordinal))
            {
                inside = true;
                continue;
            }

            if (trimmed.StartsWith(EndMarker, StringComparison.Ordinal))
            {
                inside = false;
                continue;
            }

            if (!inside)
            {
                result.Add(line);
            }
        }

        return result;
    }

    private static bool IsHostRule(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("host ", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("host\t", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("hostssl", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("hostnossl", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,63}$", RegexOptions.Compiled)]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex("^[0-9A-Fa-f.:]{1,45}/[0-9]{1,3}$", RegexOptions.Compiled)]
    private static partial Regex AddressPattern();
}
