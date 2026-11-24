using System.Diagnostics;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Htaccess;

namespace AdaPanel.Infrastructure.Htaccess;

/// <summary>Kural kümesinin çalıştırılmasının sonucu.</summary>
internal sealed class HtaccessRunOutcome
{
    public HtaccessOutcome Outcome { get; set; } = HtaccessOutcome.None;
    public string Path { get; set; } = "/";
    public string Query { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public string Location { get; set; } = string.Empty;
    public bool Failed { get; set; }
    public string FailureCode { get; set; } = string.Empty;
    public Dictionary<string, string> Env { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HtaccessBudgetReport Budget { get; set; } = new();
}

/// <summary>
/// mod_rewrite'ın çalışma zamanı: kural döngüsü, koşullar, bayraklar, geri referanslar.
/// </summary>
/// <remarks>
/// <para>
/// <b>Apache'nin dizin-içi (per-directory) döngüsü taklit edilir:</b> kural kümesi baştan sona
/// işletilir; URL değiştiyse Apache bir İÇ YÖNLENDİRME yapar ve kümeyi BAŞTAN çalıştırır.
/// <c>[L]</c> yalnızca o turu bitirir, <c>[END]</c> bütün işlemi bitirir. Bu döngü doğal olarak
/// SONSUZDUR (<c>RewriteRule ^ index.php [L]</c> kendi çıktısına da uyar) ve Apache bunu
/// <c>LimitInternalRecursion</c> ile keser — burada <see cref="HtaccessLimits.MaxInternalRedirects"/>.
/// </para>
/// <para>
/// <b>BÜTÇE HER ADIMDA ÖLÇÜLÜR.</b> Kural sayısı, tur sayısı ve DUVAR SAATİ birlikte sınırlanır:
/// tek tek hızlı olan yüzlerce eşleşme toplamda yavaş olabilir ve bu kod her istekte çalışır.
/// Bütçe aşılınca istisna atılmaz; sonuç <see cref="HtaccessRunOutcome.Failed"/> ile döner.
/// </para>
/// </remarks>
internal sealed class HtaccessRewriteRunner
{
    private readonly HtaccessRegexCache _desenler;
    private readonly IHtaccessFileSystem _dosyaSistemi;
    private readonly HtaccessLimits _sinirlar;
    private readonly List<HtaccessDiagnostic> _bulgular;

    private int _kuralSayaci;
    private int _turSayaci;
    private int _desenSayaci;

    public HtaccessRewriteRunner(
        HtaccessRegexCache desenler,
        IHtaccessFileSystem dosyaSistemi,
        HtaccessLimits sinirlar,
        List<HtaccessDiagnostic> bulgular)
    {
        _desenler = desenler;
        _dosyaSistemi = dosyaSistemi;
        _sinirlar = sinirlar;
        _bulgular = bulgular;
    }

    public HtaccessRunOutcome Run(HtaccessConfiguration yapilandirma, HtaccessRequestContext istek)
    {
        var kronometre = Stopwatch.StartNew();
        var durum = new HtaccessRewriteState
        {
            Request = istek,
            CurrentPath = string.IsNullOrEmpty(istek.RequestPath) ? "/" : istek.RequestPath,
            CurrentQuery = istek.QueryString
        };

        // COZUMLEME (var (ad, deger)) YAZILMIYOR: KeyValuePair<,>.Deconstruct .NET Core 2.0 ile geldi,
        // net48'de YOK. Key/Value okumak iki hedefte de ayni.
        foreach (var giris in yapilandirma.Env)
        {
            durum.Env[giris.Key] = giris.Value;
        }

        var sonuc = new HtaccessRunOutcome { Path = durum.CurrentPath, Query = durum.CurrentQuery };

        // mod_alias (Redirect/RedirectMatch) mod_rewrite'tan ÖNCE değerlendirilir; Apache'de de
        // mod_alias URL eşleme aşamasında çalışır ve eşleşirse istek oracıkta biter.
        var alias = AliasYonlendirmesi(yapilandirma, istek, durum);
        if (alias is not null)
        {
            alias.Budget = Butce(kronometre, asildi: false);
            return alias;
        }

        if (yapilandirma.RewriteEngine != true)
        {
            if (yapilandirma.Rules.Count > 0)
            {
                _bulgular.Add(HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Warning,
                    HtaccessDiagnosticCodes.RewriteEngineOff, yapilandirma.Rules[0].File, yapilandirma.Rules[0].Line,
                    "RewriteEngine",
                    $"{yapilandirma.Rules.Count} adet RewriteRule var ama 'RewriteEngine On' yazılmamış; Apache'de de olduğu gibi HİÇBİRİ çalışmaz."));
            }

            sonuc.Budget = Butce(kronometre, asildi: false);
            return sonuc;
        }

        var bitti = false;
        while (!bitti)
        {
            if (_turSayaci >= _sinirlar.MaxInternalRedirects)
            {
                return Basarisiz(sonuc, HtaccessDiagnosticCodes.RecursionBudgetExceeded, kronometre,
                    $"Kural kümesi {_sinirlar.MaxInternalRedirects} kez baştan çalıştı ve URL hâlâ değişiyor: sonsuz döngü. Apache'nin LimitInternalRecursion sınırı da aynı işi yapar. Kurallara [L] ya da bir '!-f' koşulu ekleyin.");
            }

            if (_turSayaci > 0)
            {
                // Apache iç yönlendirme yaparken REDIRECT_STATUS ortam değişkenini yazar ve
                // gerçek dünyadaki yapılandırmalar buna GÜVENİR: Symfony'nin resmî .htaccess'i
                // "yalnızca ilk turda yönlendir" koşulunu (`RewriteCond %{ENV:REDIRECT_STATUS} =""`)
                // tam olarak bunun için kullanır. Yazmasaydık her istek 308 döngüsüne girerdi —
                // ve bu, "çalışıyor gibi görünüp çalışmayan" hatanın ta kendisi olurdu.
                //
                // SAPMA: Apache iç yönlendirmede DİĞER ortam değişkenlerini de "REDIRECT_" ön ekiyle
                // yeniden adlandırır (BASE -> REDIRECT_BASE). Motor bunu yapmaz; değişkenler adını
                // korur. Pratikte fark yaratmaz çünkü bu değişkenleri kuran kurallar her turda
                // yeniden çalışır.
                durum.Env["REDIRECT_STATUS"] = "200";
            }

            _turSayaci++;
            var tur = TurCalistir(yapilandirma, durum, sonuc, kronometre);
            if (tur == TurSonucu.Basarisiz || tur == TurSonucu.Sonlandi)
            {
                if (tur == TurSonucu.Basarisiz)
                {
                    return sonuc;
                }

                bitti = true;
                break;
            }

            if (tur == TurSonucu.Degismedi)
            {
                break;
            }
        }

        // Bkz. yukarisi: KeyValuePair<,>.Deconstruct net48'de yok.
        foreach (var giris in durum.Env)
        {
            sonuc.Env[giris.Key] = giris.Value;
        }

        if (sonuc.Outcome == HtaccessOutcome.None &&
            (durum.CurrentPath != istek.RequestPath || durum.CurrentQuery != istek.QueryString))
        {
            sonuc.Outcome = HtaccessOutcome.Rewritten;
        }

        sonuc.Path = durum.CurrentPath;
        sonuc.Query = durum.CurrentQuery;
        sonuc.Budget = Butce(kronometre, asildi: false);
        return sonuc;
    }

    private enum TurSonucu
    {
        Degismedi,
        Degisti,
        Sonlandi,
        Basarisiz
    }

    private TurSonucu TurCalistir(
        HtaccessConfiguration yapilandirma, HtaccessRewriteState durum, HtaccessRunOutcome sonuc, Stopwatch kronometre)
    {
        var kurallar = yapilandirma.Rules;
        var degisti = false;
        var atlanacak = 0;
        var kirikZincir = false;
        var i = 0;

        while (i < kurallar.Count)
        {
            if (_kuralSayaci >= _sinirlar.MaxRuleEvaluations)
            {
                Basarisiz(sonuc, HtaccessDiagnosticCodes.RuleBudgetExceeded, kronometre,
                    $"Tek istekte {_sinirlar.MaxRuleEvaluations} kuraldan fazlası değerlendirildi; değerlendirme durduruldu.");
                return TurSonucu.Basarisiz;
            }

            if (kronometre.Elapsed > _sinirlar.MaxTotalEvaluationTime)
            {
                Basarisiz(sonuc, HtaccessDiagnosticCodes.TimeBudgetExceeded, kronometre,
                    $"Değerlendirme {_sinirlar.MaxTotalEvaluationTime.TotalMilliseconds:0} ms bütçesini aştı; durduruldu.");
                return TurSonucu.Basarisiz;
            }

            var kural = kurallar[i];
            _kuralSayaci++;

            if (atlanacak > 0)
            {
                atlanacak--;
                i++;
                continue;
            }

            if (kirikZincir)
            {
                var zincirliydi = kural.Flags.Chain;
                i++;
                if (!zincirliydi)
                {
                    kirikZincir = false;
                }

                continue;
            }

            var konu = OnEkiSoy(durum.CurrentPath, kural.VirtualPath);
            var eslesme = DesenEslestir(kural.Pattern, konu, kural.Flags.NoCase, kural.File, kural.Line, "RewriteRule");
            if (eslesme.ZamanAsimi)
            {
                Basarisiz(sonuc, HtaccessDiagnosticCodes.RegexTimeout, kronometre,
                    $"{kural.File}:{kural.Line} — RewriteRule deseni '{kural.Pattern}' zaman aşımına uğradı (ReDoS koruması). Değerlendirme durduruldu, istek YENİDEN YAZILMADAN devam etmeli.");
                return TurSonucu.Basarisiz;
            }

            var uyuyor = eslesme.Basarili ^ kural.Negated;
            if (uyuyor && !kural.Negated && eslesme.Gruplar is not null)
            {
                durum.RuleBackrefs = eslesme.Gruplar;
            }
            else if (uyuyor)
            {
                durum.RuleBackrefs = new string[10];
            }

            if (uyuyor)
            {
                var kosulSonucu = KosullariDegerlendir(kural, durum, sonuc, kronometre);
                if (kosulSonucu is null)
                {
                    return TurSonucu.Basarisiz;
                }

                uyuyor = kosulSonucu.Value;
            }

            if (!uyuyor)
            {
                kirikZincir = kural.Flags.Chain;
                i++;
                continue;
            }

            foreach (var (ad, deger) in kural.Flags.Env)
            {
                durum.Env[ad] = HtaccessInterpolator.Expand(deger, durum, _bulgular, kural.File, kural.Line);
            }

            if (kural.Flags.Forbidden)
            {
                sonuc.Outcome = HtaccessOutcome.Forbidden;
                sonuc.StatusCode = 403;
                sonuc.Path = durum.CurrentPath;
                sonuc.Query = durum.CurrentQuery;
                sonuc.Budget = Butce(kronometre, asildi: false);
                return TurSonucu.Sonlandi;
            }

            if (kural.Flags.Gone)
            {
                sonuc.Outcome = HtaccessOutcome.Gone;
                sonuc.StatusCode = 410;
                sonuc.Path = durum.CurrentPath;
                sonuc.Query = durum.CurrentQuery;
                sonuc.Budget = Butce(kronometre, asildi: false);
                return TurSonucu.Sonlandi;
            }

            if (kural.Flags.Skip > 0)
            {
                atlanacak = kural.Flags.Skip;
                i++;
                continue;
            }

            var uygulandi = Uygula(kural, durum, sonuc, kronometre);
            if (uygulandi == UygulamaSonucu.Yonlendirme)
            {
                return TurSonucu.Sonlandi;
            }

            if (uygulandi == UygulamaSonucu.Degisti)
            {
                degisti = true;
            }

            if (kural.Flags.End)
            {
                return TurSonucu.Sonlandi;
            }

            if (kural.Flags.Next)
            {
                // [N] kümeyi BAŞTAN çalıştırır. Tur sayacına yazılır; yoksa tek turda sonsuz
                // döngü kurulabilirdi ve kural bütçesi dolana kadar CPU yanardı.
                _turSayaci++;
                if (_turSayaci >= _sinirlar.MaxInternalRedirects)
                {
                    Basarisiz(sonuc, HtaccessDiagnosticCodes.RecursionBudgetExceeded, kronometre,
                        $"{kural.File}:{kural.Line} — [N] bayrağı kural kümesini {_sinirlar.MaxInternalRedirects} kez baştan çalıştırdı; döngü kesildi.");
                    return TurSonucu.Basarisiz;
                }

                i = 0;
                atlanacak = 0;
                kirikZincir = false;
                continue;
            }

            if (kural.Flags.Last)
            {
                break;
            }

            i++;
        }

        return degisti ? TurSonucu.Degisti : TurSonucu.Degismedi;
    }

    private enum UygulamaSonucu
    {
        Degismedi,
        Degisti,
        Yonlendirme
    }

    private UygulamaSonucu Uygula(
        HtaccessRewriteRule kural, HtaccessRewriteState durum, HtaccessRunOutcome sonuc, Stopwatch kronometre)
    {
        var yonlendirme = kural.Flags.RedirectStatus;
        if (kural.Substitution == "-")
        {
            if (yonlendirme > 0)
            {
                // "-" hedefiyle [R]: Apache mevcut URL'e yönlendirir. Bu neredeyse her zaman
                // yazım hatasıdır ve sonsuz yönlendirme üretir; uyarılır ama uygulanır.
                _bulgular.Add(HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Warning,
                    HtaccessDiagnosticCodes.InvalidArgument, kural.File, kural.Line, "RewriteRule",
                    "Hedefi '-' olan bir kurala [R] verilmiş: tarayıcı AYNI adrese yönlendirilir ve sonsuz döngü oluşur."));
            }
            else
            {
                return UygulamaSonucu.Degismedi;
            }
        }

        var acilmis = kural.Substitution == "-"
            ? durum.CurrentPath
            : HtaccessInterpolator.Expand(kural.Substitution, durum, _bulgular, kural.File, kural.Line);

        // Hedefteki sorgu dizesi: '?' varsa sağı YENİ sorgu dizesidir.
        string yeniSorgu;
        var soru = acilmis.IndexOf('?');
        if (soru >= 0)
        {
            yeniSorgu = acilmis[(soru + 1)..];
            acilmis = acilmis[..soru];
            if (kural.Flags.QsAppend && !kural.Flags.QsDiscard && !string.IsNullOrEmpty(durum.CurrentQuery))
            {
                yeniSorgu = string.IsNullOrEmpty(yeniSorgu)
                    ? durum.CurrentQuery
                    : $"{yeniSorgu}&{durum.CurrentQuery}";
            }
        }
        else
        {
            yeniSorgu = durum.CurrentQuery;
        }

        if (kural.Flags.QsDiscard && soru < 0)
        {
            yeniSorgu = string.Empty;
        }

        var mutlakHedef = MutlakMi(acilmis);

        if (yonlendirme > 0 || mutlakHedef)
        {
            var hedef = mutlakHedef ? acilmis : YolBirlestir(kural, acilmis);
            if (!kural.Flags.NoEscape)
            {
                hedef = HtaccessInterpolator.EscapeUri(hedef);
            }

            if (!MutlakMi(hedef))
            {
                var semaVeHost = SemaVeHost(durum.Request);
                hedef = semaVeHost + hedef;
            }

            if (!string.IsNullOrEmpty(yeniSorgu))
            {
                hedef += "?" + yeniSorgu;
            }

            sonuc.Outcome = HtaccessOutcome.Redirect;
            sonuc.StatusCode = yonlendirme > 0 ? yonlendirme : 302;
            sonuc.Location = hedef;
            sonuc.Path = durum.CurrentPath;
            sonuc.Query = durum.CurrentQuery;
            sonuc.Budget = Butce(kronometre, asildi: false);
            return UygulamaSonucu.Yonlendirme;
        }

        var yeniYol = YolBirlestir(kural, acilmis);

        // YOL GÜVENLİĞİ: yeniden yazma hedefi site kökünün DIŞINA çıkamaz. "/../../gizli" gibi
        // bir hedef, ".." bileşenleri normalleştirildikten sonra kökün dışını gösterirdi.
        if (!KokIcinde(durum.Request.SiteRootPath, yeniYol))
        {
            _bulgular.Add(HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Error,
                HtaccessDiagnosticCodes.PathEscapesSiteRoot, kural.File, kural.Line, "RewriteRule",
                $"Yeniden yazma hedefi '{yeniYol}' site kökünün dışına çıkıyor; kural UYGULANMADI."));
            return UygulamaSonucu.Degismedi;
        }

