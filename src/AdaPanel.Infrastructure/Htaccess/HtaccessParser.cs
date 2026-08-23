using System.Text;
using AdaPanel.Core.Dtos.Htaccess;

namespace AdaPanel.Infrastructure.Htaccess;

/// <summary>
/// <c>.htaccess</c> söz dizimi ayrıştırıcısı: satır devamı, yorumlar, tırnaklar, bloklar.
/// </summary>
/// <remarks>
/// <para>
/// <b>Apache'nin söz dizimi kuralları BİREBİR taklit edildi</b> (server/util.c,
/// <c>ap_getword_conf</c> + <c>ap_varbuf_cfg_getline</c>):
/// </para>
/// <list type="bullet">
/// <item>Yorum yalnızca SATIR BAŞINDA (boşluklardan sonra) <c>#</c> ile başlar. Apache satır
/// SONUNDA yorum tanımaz — <c>RewriteRule a b # not</c> satırında <c>#</c> ve <c>not</c> birer
/// ARGÜMANDIR. Burada da öyledir; aksi hâlde sessizce farklı davranırdık.</item>
/// <item>Satır sonundaki tek <c>\</c> satırı devam ettirir; devam satırının BAŞTAKİ boşluğu atılır.</item>
/// <item>Tırnak yalnızca KELİME BAŞINDA açılır (<c>"</c> ya da <c>'</c>); içinde <c>\"</c> kaçışı geçerlidir.</item>
/// </list>
/// <para>
/// <b>Tanınmayan direktif ATLANMAZ.</b> Üç ayrı bulgu üretilir: uygulanan, tanınan-ama-uygulanmayan,
/// hiç tanınmayan. Panel bunu kullanıcıya gösterir.
/// </para>
/// </remarks>
internal static class HtaccessParser
{
    /// <summary>Bellekteki bir metni ayrıştırır. İSTİSNA FIRLATMAZ.</summary>
    public static HtaccessParseResult Parse(string? content, string filePath, string virtualPath, HtaccessLimits limits)
    {
        var bulgular = new List<HtaccessDiagnostic>();
        var direktifler = new List<HtaccessDirective>();

        var metin = content ?? string.Empty;
        // UTF-8 BOM: dosya olarak okunduğunda StreamReader düşürür, ama panelden gelen metinde
        // kalabilir. Kalırsa ilk direktifin adı "﻿RewriteEngine" olur ve "bilinmeyen direktif"
        // diye reddedilirdi.
        metin = metin.TrimStart('﻿');

        var boyut = Encoding.UTF8.GetByteCount(metin);
        if (boyut > limits.MaxFileBytes)
        {
            bulgular.Add(Bulgu(HtaccessDiagnosticLevel.Fatal, HtaccessDiagnosticCodes.FileTooLarge, filePath, 0,
                string.Empty,
                $"Dosya {boyut} bayt; sınır {limits.MaxFileBytes} bayt. Bu boyuttaki bir .htaccess her istekte okunup ayrıştırılacağı için sitenin tamamını yavaşlatır. Dosya UYGULANMADI."));
            return new HtaccessParseResult
            {
                FilePath = filePath,
                VirtualPath = virtualPath,
                Directives = [],
                Diagnostics = bulgular,
                SizeBytes = boyut
            };
        }

        var mantiksalSatirlar = MantiksalSatirlar(metin, limits, filePath, bulgular);
        var blokYigini = new List<HtaccessSectionRef>();

        foreach (var (satir, satirNo) in mantiksalSatirlar)
        {
            var kirpik = satir.Trim();
            if (kirpik.Length == 0 || kirpik[0] == '#')
            {
                continue;
            }

            if (kirpik.StartsWith("</", StringComparison.Ordinal))
            {
                BlokKapat(kirpik, satirNo, filePath, blokYigini, bulgular);
                continue;
            }

            if (kirpik[0] == '<')
            {
                BlokAc(kirpik, satirNo, filePath, blokYigini, bulgular);
                continue;
            }

            if (direktifler.Count >= limits.MaxDirectivesPerFile)
            {
                bulgular.Add(Bulgu(HtaccessDiagnosticLevel.Fatal, HtaccessDiagnosticCodes.TooManyDirectives, filePath,
                    satirNo, string.Empty,
                    $"Dosyada {limits.MaxDirectivesPerFile}'den fazla direktif var. Bu noktadan sonrası OKUNMADI ve dosya UYGULANMADI."));
                break;
            }

            var parcalar = Kelimele(kirpik, out var tirnakAcikKaldi);
            if (tirnakAcikKaldi)
            {
                bulgular.Add(Bulgu(HtaccessDiagnosticLevel.Error, HtaccessDiagnosticCodes.UnterminatedQuote, filePath,
                    satirNo, parcalar.Count > 0 ? parcalar[0] : string.Empty,
                    "Tırnak kapanmamış. Satır UYGULANMADI."));
                continue;
            }

            if (parcalar.Count == 0)
            {
                continue;
            }

            var ad = parcalar[0];
            var direktif = new HtaccessDirective
            {
                Name = ad,
                Arguments = parcalar.Skip(1).ToList(),
                Line = satirNo,
                RawLine = kirpik,
                Sections = blokYigini.ToList()
            };
            direktifler.Add(direktif);

            DirektifBulgusu(direktif, filePath, blokYigini, bulgular);
        }

        foreach (var acikBlok in blokYigini)
        {
            bulgular.Add(Bulgu(HtaccessDiagnosticLevel.Fatal, HtaccessDiagnosticCodes.UnclosedSection, filePath,
                acikBlok.Line, acikBlok.Name,
                $"<{acikBlok.Name}> bloğu kapatılmamış. Apache böyle bir dosyada 500 döner; motor dosyayı UYGULAMAZ."));
        }

        return new HtaccessParseResult
        {
            FilePath = filePath,
            VirtualPath = virtualPath,
            Directives = direktifler,
            Diagnostics = bulgular,
            SizeBytes = boyut
        };
    }

