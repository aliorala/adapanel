using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Htaccess;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Htaccess.Htpasswd;

namespace AdaPanel.Infrastructure.Htaccess;

/// <summary>
/// <see cref="IHtaccessEngine"/>'in uygulaması: basamaklandırma → birleştirme → erişim denetimi →
/// mod_rewrite.
/// </summary>
/// <remarks>
/// <para>
/// <b>HİÇBİR ÜYE İSTİSNA FIRLATMAZ.</b> Her giriş noktası en dışta yakalar ve "başarısız oldum"
/// bilgisini DÖNEN DEĞERDE bildirir. Bu kod barındırılan her sitenin her isteğinde çalışır;
/// motorun bir kusuru siteyi düşüremez.
/// </para>
/// <para>
/// <b>APACHE'DEN BİLİNÇLİ SAPMALAR</b> (kodda ve panelde açıkça yazılı):
/// </para>
/// <list type="number">
/// <item><b>Erişim denetimi yeniden yazmadan ÖNCE</b> ve isteğin ÖZGÜN yolu üzerinden çalışır.
/// Apache önce URL'i yeniden yazar, sonra HEDEF dizinin izinlerine bakar; bu yüzden
/// <c>/gizli/</c> için konan bir koruma, her şeyi <c>index.php</c>'ye yönlendiren bir kural
/// yüzünden Apache'de DEVRE DIŞI kalabilir. Motor bu durumda daha KORUYUCU davranır.</item>
/// <item><b><c>&lt;Directory&gt;</c> uygulanır</b> — Apache <c>.htaccess</c> içinde reddeder.</item>
/// <item><b>Ölümcül söz dizimi hatası olan dosya UYGULANMAZ</b> ama motor 500 üretmez;
/// durumu bildirir ve kararı barındırma katmanına bırakır.</item>
/// <item><b><c>AuthUserFile</c> site kökünün ALTINDA olmak zorundadır</b> — Apache herhangi bir
/// mutlak yola izin verir. Paylaşımlı barındırmada bu, panelin kendi dosyalarını okutmanın
/// yoluydu.</item>
/// <item><b>Dizin isteğinde (<c>/blog/</c>) dosya adı BOŞTUR</b>, dolayısıyla
/// <c>&lt;Files&gt;</c>/<c>&lt;FilesMatch&gt;</c> blokları eşleşmez. Apache dizin indeksini
/// (<c>index.php</c>) çözdükten sonra o ada göre eşleştirir; motor indeks çözümünü barındırma
/// katmanına bıraktığı için bu adımı yapamaz.</item>
/// <item><b>Desen zaman aşımı BÜTÜN değerlendirmeyi durdurur</b>, tek kuralı değil. Yarım
/// uygulanmış bir kural kümesi, hiç uygulanmamış olandan tehlikelidir.</item>
/// </list>
/// <para>
/// <b>Sınıf iş parçacığı güvenlidir</b> ve tekil (singleton) olarak kaydedilmek üzere
/// tasarlandı: ayrıştırma önbelleği ve desen önbelleği eşzamanlı sözlüklerdir, birleştirme ve
/// çalıştırma ise istek başına yeni nesnelerle yapılır.
/// </para>
/// </remarks>
public sealed class HtaccessEngine : IHtaccessEngine
{
    private readonly IHtaccessFileSystem _dosyaSistemi;
    private readonly HtaccessCascadeLoader _yukleyici;
    private readonly HtaccessRegexCache _desenler;

    public HtaccessEngine()
        : this(HtaccessFileSystem.Instance, HtaccessLimits.Default)
    {
    }

    internal HtaccessEngine(IHtaccessFileSystem dosyaSistemi, HtaccessLimits sinirlar)
    {
        _dosyaSistemi = dosyaSistemi;
        _yukleyici = new HtaccessCascadeLoader(dosyaSistemi);
        _desenler = new HtaccessRegexCache(sinirlar.MaxCachedPatterns);
    }

