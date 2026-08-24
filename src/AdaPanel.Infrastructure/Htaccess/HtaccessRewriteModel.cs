using AdaPanel.Core.Dtos.Htaccess;

namespace AdaPanel.Infrastructure.Htaccess;

/// <summary>Bir <c>RewriteRule</c> bayrağı kümesi.</summary>
/// <remarks>
/// <b>Tanınmayan bayrak KURALI DEVRE DIŞI BIRAKIR.</b> Sebebi: bayraklar kuralın ANLAMINI
/// değiştirir. <c>[P]</c> (proxy) bir kuralı yeniden yazmadan bambaşka bir sunucuya taşır;
/// bayrağı yok sayıp kuralı çalıştırmak, kullanıcının istediğinden farklı ve sessiz bir davranış
/// üretirdi. "Bu kuralı uygulayamıyorum" demek her zaman daha doğrudur.
/// </remarks>
internal sealed class HtaccessRuleFlags
{
    public bool Last { get; set; }
    public bool End { get; set; }
    public bool NoCase { get; set; }
    public bool QsAppend { get; set; }
    public bool QsDiscard { get; set; }
    public bool Forbidden { get; set; }
    public bool Gone { get; set; }
    public bool NoEscape { get; set; }
    public bool Next { get; set; }
    public bool Chain { get; set; }

    /// <summary><c>[R]</c> → 302, <c>[R=301]</c> → 301. 0 = yönlendirme yok.</summary>
    public int RedirectStatus { get; set; }

    /// <summary><c>[S=3]</c> — eşleşince sonraki N kuralı atla.</summary>
    public int Skip { get; set; }

    /// <summary><c>[E=AD:DEGER]</c> — ortam değişkeni ataması.</summary>
    public List<(string Name, string Value)> Env { get; } = [];

    /// <summary>Tanınmayan bayrak yüzünden kural devre dışı mı?</summary>
    public bool Disabled { get; set; }
}

/// <summary><c>RewriteCond</c> karşılaştırma tipi.</summary>
internal enum HtaccessCondKind
{
    Regex,
    FileExists,       // -f
    DirectoryExists,  // -d
    SymbolicLink,     // -l
    NonEmptyFile,     // -s
    Executable,       // -x  (Windows'ta anlamsız — bulgu üretilir)
    LexicalEqual,     // =
    LexicalLess,      // <
    LexicalGreater,   // >
    LexicalLessEqual, // <=
    LexicalGreaterEqual, // >=
    IntegerEqual,     // -eq
    IntegerNotEqual,  // -ne
    IntegerLess,      // -lt
    IntegerGreater,   // -gt
    IntegerLessEqual, // -le
    IntegerGreaterEqual, // -ge
    AlwaysFalse       // desteklenmeyen karşılaştırma
}

/// <summary>Tek bir <c>RewriteCond</c>.</summary>
internal sealed class HtaccessRewriteCond
{
    public string TestString { get; init; } = string.Empty;
    public string RawPattern { get; init; } = string.Empty;
    public string Pattern { get; init; } = string.Empty;
    public HtaccessCondKind Kind { get; init; }
    public bool Negated { get; init; }
    public bool NoCase { get; init; }

    /// <summary><c>[OR]</c> — bir SONRAKİ koşulla VEYA bağı kurar.</summary>
    public bool OrNext { get; init; }

    public int Line { get; init; }
    public string File { get; init; } = string.Empty;
}

/// <summary>Tek bir <c>RewriteRule</c> ve kendisinden ÖNCE gelen koşulları.</summary>
internal sealed class HtaccessRewriteRule
{
    public string Pattern { get; init; } = string.Empty;

    /// <summary>Desenin başındaki <c>!</c> — "eşleşMEZse".</summary>
    public bool Negated { get; init; }

    public string Substitution { get; init; } = string.Empty;

    public HtaccessRuleFlags Flags { get; init; } = new();

    public IReadOnlyList<HtaccessRewriteCond> Conditions { get; init; } = [];

    /// <summary>Kuralın geldiği dosyanın URL ön eki (<c>/</c>, <c>/blog/</c>).</summary>
    public string VirtualPath { get; init; } = "/";

    /// <summary>O dosyada geçerli olan <c>RewriteBase</c>. Yoksa <c>null</c>.</summary>
    public string? RewriteBase { get; init; }

    public int Line { get; init; }
    public string File { get; init; } = string.Empty;
}

/// <summary><c>Redirect</c> / <c>RedirectMatch</c> (mod_alias) kaydı.</summary>
internal sealed class HtaccessRedirectRule
{
    public int StatusCode { get; init; } = 302;

    /// <summary><c>RedirectMatch</c> ise düzenli ifade, <c>Redirect</c> ise düz URL ön eki.</summary>
    public bool IsMatch { get; init; }

    public string Source { get; init; } = string.Empty;

    public string Target { get; init; } = string.Empty;