    /// <summary>
    /// Fiziksel satırları, satır devamlarını (<c>\</c>) birleştirerek MANTIKSAL satırlara çevirir.
    /// Dönen numara, mantıksal satırın BAŞLADIĞI fiziksel satırdır.
    /// </summary>
    private static List<(string Satir, int SatirNo)> MantiksalSatirlar(
        string metin, HtaccessLimits limits, string filePath, List<HtaccessDiagnostic> bulgular)
    {
        var sonuc = new List<(string, int)>();
        var fizikselSatirlar = metin.Split('\n');
        var i = 0;

        while (i < fizikselSatirlar.Length)
        {
            var baslangic = i + 1;
            var birikim = new StringBuilder();
            var ilk = true;

            while (i < fizikselSatirlar.Length)
            {
                var ham = fizikselSatirlar[i].TrimEnd('\r');
                i++;

                // Devam satırının baştaki boşluğu atılır (Apache aynısını yapar); yoksa
                // girintili yazılmış devam satırları tırnak içinde fazladan boşluk üretirdi.
                var parca = ilk ? ham : ham.TrimStart();
                ilk = false;

                var devamEdiyor = DevamEdiyorMu(parca);
                if (devamEdiyor)
                {
                    birikim.Append(parca, 0, parca.Length - 1);
                    continue;
                }

                birikim.Append(parca);
                break;
            }

            var mantiksal = birikim.ToString();
            if (mantiksal.Length > limits.MaxLineLength)
            {
                bulgular.Add(Bulgu(HtaccessDiagnosticLevel.Error, HtaccessDiagnosticCodes.LineTooLong, filePath,
                    baslangic, string.Empty,
                    $"Satır {mantiksal.Length} karakter; sınır {limits.MaxLineLength}. Satır UYGULANMADI."));
                continue;
            }

            sonuc.Add((mantiksal, baslangic));
        }

        return sonuc;
    }