    public HtaccessEvaluationResult Evaluate(HtaccessRequestContext request)
    {
        try
        {
            return Degerlendir(request);
        }
        catch (Exception istisna)
        {
            // Buraya düşmek bir HATADIR ve gizlenmez; ama isteği bozmaz. Motorun sözleşmesi
            // "başarısızlığı DÖNDÜR"dür, "istisna fırlat" değil.
            return new HtaccessEvaluationResult
            {
                Outcome = HtaccessOutcome.Failed,
                EngineFailed = true,
                FailureCode = HtaccessDiagnosticCodes.FileUnreadable,
                Diagnostics =
                [
                    HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Fatal, HtaccessDiagnosticCodes.FileUnreadable,
                        request?.SiteRootPath ?? string.Empty, 0, string.Empty,
                        $"Motor beklenmedik bir hatayla durdu: {istisna.GetType().Name}: {istisna.Message}. İstek .htaccess UYGULANMADAN sürdürülmeli.")
                ]
            };
        }
    }

    public HtaccessInspectionResult Inspect(string siteRootPath, string relativeDirectory = "")
    {
        try
        {
            var sinirlar = HtaccessLimits.Default;
            var bulgular = new List<HtaccessDiagnostic>();
            var dosyalar = _yukleyici.Load(siteRootPath, relativeDirectory, sinirlar, bulgular);

            var sanalYol = SanalYol(relativeDirectory);
            var kapsam = new HtaccessScope
            {
                FileName = string.Empty,
                RequestPath = sanalYol,
                PhysicalDirectory = FizikselDizin(siteRootPath, relativeDirectory),
                Method = "GET"
            };

            var yapilandirma = new HtaccessConfigurationBuilder(_desenler, sinirlar)
                .Build(dosyalar, kapsam, PathContainment.Normalize(siteRootPath) ?? siteRootPath);
            bulgular.AddRange(yapilandirma.Diagnostics);

            return new HtaccessInspectionResult
            {
                SiteRootPath = siteRootPath,
                VirtualPath = sanalYol,
                Files = dosyalar,
                Diagnostics = Tekille(bulgular),
                RewriteEngineEnabled = yapilandirma.RewriteEngine == true,
                RewriteRuleCount = yapilandirma.Rules.Count,
                Effective = DurgunSonuc(yapilandirma)
            };
        }
        catch (Exception istisna)
        {
            return new HtaccessInspectionResult
            {
                SiteRootPath = siteRootPath,
                Diagnostics =
                [
                    HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Fatal, HtaccessDiagnosticCodes.FileUnreadable,
                        siteRootPath, 0, string.Empty,
                        $"Motor beklenmedik bir hatayla durdu: {istisna.GetType().Name}: {istisna.Message}")
                ]
            };
        }
    }

    public HtaccessParseResult Validate(string content, string virtualPath = "/", HtaccessLimits? limits = null)
    {
        try
        {
            return HtaccessParser.Parse(content, string.Empty, virtualPath, limits ?? HtaccessLimits.Default);
        }
        catch (Exception istisna)
        {
            return new HtaccessParseResult
            {
                VirtualPath = virtualPath,
                Diagnostics =
                [
                    HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Fatal, HtaccessDiagnosticCodes.FileUnreadable,
                        string.Empty, 0, string.Empty,
                        $"Ayrıştırıcı beklenmedik bir hatayla durdu: {istisna.GetType().Name}: {istisna.Message}")
                ]
            };
        }
    }

    public HtaccessAuthResult Authenticate(
        HtaccessAuthChallenge challenge, string userName, string password, HtaccessLimits? limits = null)
    {
        try
        {
            var sinirlar = limits ?? HtaccessLimits.Default;
            if (challenge is null || string.IsNullOrEmpty(challenge.UserFilePath))
            {
                return new HtaccessAuthResult
                {
                    Succeeded = false,
                    FailureCode = HtaccessDiagnosticCodes.AuthIncomplete,
                    FailureReason = "Parola dosyası tanımlı değil."
                };
            }

            var sonuc = HtpasswdFile.Verify(_dosyaSistemi, challenge.UserFilePath, userName, password, sinirlar);
            if (!sonuc.Succeeded)
            {
                return new HtaccessAuthResult
                {
                    Succeeded = false,
                    FailureCode = sonuc.FailureCode,
                    FailureReason = sonuc.FailureReason,
                    Diagnostics = sonuc.Diagnostics
                };
            }

            // Kullanıcı doğrulandı; "Require user" listesi varsa AYRICA orada olmalı.
            if (!challenge.AnyValidUser && challenge.AllowedUsers.Count > 0 &&
                !challenge.AllowedUsers.Contains(userName, StringComparer.Ordinal))
            {
                return new HtaccessAuthResult
                {
                    Succeeded = false,
                    FailureCode = HtaccessDiagnosticCodes.AuthIncomplete,
                    FailureReason = "Kullanıcı doğrulandı ama 'Require user' listesinde değil."
                };
            }

            return new HtaccessAuthResult { Succeeded = true };
        }
        catch (Exception istisna)
        {
            return new HtaccessAuthResult
            {
                Succeeded = false,
                FailureCode = HtaccessDiagnosticCodes.AuthFileUnreadable,
                FailureReason = $"Kimlik doğrulama beklenmedik bir hatayla durdu: {istisna.GetType().Name}: {istisna.Message}"
            };
        }
    }

    public void InvalidateCache(string? siteRootPath = null) => _yukleyici.Invalidate(siteRootPath);

    // ---------------------------------------------------------------- iç akış

    private HtaccessEvaluationResult Degerlendir(HtaccessRequestContext request)
    {
        var sinirlar = request.Limits ?? HtaccessLimits.Default;
        var bulgular = new List<HtaccessDiagnostic>();

        var kok = PathContainment.Normalize(request.SiteRootPath);
        if (kok is null)
        {
            return Basarisiz(bulgular, HtaccessDiagnosticCodes.PathEscapesSiteRoot,
                $"Site kökü ('{request.SiteRootPath}') geçerli bir mutlak yol değil; .htaccess değerlendirilmedi.");
        }

        var istekYolu = IstekYolu(request.RequestPath);
        var (goreliDizin, dosyaAdi) = YoluAyir(istekYolu);

        var dosyalar = _yukleyici.Load(kok, goreliDizin, sinirlar, bulgular);
        var kapsam = new HtaccessScope
        {
            FileName = dosyaAdi,
            RequestPath = istekYolu,
            PhysicalDirectory = FizikselDizin(kok, goreliDizin),
            Method = string.IsNullOrEmpty(request.Method) ? "GET" : request.Method
        };

        var yapilandirma = new HtaccessConfigurationBuilder(_desenler, sinirlar).Build(dosyalar, kapsam, kok);
        bulgular.AddRange(yapilandirma.Diagnostics);

        var sorgu = Sorgu(yapilandirma);

        // 1) ERİŞİM DENETİMİ — yeniden yazmadan ÖNCE (sınıf yorumundaki 1. sapma).
        var karar = HtaccessAccessControl.Evaluate(
            yapilandirma.Access, request, yapilandirma.Env, yapilandirma.Auth.IsConfigured, bulgular);

        if (!karar.Allowed)
        {
            return Temel(yapilandirma, bulgular, sorgu) with
            {
                Outcome = karar.NeedsAuthentication ? HtaccessOutcome.AuthenticationRequired : HtaccessOutcome.AccessDenied,
                StatusCode = karar.NeedsAuthentication ? 401 : 403,
                RewrittenPath = istekYolu,
                RewrittenQuery = request.QueryString
            };
        }

        // 2) mod_alias + mod_rewrite
        var kosucu = new HtaccessRewriteRunner(_desenler, _dosyaSistemi, sinirlar, bulgular);
        var kosum = kosucu.Run(yapilandirma, request with { SiteRootPath = kok, RequestPath = istekYolu });

        var temel = Temel(yapilandirma, bulgular, sorgu) with
        {
            Outcome = kosum.Outcome,
            RewrittenPath = kosum.Path,
            RewrittenQuery = kosum.Query,
            StatusCode = kosum.StatusCode,
            RedirectLocation = kosum.Location,
            EngineFailed = kosum.Failed,
            FailureCode = kosum.FailureCode,
            Budget = kosum.Budget
        };

        return temel;
    }

    /// <summary>Sonucun istek-bağımsız kısmı: başlıklar, dizin indeksi, hata belgeleri, MIME...</summary>
    private static HtaccessEvaluationResult Temel(
        HtaccessConfiguration yapilandirma, List<HtaccessDiagnostic> bulgular, HtaccessAuthChallenge? sorgu) => new()
    {
        Headers = yapilandirma.Headers.ToList(),
        DirectoryIndex = yapilandirma.DirectoryIndex ?? [],
        ErrorDocuments = new Dictionary<int, string>(yapilandirma.ErrorDocuments),
        Options = yapilandirma.ToOptionsState(),
        MimeTypes = new Dictionary<string, string>(yapilandirma.MimeTypes, StringComparer.OrdinalIgnoreCase),
        DefaultCharset = yapilandirma.DefaultCharset,
        Expires = yapilandirma.ToExpiresPolicy(),
        Auth = sorgu,
        Diagnostics = Tekille(bulgular),
        AppliedFiles = yapilandirma.AppliedFiles.ToList()
    };

    private static HtaccessEvaluationResult DurgunSonuc(HtaccessConfiguration yapilandirma) =>
        Temel(yapilandirma, [], Sorgu(yapilandirma));

    private static HtaccessAuthChallenge? Sorgu(HtaccessConfiguration yapilandirma)
    {
        if (!yapilandirma.Auth.IsConfigured)
        {
            return null;
        }

        var herkes = yapilandirma.Access.Require
            .Any(r => r.Verb.Equals("valid-user", StringComparison.OrdinalIgnoreCase));
        var kullanicilar = yapilandirma.Access.Require
            .Where(r => r.Verb.Equals("user", StringComparison.OrdinalIgnoreCase))
            .SelectMany(r => r.Arguments)
            .ToList();

        return new HtaccessAuthChallenge
        {
            Realm = string.IsNullOrEmpty(yapilandirma.Auth.Realm) ? "Restricted" : yapilandirma.Auth.Realm,
            UserFilePath = yapilandirma.Auth.UserFile,
            AnyValidUser = herkes,
            AllowedUsers = kullanicilar
        };
    }

    private static HtaccessEvaluationResult Basarisiz(
        List<HtaccessDiagnostic> bulgular, string kod, string mesaj)
    {
        bulgular.Add(HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Fatal, kod, string.Empty, 0, string.Empty, mesaj));
        return new HtaccessEvaluationResult
        {
            Outcome = HtaccessOutcome.Failed,
            EngineFailed = true,
            FailureCode = kod,
            Diagnostics = bulgular
        };
    }

    /// <summary>Aynı bulgunun basamaklandırma boyunca tekrar etmesini engeller.</summary>
    private static List<HtaccessDiagnostic> Tekille(IEnumerable<HtaccessDiagnostic> bulgular)
    {
        var gorulen = new HashSet<string>(StringComparer.Ordinal);
        var sonuc = new List<HtaccessDiagnostic>();
        foreach (var bulgu in bulgular)
        {
            if (gorulen.Add($"{bulgu.Code}|{bulgu.File}|{bulgu.Line}|{bulgu.Message}"))
            {
                sonuc.Add(bulgu);
            }
        }

        return sonuc;
    }

    internal static string IstekYolu(string? yol)
    {
        if (string.IsNullOrWhiteSpace(yol))
        {
            return "/";
        }

        var temiz = yol.Replace('\\', '/');
        return temiz.StartsWith('/') ? temiz : "/" + temiz;
    }

    /// <summary>İstek yolunu (dizin, dosya adı) çiftine ayırır.</summary>
    internal static (string Directory, string FileName) YoluAyir(string istekYolu)
    {
        if (istekYolu.EndsWith('/'))
        {
            return (istekYolu.Trim('/'), string.Empty);
        }

        var sonEgik = istekYolu.LastIndexOf('/');
        var dizin = sonEgik <= 0 ? string.Empty : istekYolu[1..sonEgik];
        var ad = istekYolu[(sonEgik + 1)..];
        return (dizin, ad);
    }

    private static string SanalYol(string goreliDizin)
    {
        var temiz = (goreliDizin ?? string.Empty).Replace('\\', '/').Trim('/');
        return temiz.Length == 0 ? "/" : "/" + temiz + "/";
    }

    private static string FizikselDizin(string siteRoot, string goreliDizin)
    {
        var temiz = (goreliDizin ?? string.Empty).Replace('/', Path.DirectorySeparatorChar).Trim(Path.DirectorySeparatorChar);
        try
        {
            return temiz.Length == 0 ? siteRoot : Path.Combine(siteRoot, temiz);
        }
        catch (ArgumentException)
        {
            return siteRoot;
        }
    }
}