    public int Line { get; init; }
    public string File { get; init; } = string.Empty;
}

/// <summary><c>RewriteRule</c>/<c>RewriteCond</c> bayraklarının ayrıştırıcısı.</summary>
internal static class HtaccessFlagParser
{
    /// <summary>
    /// Tanınan ama UYGULANMAYAN kural bayrakları. Bunlardan biri varsa kural devre dışı kalır.
    /// </summary>
    private static readonly Dictionary<string, string> UygulanmayanBayrak = new(StringComparer.OrdinalIgnoreCase)
    {
        ["P"] = "vekil (proxy) yönlendirmesi; isteği başka bir sunucuya taşır. Panelin ters vekil ayarlarını kullanın.",
        ["proxy"] = "vekil (proxy) yönlendirmesi; isteği başka bir sunucuya taşır. Panelin ters vekil ayarlarını kullanın.",
        ["PT"] = "isteği başka bir Apache modülünün eşleyicisine devreder; IIS'te karşılığı yok.",
        ["passthrough"] = "isteği başka bir Apache modülünün eşleyicisine devreder; IIS'te karşılığı yok.",
        ["T"] = "MIME tipi zorlaması uygulanmıyor; AddType kullanın.",
        ["type"] = "MIME tipi zorlaması uygulanmıyor; AddType kullanın.",
        ["H"] = "işleyici (handler) zorlaması IIS'te sunucu düzeyinde yönetilir.",
        ["handler"] = "işleyici (handler) zorlaması IIS'te sunucu düzeyinde yönetilir.",
        ["CO"] = "çerez yazma uygulanmıyor.",
        ["cookie"] = "çerez yazma uygulanmıyor.",
        ["B"] = "geri referansların yeniden kaçırılması uygulanmıyor; sonuç Apache'den farklı olurdu.",
        ["backrefescaping"] = "geri referansların yeniden kaçırılması uygulanmıyor; sonuç Apache'den farklı olurdu.",
        ["BNP"] = "geri referans kaçış ayarı uygulanmıyor.",
        ["BCTLS"] = "geri referans kaçış ayarı uygulanmıyor.",
        ["DPI"] = "PATH_INFO ayıklama uygulanmıyor.",
        ["discardpath"] = "PATH_INFO ayıklama uygulanmıyor.",
        ["QSL"] = "sorgu dizesinin son kural tarafından belirlenmesi uygulanmıyor.",
        ["qslast"] = "sorgu dizesinin son kural tarafından belirlenmesi uygulanmıyor.",
        ["MB"] = "çok bayt eşleşme ayarı uygulanmıyor."
    };

    /// <summary>Anlamı olan ama motorda karşılığı OLMAYAN, zararsız bayraklar.</summary>
    private static readonly HashSet<string> ZararsizBayrak = new(StringComparer.OrdinalIgnoreCase)
    {
        // Motor alt-istek (subrequest) çalıştırmadığı için NS'in kısıtladığı durum hiç oluşmaz.
        "NS", "nosubreq",
        // Vary başlığı üretilmiyor; NV'nin engellediği şey zaten yapılmıyor.
        "NV", "novary"
    };