    /// <summary>
    /// Satır tek bir <c>\</c> ile mi bitiyor? <c>\\</c> (kaçırılmış ters bölü) devam DEĞİLDİR.
    /// </summary>
    private static bool DevamEdiyorMu(string satir)
    {
        if (satir.Length == 0 || satir[^1] != '\\')
        {
            return false;
        }

        var tersBolu = 0;
        for (var i = satir.Length - 1; i >= 0 && satir[i] == '\\'; i--)
        {
            tersBolu++;
        }

        return tersBolu % 2 == 1;
    }

    /// <summary>
    /// Apache'nin <c>ap_getword_conf</c> kuralıyla kelimelere ayırır.
    /// </summary>
    public static List<string> Kelimele(string satir, out bool tirnakAcikKaldi)
    {
        tirnakAcikKaldi = false;
        var kelimeler = new List<string>();
        var i = 0;

        while (i < satir.Length)
        {
            while (i < satir.Length && char.IsWhiteSpace(satir[i]))
            {
                i++;
            }

            if (i >= satir.Length)
            {
                break;
            }

            var kelime = new StringBuilder();
            var tirnak = satir[i];

            if (tirnak is '"' or '\'')
            {
                i++;
                var kapandi = false;
                while (i < satir.Length)
                {
                    var k = satir[i];
                    if (k == '\\' && i + 1 < satir.Length && (satir[i + 1] == tirnak || satir[i + 1] == '\\'))
                    {
                        kelime.Append(satir[i + 1]);
                        i += 2;
                        continue;
                    }

                    if (k == tirnak)
                    {
                        i++;
                        kapandi = true;
                        break;
                    }

                    kelime.Append(k);
                    i++;
                }

                if (!kapandi)
                {
                    tirnakAcikKaldi = true;
                }
            }
            else
            {
                while (i < satir.Length && !char.IsWhiteSpace(satir[i]))
                {
                    kelime.Append(satir[i]);
                    i++;
                }
            }

            kelimeler.Add(kelime.ToString());
        }

        return kelimeler;
    }

    private static void BlokAc(
        string satir, int satirNo, string filePath,
        List<HtaccessSectionRef> yigin, List<HtaccessDiagnostic> bulgular)
    {
        var kapanis = satir.LastIndexOf('>');
        if (kapanis < 0)
        {
            bulgular.Add(Bulgu(HtaccessDiagnosticLevel.Error, HtaccessDiagnosticCodes.InvalidArgument, filePath,
                satirNo, string.Empty, "Blok başlığı '>' ile kapatılmamış. Satır UYGULANMADI."));
            return;
        }

        var ic = satir[1..kapanis].Trim();
        var parcalar = Kelimele(ic, out _);
        if (parcalar.Count == 0)
        {
            bulgular.Add(Bulgu(HtaccessDiagnosticLevel.Error, HtaccessDiagnosticCodes.MissingArgument, filePath,
                satirNo, string.Empty, "Blok adı yok."));
            return;
        }

        var ad = parcalar[0];
        var argumanlar = string.Join(' ', parcalar.Skip(1));
        var olumsuz = argumanlar.StartsWith('!');
        if (olumsuz)
        {
            argumanlar = argumanlar[1..].Trim();
        }

        yigin.Add(new HtaccessSectionRef
        {
            Name = ad,
            Argument = argumanlar,
            Negated = olumsuz,
            Line = satirNo
        });

        if (HtaccessDirectiveCatalog.TryGetUnsupportedSectionReason(ad, out var sebep))
        {
            bulgular.Add(Bulgu(HtaccessDiagnosticLevel.Unsupported, HtaccessDiagnosticCodes.UnsupportedSection,
                filePath, satirNo, ad, $"<{ad}> bloğu uygulanmıyor: {sebep}"));
            return;
        }

        if (!HtaccessDirectiveCatalog.IsSupportedSection(ad))
        {
            bulgular.Add(Bulgu(HtaccessDiagnosticLevel.Unsupported, HtaccessDiagnosticCodes.UnsupportedSection,
                filePath, satirNo, ad,
                $"<{ad}> tanınmayan bir blok. İçindeki hiçbir direktif UYGULANMADI. Yazım hatası olabilir."));
            return;
        }

        if (ad.Equals("Directory", StringComparison.OrdinalIgnoreCase) ||
            ad.Equals("DirectoryMatch", StringComparison.OrdinalIgnoreCase))
        {
            // DÜRÜSTLÜK: Apache <Directory> bloğunu .htaccess içinde REDDEDER (500 döner).
            // Motor onu bir dizin süzgeci olarak uyguluyor — bilerek alınmış bir SAPMA, çünkü
            // panele yapıştırılan yapılandırmalarda sık geçiyor ve sessizce yok saymak
            // "çalışıyor sandım" hatası üretirdi.
            bulgular.Add(Bulgu(HtaccessDiagnosticLevel.Warning, HtaccessDiagnosticCodes.ApacheWouldReject,
                filePath, satirNo, ad,
                $"Apache <{ad}> bloğunu .htaccess içinde KABUL ETMEZ ve 500 döner. AdaPanel bunu dizin süzgeci olarak UYGULUYOR; Apache'ye taşırsanız çalışmaz."));
        }
    }

