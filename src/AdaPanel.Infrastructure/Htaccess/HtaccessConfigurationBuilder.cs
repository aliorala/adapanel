using System.Text;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Htaccess;

namespace AdaPanel.Infrastructure.Htaccess;

/// <summary>
/// Basamaklandırılmış <c>.htaccess</c> dosyalarını TEK bir çalıştırılabilir yapılandırmaya çevirir.
/// </summary>
/// <remarks>
/// <para>
/// <b>BİRLEŞTİRME KURALLARI Apache'nin kendi birleştiricisinden alındı</b>
/// (<c>mod_rewrite.c: config_perdir_merge</c> ve arkadaşları) — çünkü sezgisel olan yanlıştır:
/// </para>
/// <list type="bullet">
/// <item><b>mod_rewrite kuralları EKLENMEZ, DEĞİŞTİRİLİR.</b> Alt dizinde bir tek
/// <c>RewriteRule</c> varsa üst dizinin BÜTÜN kuralları düşer. Apache'nin varsayılanı budur;
/// eklemek için <c>RewriteOptions Inherit</c> gerekir (desteklenmiyor, bulgu üretilir).
/// Bunu "hepsini birleştir" diye yazmak, WordPress kökünün kurallarının alt dizindeki bir
/// eklentinin kurallarıyla çakışmasına yol açardı.</item>
/// <item>Alt dizinde HİÇ kural yoksa üst dizinin kuralları GEÇERLİDİR ve o kuralların dizin ön eki
/// (<c>VirtualPath</c>) üst dizinindir. <c>RewriteBase</c>'in var olma sebebi tam olarak budur.</item>
/// <item><c>Header</c> satırları BİRİKİR (mod_headers üst+alt birleştirir).</item>
/// <item><c>DirectoryIndex</c>, <c>Order/Allow/Deny</c>, <c>Require</c>, <c>Auth*</c> alt dizinde
/// tanımlıysa üstü tamamen EZER.</item>
/// <item><c>ErrorDocument</c>, <c>AddType</c>, <c>ExpiresByType</c>, <c>SetEnv</c> anahtar bazında
/// birleşir; alt dizin kazanır.</item>
/// <item><c>Options</c> işaretsiz yazıldığında mirası SIFIRLAR, <c>+</c>/<c>-</c> ile yazıldığında
/// mirası değiştirir.</item>
/// </list>
/// </remarks>
internal sealed class HtaccessConfigurationBuilder
{
    private readonly HtaccessRegexCache _desenler;
    private readonly HtaccessLimits _sinirlar;

    public HtaccessConfigurationBuilder(HtaccessRegexCache desenler, HtaccessLimits sinirlar)
    {
        _desenler = desenler;
        _sinirlar = sinirlar;
    }

    public HtaccessConfiguration Build(
        IReadOnlyList<HtaccessParseResult> dosyalar, HtaccessScope kapsam, string siteRoot)
    {
        var yapilandirma = new HtaccessConfiguration();

        foreach (var dosya in dosyalar)
        {
            yapilandirma.Diagnostics.AddRange(dosya.Diagnostics);

            if (!dosya.Usable)
            {
                // Ölümcül hatalı dosya UYGULANMAZ. Apache 500 dönerdi; motor kararı çağırana
                // bırakır ama dosyanın kurallarını da uygulamaz — yarım uygulanmış bir
                // yapılandırma en tehlikeli sonuçtur (kapalı sanılan dizin açık kalır).
                yapilandirma.HasFatal = true;
                continue;
            }

            yapilandirma.AppliedFiles.Add(dosya.FilePath);
            DosyayiIsle(dosya, kapsam, siteRoot, yapilandirma);
        }

        return yapilandirma;
    }