    /// <summary><c>[L,NC,R=301]</c> biçimindeki bayrak öbeğini ayrıştırır.</summary>
    public static HtaccessRuleFlags ParseRuleFlags(
        string? raw, string file, int line, List<HtaccessDiagnostic> bulgular)
    {
        var bayraklar = new HtaccessRuleFlags();
        foreach (var parca in BayrakParcalari(raw))
        {
            var (ad, deger) = Ayir(parca);
            switch (ad.ToLowerInvariant())
            {
                case "l":
                case "last":
                    bayraklar.Last = true;
                    break;
                case "end":
                    bayraklar.End = true;
                    bayraklar.Last = true;
                    break;
                case "nc":
                case "nocase":
                    bayraklar.NoCase = true;
                    break;
                case "qsa":
                case "qsappend":
                    bayraklar.QsAppend = true;
                    break;
                case "qsd":
                case "qsdiscard":
                    bayraklar.QsDiscard = true;
                    break;
                case "f":
                case "forbidden":
                    bayraklar.Forbidden = true;
                    break;
                case "g":
                case "gone":
                    bayraklar.Gone = true;
                    break;
                case "ne":
                case "noescape":
                    bayraklar.NoEscape = true;
                    break;
                case "n":
                case "next":
                    bayraklar.Next = true;
                    break;
                case "c":
                case "chain":
                    bayraklar.Chain = true;
                    break;
                case "r":
                case "redirect":
                    bayraklar.RedirectStatus = YonlendirmeKodu(deger, file, line, bulgular);
                    break;
                case "s":
                case "skip":
                    bayraklar.Skip = int.TryParse(deger, out var atla) && atla > 0 ? atla : 1;
                    break;
                case "e":
                case "env":
                    OrtamDegiskeni(deger, bayraklar, file, line, bulgular);
                    break;
                default:
                    if (ZararsizBayrak.Contains(ad))
                    {
                        break;
                    }

                    if (UygulanmayanBayrak.TryGetValue(ad, out var sebep))
                    {
                        bayraklar.Disabled = true;
                        bulgular.Add(HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Unsupported,
                            HtaccessDiagnosticCodes.UnsupportedFlag, file, line, "RewriteRule",
                            $"[{parca}] bayrağı uygulanmıyor: {sebep} KURAL BÜTÜNÜYLE DEVRE DIŞI bırakıldı — bayrağı yok sayıp kuralı çalıştırmak sessizce yanlış davranmak olurdu."));
                        break;
                    }

                    bayraklar.Disabled = true;
                    bulgular.Add(HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Unsupported,
                        HtaccessDiagnosticCodes.UnsupportedFlag, file, line, "RewriteRule",
                        $"[{parca}] tanınmayan bir bayrak; KURAL DEVRE DIŞI bırakıldı. Yazım hatası olabilir."));
                    break;
            }
        }

        return bayraklar;
    }

    /// <summary><c>RewriteCond</c> bayrakları: yalnızca <c>NC</c>, <c>OR</c> ve zararsız olanlar.</summary>
    public static (bool NoCase, bool OrNext) ParseCondFlags(
        string? raw, string file, int line, List<HtaccessDiagnostic> bulgular)
    {
        var noCase = false;
        var orNext = false;

        foreach (var parca in BayrakParcalari(raw))
        {
            var (ad, _) = Ayir(parca);
            switch (ad.ToLowerInvariant())
            {
                case "nc":
                case "nocase":
                    noCase = true;
                    break;
                case "or":
                case "ornext":
                    orNext = true;
                    break;
                default:
                    if (ZararsizBayrak.Contains(ad))
                    {
                        break;
                    }

                    bulgular.Add(HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Unsupported,
                        HtaccessDiagnosticCodes.UnsupportedFlag, file, line, "RewriteCond",
                        $"[{parca}] RewriteCond için tanınmayan bir bayrak; YOK SAYILDI."));
                    break;
            }
        }

        return (noCase, orNext);
    }

    private static IEnumerable<string> BayrakParcalari(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            yield break;
        }

        var metin = raw.Trim();
        if (!metin.StartsWith("[", StringComparison.Ordinal) || !metin.EndsWith("]", StringComparison.Ordinal))
        {
            yield break;
        }

        foreach (var parca in metin[1..^1].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var kirpik = parca.Trim();
            if (kirpik.Length > 0)
            {
                yield return kirpik;
            }
        }
    }

    /// <summary>Bir bayrak öbeği gibi görünüyor mu (<c>[...]</c>)?</summary>
    public static bool LooksLikeFlags(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().StartsWith("[", StringComparison.Ordinal) && value.Trim().EndsWith("]", StringComparison.Ordinal);

    private static (string Ad, string Deger) Ayir(string parca)
    {
        var esittir = parca.IndexOf('=');
        return esittir < 0
            ? (parca, string.Empty)
            : (parca[..esittir], parca[(esittir + 1)..]);
    }

    private static int YonlendirmeKodu(string deger, string file, int line, List<HtaccessDiagnostic> bulgular)
    {
        if (string.IsNullOrWhiteSpace(deger))
        {
            // Apache'nin varsayılanı 302'dir.
            return 302;
        }

        var isim = deger.Trim().ToLowerInvariant();
        var kod = isim switch
        {
            "permanent" => 301,
            "temp" => 302,
            "seeother" => 303,
            _ => int.TryParse(isim, out var sayi) ? sayi : 0
        };

        if (kod is >= 300 and <= 399)
        {
            return kod;
        }

        // Apache 300-399 dışındaki kodları da kabul eder ve bunu bir HATA yanıtı olarak üretir
        // (ör. [R=404]). Motor bunu desteklemiyor; sessizce 302'ye düşmek yanlış olurdu.
        bulgular.Add(HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Unsupported,
            HtaccessDiagnosticCodes.UnsupportedFlag, file, line, "RewriteRule",
            $"[R={deger}] uygulanmıyor: yalnızca 300-399 arası yönlendirme kodları destekleniyor. Hata kodu üretmek için [F] (403) ya da [G] (410) kullanın."));
        return -1;
    }

    private static void OrtamDegiskeni(
        string deger, HtaccessRuleFlags bayraklar, string file, int line, List<HtaccessDiagnostic> bulgular)
    {
        var ikiNokta = deger.IndexOf(':');
        if (ikiNokta <= 0)
        {
            bulgular.Add(HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Error,
                HtaccessDiagnosticCodes.InvalidArgument, file, line, "RewriteRule",
                $"[E={deger}] biçimi hatalı; 'E=AD:DEGER' bekleniyor."));
            return;
        }

        bayraklar.Env.Add((deger[..ikiNokta], deger[(ikiNokta + 1)..]));
    }
}