        var degistiMi = yeniYol != durum.CurrentPath || yeniSorgu != durum.CurrentQuery;
        durum.CurrentPath = yeniYol;
        durum.CurrentQuery = yeniSorgu;
        return degistiMi ? UygulamaSonucu.Degisti : UygulamaSonucu.Degismedi;
    }

    private bool? KosullariDegerlendir(
        HtaccessRewriteRule kural, HtaccessRewriteState durum, HtaccessRunOutcome sonuc, Stopwatch kronometre)
    {
        var i = 0;
        var kosullar = kural.Conditions;

        while (i < kosullar.Count)
        {
            // [OR] ile bağlı ardışık koşullar TEK bir grup oluşturur; gruplar birbirine VE ile bağlanır.
            var grupSonucu = false;
            var grupBitti = false;

            while (!grupBitti && i < kosullar.Count)
            {
                var kosul = kosullar[i];
                var deger = TekKosul(kosul, durum, out var zamanAsimi);
                if (zamanAsimi)
                {
                    Basarisiz(sonuc, HtaccessDiagnosticCodes.RegexTimeout, kronometre,
                        $"{kosul.File}:{kosul.Line} — RewriteCond deseni '{kosul.Pattern}' zaman aşımına uğradı (ReDoS koruması). Değerlendirme durduruldu.");
                    return null;
                }

                grupSonucu |= deger;
                grupBitti = !kosul.OrNext;
                i++;
            }

            if (!grupSonucu)
            {
                return false;
            }
        }

        return true;
    }

    private bool TekKosul(HtaccessRewriteCond kosul, HtaccessRewriteState durum, out bool zamanAsimi)
    {
        zamanAsimi = false;
        var test = HtaccessInterpolator.Expand(kosul.TestString, durum, _bulgular, kosul.File, kosul.Line);
        bool sonuc;

        switch (kosul.Kind)
        {
            case HtaccessCondKind.AlwaysFalse:
                return false;

            case HtaccessCondKind.FileExists:
                sonuc = KokIcindekiDosya(durum, test) && _dosyaSistemi.FileExists(test);
                break;
            case HtaccessCondKind.DirectoryExists:
                sonuc = KokIcindekiDosya(durum, test) && _dosyaSistemi.DirectoryExists(test);
                break;
            case HtaccessCondKind.SymbolicLink:
                sonuc = KokIcindekiDosya(durum, test) && _dosyaSistemi.IsSymbolicLink(test);
                break;
            case HtaccessCondKind.NonEmptyFile:
                sonuc = KokIcindekiDosya(durum, test) && _dosyaSistemi.IsNonEmptyFile(test);
                break;

            case HtaccessCondKind.LexicalEqual:
            case HtaccessCondKind.LexicalLess:
            case HtaccessCondKind.LexicalGreater:
            case HtaccessCondKind.LexicalLessEqual:
            case HtaccessCondKind.LexicalGreaterEqual:
            {
                var beklenen = HtaccessInterpolator.Expand(kosul.Pattern, durum, _bulgular, kosul.File, kosul.Line);
                var karsilastirma = string.CompareOrdinal(test, beklenen);
                sonuc = kosul.Kind switch
                {
                    HtaccessCondKind.LexicalEqual => karsilastirma == 0,
                    HtaccessCondKind.LexicalLess => karsilastirma < 0,
                    HtaccessCondKind.LexicalGreater => karsilastirma > 0,
                    HtaccessCondKind.LexicalLessEqual => karsilastirma <= 0,
                    _ => karsilastirma >= 0
                };
                break;
            }

            case HtaccessCondKind.IntegerEqual:
            case HtaccessCondKind.IntegerNotEqual:
            case HtaccessCondKind.IntegerLess:
            case HtaccessCondKind.IntegerGreater:
            case HtaccessCondKind.IntegerLessEqual:
            case HtaccessCondKind.IntegerGreaterEqual:
            {
                var beklenen = HtaccessInterpolator.Expand(kosul.Pattern, durum, _bulgular, kosul.File, kosul.Line);
                if (!long.TryParse(test, out var sol) || !long.TryParse(beklenen, out var sag))
                {
                    sonuc = false;
                    break;
                }

                sonuc = kosul.Kind switch
                {
                    HtaccessCondKind.IntegerEqual => sol == sag,
                    HtaccessCondKind.IntegerNotEqual => sol != sag,
                    HtaccessCondKind.IntegerLess => sol < sag,
                    HtaccessCondKind.IntegerGreater => sol > sag,
                    HtaccessCondKind.IntegerLessEqual => sol <= sag,
                    _ => sol >= sag
                };
                break;
            }

            default:
            {
                var eslesme = DesenEslestir(kosul.Pattern, test, kosul.NoCase, kosul.File, kosul.Line, "RewriteCond");
                if (eslesme.ZamanAsimi)
                {
                    zamanAsimi = true;
                    return false;
                }

                sonuc = eslesme.Basarili;
                if (sonuc && !kosul.Negated && eslesme.Gruplar is not null)
                {
                    durum.CondBackrefs = eslesme.Gruplar;
                }

                break;
            }
        }

        return kosul.Negated ? !sonuc : sonuc;
    }

    private HtaccessRunOutcome? AliasYonlendirmesi(
        HtaccessConfiguration yapilandirma, HtaccessRequestContext istek, HtaccessRewriteState durum)
    {
        foreach (var yonlendirme in yapilandirma.Redirects)
        {
            string hedef;

            if (yonlendirme.IsMatch)
            {
                var eslesme = DesenEslestir(yonlendirme.Source, durum.CurrentPath, ignoreCase: false,
                    yonlendirme.File, yonlendirme.Line, "RedirectMatch");
                if (eslesme.ZamanAsimi || !eslesme.Basarili)
                {
                    continue;
                }

                durum.RuleBackrefs = eslesme.Gruplar ?? new string[10];
                hedef = HtaccessInterpolator.Expand(yonlendirme.Target, durum, _bulgular, yonlendirme.File, yonlendirme.Line);
            }
            else
            {
                if (!durum.CurrentPath.StartsWith(yonlendirme.Source, StringComparison.Ordinal))
                {
                    continue;
                }

                var kalan = durum.CurrentPath[yonlendirme.Source.Length..];
                hedef = yonlendirme.Target.TrimEnd('/') + kalan;
            }

            if (!MutlakMi(hedef))
            {
                if (!hedef.StartsWith("/", StringComparison.Ordinal))
                {
                    hedef = "/" + hedef;
                }

                hedef = SemaVeHost(istek) + hedef;
            }

            if (!string.IsNullOrEmpty(durum.CurrentQuery))
            {
                hedef += "?" + durum.CurrentQuery;
            }

            return new HtaccessRunOutcome
            {
                Outcome = HtaccessOutcome.Redirect,
                StatusCode = yonlendirme.StatusCode,
                Location = hedef,
                Path = durum.CurrentPath,
                Query = durum.CurrentQuery
            };
        }

        return null;
    }

    // ---------------------------------------------------------------- yardımcılar

    private readonly record struct EslesmeSonucu(bool Basarili, string[]? Gruplar, bool ZamanAsimi);

    private EslesmeSonucu DesenEslestir(
        string desen, string girdi, bool ignoreCase, string dosya, int satir, string direktif)
    {
        _desenSayaci++;
        var regex = _desenler.TryGet(desen, ignoreCase, _sinirlar.RegexTimeout, out var hata);
        if (regex is null)
        {
            _bulgular.Add(HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Error,
                HtaccessDiagnosticCodes.InvalidRegex, dosya, satir, direktif,
                $"Desen derlenemedi ({hata}); kural UYGULANMADI."));
            return new EslesmeSonucu(false, null, false);
        }

        var eslesme = HtaccessRegexCache.TryMatch(regex, girdi, out var zamanAsimi);
        if (zamanAsimi || eslesme is null)
        {
            return new EslesmeSonucu(false, null, true);
        }

        if (!eslesme.Success)
        {
            return new EslesmeSonucu(false, null, false);
        }

        var gruplar = new string[10];
        for (var i = 0; i < 10 && i < eslesme.Groups.Count; i++)
        {
            gruplar[i] = eslesme.Groups[i].Success ? eslesme.Groups[i].Value : string.Empty;
        }

        return new EslesmeSonucu(true, gruplar, false);
    }

    /// <summary>
    /// Dizin-içi bağlamda desen, URL'in DİZİN ÖN EKİ SOYULMUŞ hâline uygulanır.
    /// <c>/blog/</c> içindeki bir kural için <c>/blog/yazi</c> isteği <c>yazi</c> olarak eşleşir.
    /// </summary>
    internal static string OnEkiSoy(string yol, string dizinOnEki)
    {
        if (!string.IsNullOrEmpty(dizinOnEki) && dizinOnEki != "/" &&
            yol.StartsWith(dizinOnEki, StringComparison.OrdinalIgnoreCase))
        {
            return yol[dizinOnEki.Length..];
        }

        return yol.TrimStart('/');
    }

    /// <summary>
    /// Göreli bir hedefi mutlak URL yoluna çevirir. <c>RewriteBase</c> varsa o, yoksa kuralın
    /// bulunduğu dizinin ön eki kullanılır — Apache'nin davranışı budur.
    /// </summary>
    private static string YolBirlestir(HtaccessRewriteRule kural, string hedef)
    {
        if (hedef.StartsWith("/", StringComparison.Ordinal))
        {
            return Normalize(hedef);
        }

        var temel = kural.RewriteBase ?? kural.VirtualPath;
        if (!temel.EndsWith("/", StringComparison.Ordinal))
        {
            temel += "/";
        }

        return Normalize(temel + hedef);
    }

    /// <summary><c>.</c> ve <c>..</c> bileşenlerini çözer; sonuç her zaman <c>/</c> ile başlar.</summary>
    internal static string Normalize(string yol)
    {
        var parcalar = new List<string>();
        foreach (var parca in yol.Replace('\\', '/').Split('/'))
        {
            switch (parca)
            {
                case "" or ".":
                    continue;
                case "..":
                    // Kökün ÜSTÜNE çıkma denemesi İŞARETLENİR ki çağıran reddedebilsin.
                    // ÖLÇÜLDÜ: işareti sıradan bir bileşen gibi bırakmak yetmiyordu —
                    // "/../../gizli.php" içinde ikinci ".." birincisini POP ederek işareti siliyor
                    // ve yol "/gizli.php" olarak KÖKÜN İÇİNDE görünüyordu. Bir kez yukarı çıkıldıysa
                    // sonraki ".." asla geri sarmaz.
                    if (parcalar.Count > 0 && parcalar[^1] != "..")
                    {
                        parcalar.RemoveAt(parcalar.Count - 1);
                    }
                    else
                    {
                        parcalar.Add("..");
                    }

                    continue;
                default:
                    parcalar.Add(parca);
                    continue;
            }
        }

        var sonEgikCizgi = yol.EndsWith("/", StringComparison.Ordinal) && parcalar.Count > 0 ? "/" : string.Empty;
        return "/" + string.Join("/", parcalar) + sonEgikCizgi;
    }

    private static bool KokIcinde(string siteRoot, string urlYolu)
    {
        if (urlYolu.Split('/').Any(p => p == ".."))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(siteRoot))
        {
            return true;
        }

        var fiziksel = HtaccessServerVariables.FizikselYol(siteRoot, urlYolu);
        return !string.IsNullOrEmpty(fiziksel) && PathContainment.IsRootOrInside(siteRoot, fiziksel);
    }

    /// <summary>
    /// Dosya testleri (<c>-f</c>, <c>-d</c>) yalnızca site kökünün ALTINDA yapılır.
    /// </summary>
    /// <remarks>
    /// Aksi hâlde <c>RewriteCond C:\ProgramData\AdaPanel\panel.db -f</c> gibi bir koşul, kiracıya
    /// sunucudaki dosyaların VARLIĞINI sızdıran bir sorgulama aracı olurdu.
    /// </remarks>
    private bool KokIcindekiDosya(HtaccessRewriteState durum, string yol)
    {
        var kok = durum.Request.SiteRootPath;
        if (string.IsNullOrWhiteSpace(kok) || string.IsNullOrWhiteSpace(yol))
        {
            return false;
        }

        if (PathContainment.IsRootOrInside(kok, yol))
        {
            return true;
        }

        _bulgular.Add(HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Warning,
            HtaccessDiagnosticCodes.PathEscapesSiteRoot, string.Empty, 0, "RewriteCond",
            $"Dosya testi site kökünün dışındaki bir yola bakıyor ('{yol}'); koşul BAŞARISIZ sayıldı."));
        return false;
    }

    private static bool MutlakMi(string hedef) =>
        hedef.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        hedef.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        hedef.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase) ||
        hedef.StartsWith("//", StringComparison.Ordinal);

    private static string SemaVeHost(HtaccessRequestContext istek)
    {
        var sema = istek.IsHttps ? "https" : "http";
        var host = string.IsNullOrEmpty(istek.HttpHost) ? istek.ServerName : istek.HttpHost;
        return $"{sema}://{host}";
    }

    private HtaccessRunOutcome Basarisiz(
        HtaccessRunOutcome sonuc, string kod, Stopwatch kronometre, string mesaj)
    {
        sonuc.Failed = true;
        sonuc.FailureCode = kod;
        sonuc.Outcome = HtaccessOutcome.Failed;
        sonuc.Budget = Butce(kronometre, asildi: true);
        _bulgular.Add(HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Error, kod, string.Empty, 0, "RewriteRule", mesaj));
        return sonuc;
    }

    private HtaccessBudgetReport Butce(Stopwatch kronometre, bool asildi) => new()
    {
        RuleEvaluations = _kuralSayaci,
        InternalRedirects = _turSayaci,
        RegexEvaluations = _desenSayaci,
        Elapsed = kronometre.Elapsed,
        Exceeded = asildi
    };
}