    private static void BlokKapat(
        string satir, int satirNo, string filePath,
        List<HtaccessSectionRef> yigin, List<HtaccessDiagnostic> bulgular)
    {
        var kapanis = satir.LastIndexOf('>');
        var ad = kapanis > 2 ? satir[2..kapanis].Trim() : string.Empty;

        if (yigin.Count == 0)
        {
            bulgular.Add(Bulgu(HtaccessDiagnosticLevel.Error, HtaccessDiagnosticCodes.UnexpectedSectionEnd,
                filePath, satirNo, ad, $"</{ad}> açılmamış bir bloğu kapatıyor."));
            return;
        }

        var sonuncu = yigin[^1];
        if (!string.IsNullOrEmpty(ad) && !sonuncu.Name.Equals(ad, StringComparison.OrdinalIgnoreCase))
        {
            bulgular.Add(Bulgu(HtaccessDiagnosticLevel.Error, HtaccessDiagnosticCodes.MismatchedSectionEnd,
                filePath, satirNo, ad,
                $"</{ad}> ile <{sonuncu.Name}> eşleşmiyor (blok {sonuncu.Line}. satırda açılmıştı)."));
        }

        yigin.RemoveAt(yigin.Count - 1);
    }

    private static void DirektifBulgusu(
        HtaccessDirective direktif, string filePath,
        List<HtaccessSectionRef> yigin, List<HtaccessDiagnostic> bulgular)
    {
        // Zaten uygulanmayan bir bloğun içindeysek direktif başına ayrıca uyarı üretmeyiz:
        // blok uyarısı sebebi söylüyor, satır satır tekrarlamak paneli gürültüye boğardı.
        if (yigin.Any(b => !HtaccessDirectiveCatalog.IsSupportedSection(b.Name)))
        {
            return;
        }

        if (HtaccessDirectiveCatalog.IsSupported(direktif.Name))
        {
            return;
        }

        if (HtaccessDirectiveCatalog.TryGetUnsupportedReason(direktif.Name, out var sebep))
        {
            bulgular.Add(Bulgu(HtaccessDiagnosticLevel.Unsupported, HtaccessDiagnosticCodes.UnsupportedDirective,
                filePath, direktif.Line, direktif.Name, $"{direktif.Name} uygulanmıyor: {sebep}"));
            return;
        }

        bulgular.Add(Bulgu(HtaccessDiagnosticLevel.Unsupported, HtaccessDiagnosticCodes.UnknownDirective,
            filePath, direktif.Line, direktif.Name,
            $"'{direktif.Name}' tanınmayan bir direktif; UYGULANMADI. Apache eklentisine ait olabilir ya da yazım hatası olabilir."));
    }

    internal static HtaccessDiagnostic Bulgu(
        HtaccessDiagnosticLevel seviye, string kod, string dosya, int satir, string direktif, string mesaj) =>
        new()
        {
            Level = seviye,
            Code = kod,
            File = dosya,
            Line = satir,
            Directive = direktif,
            Message = mesaj
        };
}