    private void DosyayiIsle(
        HtaccessParseResult dosya, HtaccessScope kapsam, string siteRoot, HtaccessConfiguration hedef)
    {
        var bulgular = hedef.Diagnostics;
        var etkinDirektifler = dosya.Directives
            .Where(d => BloklarEslesiyor(d, kapsam, dosya.FilePath, bulgular))
            .ToList();

        // RewriteBase dosyanın TAMAMI için geçerlidir; kuralların önünde olmak zorunda değildir.
        string? rewriteBase = null;
        foreach (var d in etkinDirektifler.Where(d => d.Name.Equals("RewriteBase", StringComparison.OrdinalIgnoreCase)))
        {
            if (d.Arguments.Count != 1)
            {
                Hata(bulgular, dosya.FilePath, d, "RewriteBase tam olarak bir argüman ister (ör. 'RewriteBase /').");
                continue;
            }

            rewriteBase = d.Argument(0);
            if (!rewriteBase.StartsWith("/", StringComparison.Ordinal))
            {
                rewriteBase = "/" + rewriteBase;
            }

            if (!rewriteBase.EndsWith("/", StringComparison.Ordinal))
            {
                rewriteBase += "/";
            }
        }

        var dosyaKurallari = new List<HtaccessRewriteRule>();
        var bekleyenKosullar = new List<HtaccessRewriteCond>();
        var dosyaErisimi = new HtaccessAccessRules();
        var dosyaDizinIndeksi = (List<string>?)null;
        var dosyaAuth = new HtaccessAuthSettings();

        foreach (var d in etkinDirektifler)
        {
            switch (d.Name.ToLowerInvariant())
            {
                case "rewriteengine":
                    hedef.RewriteEngine = AcikMi(d.Argument(0));
                    break;

                case "rewritebase":
                    break; // yukarıda işlendi

                case "rewritecond":
                    var kosul = KosulKur(d, dosya.FilePath, bulgular);
                    if (kosul is not null)
                    {
                        bekleyenKosullar.Add(kosul);
                    }

                    break;

                case "rewriterule":
                    var kural = KuralKur(d, dosya, rewriteBase, bekleyenKosullar, bulgular);
                    bekleyenKosullar = [];
                    if (kural is not null)
                    {
                        dosyaKurallari.Add(kural);
                    }

                    break;

                case "redirect":
                case "redirectpermanent":
                case "redirecttemp":
                case "redirectmatch":
                    YonlendirmeKur(d, dosya.FilePath, hedef, bulgular);
                    break;

                case "directoryindex":
                    dosyaDizinIndeksi = DizinIndeksi(d);
                    break;

                case "errordocument":
                    HataBelgesi(d, dosya.FilePath, hedef, bulgular);
                    break;

                case "options":
                    Secenekler(d, dosya.FilePath, hedef, bulgular);
                    break;

                case "order":
                    dosyaErisimi.Order = string.Join(string.Empty, d.Arguments);
                    break;

                case "allow":
                    dosyaErisimi.Allow.AddRange(ErisimAdresleri(d, dosya.FilePath, bulgular));
                    break;

                case "deny":
                    dosyaErisimi.Deny.AddRange(ErisimAdresleri(d, dosya.FilePath, bulgular));
                    break;

                case "require":
                    if (d.Arguments.Count == 0)
                    {
                        Hata(bulgular, dosya.FilePath, d, "Require en az bir argüman ister.");
                        break;
                    }

                    dosyaErisimi.Require.Add(new HtaccessRequireEntry
                    {
                        Verb = d.Argument(0),
                        Arguments = d.Arguments.Skip(1).ToList(),
                        Line = d.Line,
                        File = dosya.FilePath
                    });
                    break;

                case "header":
                    Baslik(d, dosya.FilePath, hedef, bulgular);
                    break;

                case "expiresactive":
                    hedef.ExpiresActive = AcikMi(d.Argument(0));
                    break;

                case "expiresbytype":
                    if (d.Arguments.Count < 2)
                    {
                        Hata(bulgular, dosya.FilePath, d, "ExpiresByType 'MIME-tipi \"access plus 1 month\"' biçiminde iki argüman ister.");
                        break;
                    }

                    var girdi = SureCozumle(string.Join(" ", d.Arguments.Skip(1)), dosya.FilePath, d, bulgular);
                    if (girdi is not null)
                    {
                        hedef.ExpiresByType[d.Argument(0)] = girdi;
                    }

                    break;

                case "expiresdefault":
                    var varsayilan = SureCozumle(string.Join(" ", d.Arguments), dosya.FilePath, d, bulgular);
                    if (varsayilan is not null)
                    {
                        hedef.ExpiresDefault = varsayilan;
                    }

                    break;

                case "authtype":
                    dosyaAuth.Type = d.Argument(0);
                    dosyaAuth.Line = d.Line;
                    dosyaAuth.File = dosya.FilePath;
                    if (!dosyaAuth.Type.Equals("Basic", StringComparison.OrdinalIgnoreCase))
                    {
                        bulgular.Add(HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Unsupported,
                            HtaccessDiagnosticCodes.UnsupportedDirective, dosya.FilePath, d.Line, "AuthType",
                            $"AuthType {dosyaAuth.Type} desteklenmiyor; yalnızca Basic uygulanıyor. Dizin KORUMASIZ kalır."));
                    }

                    break;

                case "authname":
                    dosyaAuth.Realm = string.Join(" ", d.Arguments);
                    break;

                case "authuserfile":
                    dosyaAuth.UserFile = ParolaDosyasi(d, dosya, siteRoot, bulgular);
                    dosyaAuth.Line = d.Line;
                    dosyaAuth.File = dosya.FilePath;
                    break;

                case "addtype":
                    Mime(d, dosya.FilePath, hedef, bulgular);
                    break;

                case "adddefaultcharset":
                case "defaultcharset":
                    var karakterKumesi = d.Argument(0);
                    hedef.DefaultCharset = karakterKumesi.Equals("Off", StringComparison.OrdinalIgnoreCase)
                        ? string.Empty
                        : karakterKumesi.Equals("On", StringComparison.OrdinalIgnoreCase) ? "iso-8859-1" : karakterKumesi;
                    if (d.Name.Equals("DefaultCharset", StringComparison.OrdinalIgnoreCase))
                    {
                        bulgular.Add(HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Warning,
                            HtaccessDiagnosticCodes.ApacheWouldReject, dosya.FilePath, d.Line, "DefaultCharset",
                            "Apache'de 'DefaultCharset' diye bir direktif YOKTUR ve Apache 500 döner. Doğrusu 'AddDefaultCharset'tir; motor ikisini de kabul ediyor."));
                    }

                    break;

                case "setenv":
                    if (d.Arguments.Count == 0)
                    {
                        Hata(bulgular, dosya.FilePath, d, "SetEnv en az bir değişken adı ister.");
                        break;
                    }

                    hedef.Env[d.Argument(0)] = string.Join(" ", d.Arguments.Skip(1));
                    break;
            }
        }

        if (bekleyenKosullar.Count > 0)
        {
            bulgular.Add(HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Warning,
                HtaccessDiagnosticCodes.CondWithoutRule, dosya.FilePath, bekleyenKosullar[0].Line, "RewriteCond",
                $"{bekleyenKosullar.Count} adet RewriteCond'un ardından RewriteRule gelmiyor; bu koşullar HİÇBİR ŞEY yapmaz."));
        }

        // mod_rewrite: alt dizinde kural VARSA üstünkiler tamamen düşer.
        if (dosyaKurallari.Count > 0)
        {
            hedef.Rules.Clear();
            hedef.Rules.AddRange(dosyaKurallari);
        }

        if (!dosyaErisimi.IsEmpty)
        {
            hedef.Access = dosyaErisimi;
        }

        if (dosyaDizinIndeksi is not null)
        {
            hedef.DirectoryIndex = dosyaDizinIndeksi;
        }

        if (!string.IsNullOrEmpty(dosyaAuth.Type) || !string.IsNullOrEmpty(dosyaAuth.UserFile) ||
            !string.IsNullOrEmpty(dosyaAuth.Realm))
        {
            if (!string.IsNullOrEmpty(dosyaAuth.Type))
            {
                hedef.Auth.Type = dosyaAuth.Type;
            }

            if (!string.IsNullOrEmpty(dosyaAuth.Realm))
            {
                hedef.Auth.Realm = dosyaAuth.Realm;
            }

            if (!string.IsNullOrEmpty(dosyaAuth.UserFile))
            {
                hedef.Auth.UserFile = dosyaAuth.UserFile;
            }

            hedef.Auth.File = dosyaAuth.File;
            hedef.Auth.Line = dosyaAuth.Line;
        }
    }

    // ---------------------------------------------------------------- bloklar

    /// <summary>Direktifi çevreleyen bütün blokların koşulu sağlanıyor mu?</summary>
    private bool BloklarEslesiyor(
        HtaccessDirective direktif, HtaccessScope kapsam, string dosyaYolu, List<HtaccessDiagnostic> bulgular)
    {
        foreach (var blok in direktif.Sections)
        {
            if (!HtaccessDirectiveCatalog.IsSupportedSection(blok.Name))
            {
                return false;
            }

            var eslesti = blok.Name.ToLowerInvariant() switch
            {
                "ifmodule" => kapsam.LoadedModules.Contains(ModulAdi(blok.Argument)),
                "files" => AdEslesiyor(blok.Argument, kapsam.FileName, dosyaYolu, blok.Line, bulgular),
                "filesmatch" => DesenEslesiyor(blok.Argument, kapsam.FileName, dosyaYolu, blok.Line, bulgular),
                "directory" => AdEslesiyor(blok.Argument, kapsam.PhysicalDirectory, dosyaYolu, blok.Line, bulgular),
                "directorymatch" => DesenEslesiyor(blok.Argument, kapsam.PhysicalDirectory, dosyaYolu, blok.Line, bulgular),
                "limit" => YontemVar(blok.Argument, kapsam.Method),
                "limitexcept" => !YontemVar(blok.Argument, kapsam.Method),
                _ => false
            };

            if (blok.Negated)
            {
                eslesti = !eslesti;
            }

            if (!eslesti)
            {
                return false;
            }
        }

        return true;
    }

    private static string ModulAdi(string arg) => arg.Trim().Trim('"', '\'');

    private static bool YontemVar(string arg, string method) =>
        arg.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Any(y => y.Equals(method, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// <c>&lt;Files&gt;</c> / <c>&lt;Directory&gt;</c> argümanı: joker karakter ya da
    /// <c>~ desen</c> biçiminde düzenli ifade.
    /// </summary>
    private bool AdEslesiyor(string arg, string deger, string dosyaYolu, int satir, List<HtaccessDiagnostic> bulgular)
    {
        var temiz = arg.Trim();
        if (temiz.StartsWith("~", StringComparison.Ordinal))
        {
            return DesenEslesiyor(temiz[1..].Trim(), deger, dosyaYolu, satir, bulgular);
        }

        if (string.IsNullOrEmpty(deger))
        {
            return false;
        }

        return DesenEslesiyor(JokerdenDesene(temiz), deger, dosyaYolu, satir, bulgular);
    }

    private bool DesenEslesiyor(string desen, string deger, string dosyaYolu, int satir, List<HtaccessDiagnostic> bulgular)
    {
        if (string.IsNullOrEmpty(deger))
        {
            return false;
        }

        var temiz = desen.Trim().Trim('"');
        var regex = _desenler.TryGet(temiz, ignoreCase: true, _sinirlar.RegexTimeout, out var hata);
        if (regex is null)
        {
            bulgular.Add(HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Error,
                HtaccessDiagnosticCodes.InvalidRegex, dosyaYolu, satir, "Blok",
                $"Blok deseni derlenemedi ({hata}); blok içindeki direktifler UYGULANMADI."));
            return false;
        }

        var eslesme = HtaccessRegexCache.TryMatch(regex, deger, out var zamanAsimi);
        if (zamanAsimi)
        {
            bulgular.Add(HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Error,
                HtaccessDiagnosticCodes.RegexTimeout, dosyaYolu, satir, "Blok",
                "Blok deseni zaman aşımına uğradı; blok içindeki direktifler UYGULANMADI."));
            return false;
        }

        return eslesme?.Success == true;
    }

    /// <summary>Apache'nin joker eşleşmesini (<c>*</c>, <c>?</c>, <c>[...]</c>) düzenli ifadeye çevirir.</summary>
    internal static string JokerdenDesene(string joker)
    {
        var desen = new StringBuilder("^");
        var i = 0;
        while (i < joker.Length)
        {
            var k = joker[i];
            switch (k)
            {
                case '*':
                    desen.Append(".*");
                    break;
                case '?':
                    desen.Append('.');
                    break;
                case '[':
                    var kapanis = joker.IndexOf(']', i + 1);
                    if (kapanis < 0)
                    {
                        desen.Append("\\[");
                    }
                    else
                    {
                        desen.Append(joker[i..(kapanis + 1)]);
                        i = kapanis;
                    }

                    break;
                default:
                    desen.Append(Regex.Escape(k.ToString()));
                    break;
            }

            i++;
        }

        return desen.Append('$').ToString();
    }

    // ---------------------------------------------------------------- mod_rewrite

    private HtaccessRewriteCond? KosulKur(
        HtaccessDirective d, string dosyaYolu, List<HtaccessDiagnostic> bulgular)
    {
        if (d.Arguments.Count < 2)
        {
            Hata(bulgular, dosyaYolu, d, "RewriteCond en az iki argüman ister: 'RewriteCond test-dizgesi koşul-deseni [bayraklar]'.");
            return null;
        }

        var test = d.Argument(0);
        var ham = d.Argument(1);
        var bayrakMetni = d.Arguments.Count > 2 && HtaccessFlagParser.LooksLikeFlags(d.Argument(2)) ? d.Argument(2) : null;
        var (noCase, orNext) = HtaccessFlagParser.ParseCondFlags(bayrakMetni, dosyaYolu, d.Line, bulgular);

        var desen = ham;
        var olumsuz = false;
        if (desen.StartsWith("!", StringComparison.Ordinal))
        {
            olumsuz = true;
            desen = desen[1..];
        }

        var tip = HtaccessCondKind.Regex;
        switch (desen)
        {
            case "-f": tip = HtaccessCondKind.FileExists; desen = string.Empty; break;
            case "-d": tip = HtaccessCondKind.DirectoryExists; desen = string.Empty; break;
            case "-l" or "-L" or "-h": tip = HtaccessCondKind.SymbolicLink; desen = string.Empty; break;
            case "-s": tip = HtaccessCondKind.NonEmptyFile; desen = string.Empty; break;
            case "-x":
                // Windows'ta "çalıştırılabilir" bir izin biti yoktur. Sessizce -f gibi davranmak
                // yanlış olurdu: kural her zaman eşleşirdi.
                tip = HtaccessCondKind.AlwaysFalse;
                bulgular.Add(HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Unsupported,
                    HtaccessDiagnosticCodes.UnsupportedCondition, dosyaYolu, d.Line, "RewriteCond",
                    "'-x' (çalıştırılabilir dosya) Windows'ta ölçülemez; koşul HER ZAMAN BAŞARISIZ sayılır."));
                desen = string.Empty;
                break;
            case "-F" or "-U":
                tip = HtaccessCondKind.AlwaysFalse;
                bulgular.Add(HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Unsupported,
                    HtaccessDiagnosticCodes.UnsupportedCondition, dosyaYolu, d.Line, "RewriteCond",
                    $"'{desen}' alt-istek (subrequest) çalıştırmayı gerektirir; koşul HER ZAMAN BAŞARISIZ sayılır."));
                desen = string.Empty;
                break;
            default:
                if (desen.StartsWith(">=", StringComparison.Ordinal)) { tip = HtaccessCondKind.LexicalGreaterEqual; desen = desen[2..]; }
                else if (desen.StartsWith("<=", StringComparison.Ordinal)) { tip = HtaccessCondKind.LexicalLessEqual; desen = desen[2..]; }
                else if (desen.StartsWith("-eq", StringComparison.Ordinal)) { tip = HtaccessCondKind.IntegerEqual; desen = desen[3..].Trim(); }
                else if (desen.StartsWith("-ne", StringComparison.Ordinal)) { tip = HtaccessCondKind.IntegerNotEqual; desen = desen[3..].Trim(); }
                else if (desen.StartsWith("-lt", StringComparison.Ordinal)) { tip = HtaccessCondKind.IntegerLess; desen = desen[3..].Trim(); }
                else if (desen.StartsWith("-gt", StringComparison.Ordinal)) { tip = HtaccessCondKind.IntegerGreater; desen = desen[3..].Trim(); }
                else if (desen.StartsWith("-le", StringComparison.Ordinal)) { tip = HtaccessCondKind.IntegerLessEqual; desen = desen[3..].Trim(); }
                else if (desen.StartsWith("-ge", StringComparison.Ordinal)) { tip = HtaccessCondKind.IntegerGreaterEqual; desen = desen[3..].Trim(); }
                else if (desen.StartsWith("=", StringComparison.Ordinal))
                {
                    tip = HtaccessCondKind.LexicalEqual;
                    desen = desen[1..];

                    // Apache'nin BİREBİR aynı özel durumu (mod_rewrite.c, cmd_rewritecond):
                    //   case '=': ... if (*++a2 == '"' && a2[1] == '"' && !a2[2]) a2 += 2;
                    // Yani YALNIZCA tam olarak `=""` yazımı "boş dizgeye eşit" demektir. Symfony'nin
                    // resmî .htaccess dosyası buna dayanır (`RewriteCond %{ENV:REDIRECT_STATUS} =""`);
                    // bu iki karakteri harfi harfine karşılaştırsaydık koşul hiç tutmaz ve
                    // index.php yönlendirmesi SESSİZCE ölürdü.
                    if (desen == "\"\"")
                    {
                        desen = string.Empty;
                    }
                }
                else if (desen.StartsWith("<", StringComparison.Ordinal)) { tip = HtaccessCondKind.LexicalLess; desen = desen[1..]; }
                else if (desen.StartsWith(">", StringComparison.Ordinal)) { tip = HtaccessCondKind.LexicalGreater; desen = desen[1..]; }
                else if (desen.StartsWith("-strmatch", StringComparison.OrdinalIgnoreCase) ||
                         test.StartsWith("expr", StringComparison.OrdinalIgnoreCase))
                {
                    tip = HtaccessCondKind.AlwaysFalse;
                    bulgular.Add(HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Unsupported,
                        HtaccessDiagnosticCodes.UnsupportedCondition, dosyaYolu, d.Line, "RewriteCond",
                        "Apache ifade dili (expr / -strmatch) desteklenmiyor; koşul HER ZAMAN BAŞARISIZ sayılır."));
                }

                break;
        }

        if (tip == HtaccessCondKind.Regex)
        {
            DesenRiskiniBildir(desen, dosyaYolu, d.Line, "RewriteCond", bulgular);
        }

        return new HtaccessRewriteCond
        {
            TestString = test,
            RawPattern = ham,
            Pattern = desen,
            Kind = tip,
            Negated = olumsuz,
            NoCase = noCase,
            OrNext = orNext,
            Line = d.Line,
            File = dosyaYolu
        };
    }

    private HtaccessRewriteRule? KuralKur(
        HtaccessDirective d,
        HtaccessParseResult dosya,
        string? rewriteBase,
        List<HtaccessRewriteCond> kosullar,
        List<HtaccessDiagnostic> bulgular)
    {
        if (d.Arguments.Count < 2)
        {
            Hata(bulgular, dosya.FilePath, d, "RewriteRule en az iki argüman ister: 'RewriteRule desen hedef [bayraklar]'.");
            return null;
        }

        var desen = d.Argument(0);
        var hedef = d.Argument(1);
        var bayrakMetni = d.Arguments.Count > 2 && HtaccessFlagParser.LooksLikeFlags(d.Argument(2)) ? d.Argument(2) : null;

        if (d.Arguments.Count > 2 && bayrakMetni is null)
        {
            bulgular.Add(HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Warning,
                HtaccessDiagnosticCodes.TooManyArguments, dosya.FilePath, d.Line, "RewriteRule",
                "RewriteRule'un üçüncü argümanı bayrak öbeği ([...]) değil; YOK SAYILDI. Hedefte boşluk varsa tırnak içine alın."));
        }

        var bayraklar = HtaccessFlagParser.ParseRuleFlags(bayrakMetni, dosya.FilePath, d.Line, bulgular);
        if (bayraklar.Disabled)
        {
            return null;
        }

        var olumsuz = false;
        if (desen.StartsWith("!", StringComparison.Ordinal))
        {
            olumsuz = true;
            desen = desen[1..];
        }

        DesenRiskiniBildir(desen, dosya.FilePath, d.Line, "RewriteRule", bulgular);

        return new HtaccessRewriteRule
        {
            Pattern = desen,
            Negated = olumsuz,
            Substitution = hedef,
            Flags = bayraklar,
            Conditions = kosullar,
            VirtualPath = dosya.VirtualPath,
            RewriteBase = rewriteBase,
            Line = d.Line,
            File = dosya.FilePath
        };
    }

    private static void DesenRiskiniBildir(
        string desen, string dosya, int satir, string direktif, List<HtaccessDiagnostic> bulgular)
    {
        if (!HtaccessRegexCache.LooksCatastrophic(desen))
        {
            return;
        }

        bulgular.Add(HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Warning,
            HtaccessDiagnosticCodes.RiskyRegex, dosya, satir, direktif,
            $"'{desen}' deseninde iç içe niceleyici var. Böyle desenler uzun girdilerde ÜSTEL sürede çalışabilir (ReDoS). Kural çalışıyor ama zaman aşımına takılırsa istek yeniden yazılmadan geçer; deseni sadeleştirin."));
    }

    // ---------------------------------------------------------------- diğer direktifler

    private static void YonlendirmeKur(
        HtaccessDirective d, string dosyaYolu, HtaccessConfiguration hedef, List<HtaccessDiagnostic> bulgular)
    {
        var eslesmeli = d.Name.Equals("RedirectMatch", StringComparison.OrdinalIgnoreCase);
        var argumanlar = d.Arguments.ToList();
        var kod = d.Name.ToLowerInvariant() switch
        {
            "redirectpermanent" => 301,
            "redirecttemp" => 302,
            _ => 302
        };

        if (argumanlar.Count > 0 && DurumKodu(argumanlar[0], out var acikKod))
        {
            kod = acikKod;
            argumanlar.RemoveAt(0);
        }

        if (argumanlar.Count < 2)
        {
            Hata(bulgular, dosyaYolu, d, $"{d.Name} kaynak ve hedef olmak üzere iki argüman ister.");
            return;
        }

        hedef.Redirects.Add(new HtaccessRedirectRule
        {
            StatusCode = kod,
            IsMatch = eslesmeli,
            Source = argumanlar[0],
            Target = argumanlar[1],
            Line = d.Line,
            File = dosyaYolu
        });
    }

    private static bool DurumKodu(string deger, out int kod)
    {
        kod = deger.ToLowerInvariant() switch
        {
            "permanent" => 301,
            "temp" => 302,
            "seeother" => 303,
            "gone" => 410,
            _ => int.TryParse(deger, out var sayi) && sayi is >= 300 and <= 599 ? sayi : 0
        };

        return kod > 0;
    }

    private static List<string> DizinIndeksi(HtaccessDirective d) =>
        d.Arguments.Count == 1 && d.Argument(0).Equals("disabled", StringComparison.OrdinalIgnoreCase)
            ? []
            : d.Arguments.ToList();

    private static void HataBelgesi(
        HtaccessDirective d, string dosyaYolu, HtaccessConfiguration hedef, List<HtaccessDiagnostic> bulgular)
    {
        if (d.Arguments.Count < 2 || !int.TryParse(d.Argument(0), out var kod))
        {
            Hata(bulgular, dosyaYolu, d, "ErrorDocument 'ErrorDocument 404 /404.html' biçiminde durum kodu + hedef ister.");
            return;
        }

        var deger = string.Join(" ", d.Arguments.Skip(1));

        // Yol güvenliği: "/../../ProgramData/gizli.html" gibi bir hedef site kökünün dışını
        // gösterirdi. Yüzde kodlaması da çözülür, yoksa "%2e%2e" ile aynı kaçış yapılır.
        if (deger.StartsWith("/", StringComparison.Ordinal) && YolKacisiVar(deger))
        {
            bulgular.Add(HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Error,
                HtaccessDiagnosticCodes.PathEscapesSiteRoot, dosyaYolu, d.Line, "ErrorDocument",
                $"'{deger}' site kökünün dışına çıkıyor; ErrorDocument UYGULANMADI."));
            return;
        }

        hedef.ErrorDocuments[kod] = deger;
    }

    /// <summary>Yüzde kodlaması çözülmüş hâlde <c>..</c> bileşeni var mı?</summary>
    internal static bool YolKacisiVar(string yol)
    {
        var cozulmus = yol;
        for (var i = 0; i < 3; i++)
        {
            var yeni = Uri.UnescapeDataString(cozulmus);
            if (yeni == cozulmus)
            {
                break;
            }

            cozulmus = yeni;
        }

        return cozulmus.Replace('\\', '/')
            .Split('/')
            .Any(p => p == "..");
    }

    private static void Secenekler(
        HtaccessDirective d, string dosyaYolu, HtaccessConfiguration hedef, List<HtaccessDiagnostic> bulgular)
    {
        var bilinen = new[] { "Indexes", "FollowSymLinks", "SymLinksIfOwnerMatch", "MultiViews", "ExecCGI", "Includes", "IncludesNOEXEC" };
        var isaretsiz = d.Arguments.Any(a => a.Length > 0 && a[0] is not ('+' or '-'));

        if (isaretsiz)
        {
            // İşaretsiz Options mirası SIFIRLAR (Apache davranışı).
            foreach (var ad in bilinen)
            {
                hedef.Options[ad] = false;
            }
        }

        foreach (var ham in d.Arguments)
        {
            if (ham.Length == 0)
            {
                continue;
            }

            var acik = ham[0] != '-';
            var ad = ham[0] is '+' or '-' ? ham[1..] : ham;

            if (ad.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                // Apache: "All" MultiViews DIŞINDAKİ her şeydir.
                foreach (var bilinenAd in bilinen)
                {
                    hedef.Options[bilinenAd] = acik && !bilinenAd.Equals("MultiViews", StringComparison.OrdinalIgnoreCase);
                }

                continue;
            }

            if (ad.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var bilinenAd in bilinen)
                {
                    hedef.Options[bilinenAd] = false;
                }

                continue;
            }

            var eslesen = bilinen.FirstOrDefault(b => b.Equals(ad, StringComparison.OrdinalIgnoreCase));
            if (eslesen is null)
            {
                bulgular.Add(HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Unsupported,
                    HtaccessDiagnosticCodes.UnsupportedDirective, dosyaYolu, d.Line, "Options",
                    $"'Options {ham}' tanınmıyor; YOK SAYILDI."));
                continue;
            }

            hedef.Options[eslesen] = acik;

            if (eslesen.Equals("MultiViews", StringComparison.OrdinalIgnoreCase) && acik)
            {
                bulgular.Add(HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Unsupported,
                    HtaccessDiagnosticCodes.UnsupportedDirective, dosyaYolu, d.Line, "Options",
                    "'+MultiViews' uygulanmıyor: içerik pazarlığı (content negotiation) yapılmıyor. '-MultiViews' ile davranış Apache ile aynıdır, '+MultiViews' ile DEĞİLDİR."));
            }

            if (eslesen.Equals("ExecCGI", StringComparison.OrdinalIgnoreCase) && acik)
            {
                bulgular.Add(HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Unsupported,
                    HtaccessDiagnosticCodes.UnsupportedDirective, dosyaYolu, d.Line, "Options",
                    "'+ExecCGI' uygulanmıyor: CGI çalıştırma IIS'te işleyici olarak tanımlanır."));
            }
        }
    }

    private static void Baslik(
        HtaccessDirective d, string dosyaYolu, HtaccessConfiguration hedef, List<HtaccessDiagnostic> bulgular)
    {
        var argumanlar = d.Arguments.ToList();
        if (argumanlar.Count == 0)
        {
            Hata(bulgular, dosyaYolu, d, "Header en az bir işlem ister.");
            return;
        }

        var herZaman = false;
        if (argumanlar[0].Equals("always", StringComparison.OrdinalIgnoreCase) ||
            argumanlar[0].Equals("onsuccess", StringComparison.OrdinalIgnoreCase))
        {
            herZaman = argumanlar[0].Equals("always", StringComparison.OrdinalIgnoreCase);
            argumanlar.RemoveAt(0);
        }

        if (argumanlar.Count == 0)
        {
            Hata(bulgular, dosyaYolu, d, "Header işlemi eksik (set/add/append/unset).");
            return;
        }

        var islem = argumanlar[0].ToLowerInvariant();
        HtaccessHeaderOperation tip;
        switch (islem)
        {
            case "set": tip = HtaccessHeaderOperation.Set; break;
            case "add": tip = HtaccessHeaderOperation.Add; break;
            case "append": tip = HtaccessHeaderOperation.Append; break;
            case "unset": tip = HtaccessHeaderOperation.Unset; break;
            default:
                bulgular.Add(HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Unsupported,
                    HtaccessDiagnosticCodes.UnsupportedDirective, dosyaYolu, d.Line, "Header",
                    $"'Header {islem}' uygulanmıyor; yalnızca set/add/append/unset destekleniyor."));
                return;
        }

        if (argumanlar.Count < 2)
        {
            Hata(bulgular, dosyaYolu, d, "Header başlık adı ister.");
            return;
        }

        var ad = argumanlar[1];
        var deger = argumanlar.Count > 2 ? argumanlar[2] : string.Empty;

        // Koşullu Header (env=/expr=) uygulanmıyor; koşulu yok sayıp başlığı HER İSTEĞE eklemek
        // güvenlik başlıklarında yanlış sonuç verirdi.
        var kosul = argumanlar.Skip(3).FirstOrDefault(a =>
            a.StartsWith("env=", StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith("expr=", StringComparison.OrdinalIgnoreCase));
        if (kosul is not null)
        {
            bulgular.Add(HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Unsupported,
                HtaccessDiagnosticCodes.UnsupportedDirective, dosyaYolu, d.Line, "Header",
                $"Koşullu Header ('{kosul}') uygulanmıyor; başlık EKLENMEDİ. Koşulu yok sayıp başlığı her isteğe eklemek daha tehlikeli olurdu."));
            return;
        }

        // Değeri içinde geçen %{...} genişletmesi mod_headers'a aittir ve BURADA yapılmaz;
        // istek bağlamı gerektirir ve başlık değerine istek verisi enjekte etme yüzeyi açar.
        // Contains(string, StringComparison) .NET Core 2.1 ile geldi; IndexOf'un ayni asiri yuklemesi
        // net48'de DE var ve AYNI karsilastirmayi yapar (bkz. HtaccessPortableApi gerekcesi).
        if (deger.IndexOf("%{", StringComparison.Ordinal) >= 0)
        {
            bulgular.Add(HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Unsupported,
                HtaccessDiagnosticCodes.UnsupportedDirective, dosyaYolu, d.Line, "Header",
                $"Header değerindeki '%{{...}}' genişletmesi uygulanmıyor; başlık DEĞERİ OLDUĞU GİBİ yazılır ({deger})."));
        }

        hedef.Headers.RemoveAll(h =>
            tip != HtaccessHeaderOperation.Add &&
            h.Name.Equals(ad, StringComparison.OrdinalIgnoreCase) &&
            h.Operation == tip);

        hedef.Headers.Add(new HtaccessHeaderAction
        {
            Operation = tip,
            Name = ad,
            Value = deger,
            Always = herZaman
        });
    }

    private static HtaccessExpiresEntry? SureCozumle(
        string ifade, string dosyaYolu, HtaccessDirective d, List<HtaccessDiagnostic> bulgular)
    {
        var temiz = ifade.Trim().Trim('"');
        if (temiz.Length == 0)
        {
            Hata(bulgular, dosyaYolu, d, "Süre ifadesi boş.");
            return null;
        }

        // Kısa biçim: "A604800" (access) / "M604800" (modification).
        if ((temiz[0] is 'A' or 'M') && long.TryParse(temiz[1..], out var kisaSaniye))
        {
            return new HtaccessExpiresEntry { FromAccessTime = temiz[0] == 'A', Seconds = kisaSaniye };
        }

        var parcalar = temiz.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parcalar.Length < 2)
        {
            Hata(bulgular, dosyaYolu, d, $"'{temiz}' süre ifadesi anlaşılamadı; 'access plus 1 month' biçimi bekleniyor.");
            return null;
        }

        var temelAdi = parcalar[0].ToLowerInvariant();
        if (temelAdi is not ("access" or "now" or "modification"))
        {
            Hata(bulgular, dosyaYolu, d, $"'{parcalar[0]}' süre temeli tanınmıyor; access / now / modification bekleniyor.");
            return null;
        }

        long toplam = 0;
        var i = parcalar[1].Equals("plus", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        while (i + 1 < parcalar.Length + 1 && i < parcalar.Length)
        {
            if (!long.TryParse(parcalar[i], out var sayi) || i + 1 >= parcalar.Length)
            {
                Hata(bulgular, dosyaYolu, d, $"'{temiz}' süre ifadesi anlaşılamadı (sayı + birim çifti bekleniyor).");
                return null;
            }

            var birim = parcalar[i + 1].TrimEnd('s').ToLowerInvariant();
            var carpan = birim switch
            {
                "second" => 1L,
                "minute" => 60L,
                "hour" => 3600L,
                "day" => 86400L,
                "week" => 604800L,
                "month" => 2592000L,
                "year" => 31536000L,
                _ => -1L
            };

            if (carpan < 0)
            {
                Hata(bulgular, dosyaYolu, d, $"'{parcalar[i + 1]}' süre birimi tanınmıyor.");
                return null;
            }

            toplam += sayi * carpan;
            i += 2;
        }

        return new HtaccessExpiresEntry { FromAccessTime = temelAdi != "modification", Seconds = toplam };
    }

    private static void Mime(
        HtaccessDirective d, string dosyaYolu, HtaccessConfiguration hedef, List<HtaccessDiagnostic> bulgular)
    {
        if (d.Arguments.Count < 2)
        {
            Hata(bulgular, dosyaYolu, d, "AddType 'AddType MIME-tipi .uzanti' biçiminde en az iki argüman ister.");
            return;
        }

        var mime = d.Argument(0);
        foreach (var uzanti in d.Arguments.Skip(1))
        {
            var temiz = uzanti.StartsWith(".", StringComparison.Ordinal) ? uzanti : "." + uzanti;
            hedef.MimeTypes[temiz] = mime;
        }
    }

    private static List<string> ErisimAdresleri(
        HtaccessDirective d, string dosyaYolu, List<HtaccessDiagnostic> bulgular)
    {
        var argumanlar = d.Arguments.ToList();
        if (argumanlar.Count > 0 && argumanlar[0].Equals("from", StringComparison.OrdinalIgnoreCase))
        {
            argumanlar.RemoveAt(0);
        }

        foreach (var a in argumanlar.Where(a =>
                     !a.Equals("all", StringComparison.OrdinalIgnoreCase) &&
                     !a.StartsWith("env=", StringComparison.OrdinalIgnoreCase) &&
                     !a.Any(HtaccessPortableApi.AsciiRakamMi)))
        {
            bulgular.Add(HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Unsupported,
                HtaccessDiagnosticCodes.UnsupportedDirective, dosyaYolu, d.Line, d.Name,
                $"'{d.Name} from {a}' alan adına göre eşleşme ister (ters DNS); uygulanmıyor ve HİÇBİR ZAMAN eşleşmez. IP ya da CIDR yazın."));
        }

        return argumanlar;
    }

    /// <summary>
    /// <c>AuthUserFile</c> yolunu doğrular. <b>Site kökünün dışına çıkamaz.</b>
    /// </summary>
    /// <remarks>
    /// Apache mutlak yola izin verir; motor VERMEZ. Aksi hâlde bir kiracı
    /// <c>AuthUserFile C:\ProgramData\AdaPanel\...</c> yazıp panelin kendi dosyalarını okutabilir
    /// ya da başka bir kiracının parola dosyasını hedefleyebilirdi. Kapsama sorusu
    /// <see cref="PathContainment"/> ile sorulur.
    /// </remarks>
    private static string ParolaDosyasi(
        HtaccessDirective d, HtaccessParseResult dosya, string siteRoot, List<HtaccessDiagnostic> bulgular)
    {
        var ham = string.Join(" ", d.Arguments).Trim();
        if (ham.Length == 0)
        {
            Hata(bulgular, dosya.FilePath, d, "AuthUserFile bir dosya yolu ister.");
            return string.Empty;
        }

        // Apache yolu eğik çizgiyle yazar; Windows'ta ters bölüye çevrilir.
        var yerel = ham.Replace('/', Path.DirectorySeparatorChar);
        // Path.IsPathFullyQualified DEGIL: net48'de yok. PathContainment'taki tasinabilir karsiligi
        // net8 tarafinda BCL uyesiyle karsilastirmali olarak DENETLENIYOR (PathContainmentTests) --
        // ve zaten bu satirin hemen ardindaki kapsam denetimi de ayni sinifa dayaniyor.
        var mutlak = PathContainment.PortableIsPathFullyQualified(yerel) ? yerel : Path.Combine(siteRoot, yerel.TrimStart(Path.DirectorySeparatorChar));

        var normal = PathContainment.Normalize(mutlak);
        if (normal is null || !PathContainment.IsInsideRoot(siteRoot, normal))
        {
            bulgular.Add(HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Error,
                HtaccessDiagnosticCodes.PathEscapesSiteRoot, dosya.FilePath, d.Line, "AuthUserFile",
                $"'{ham}' site kökünün ({siteRoot}) dışında. Parola dosyası site kökünün ALTINDA olmalı; koruma KURULMADI."));
            return string.Empty;
        }

        return normal;
    }

    private static bool AcikMi(string deger) =>
        deger.Equals("On", StringComparison.OrdinalIgnoreCase) ||
        deger.Equals("1", StringComparison.Ordinal) ||
        deger.Equals("true", StringComparison.OrdinalIgnoreCase);

    private static void Hata(List<HtaccessDiagnostic> bulgular, string dosya, HtaccessDirective d, string mesaj) =>
        bulgular.Add(HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Error,
            HtaccessDiagnosticCodes.MissingArgument, dosya, d.Line, d.Name, mesaj));
}
