using System.Collections.Concurrent;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Htaccess;

namespace AdaPanel.Infrastructure.Htaccess;

/// <summary>
/// Apache'nin DİZİN BASAMAKLANDIRMASI: kökten istenen dizine kadar her seviyedeki
/// <c>.htaccess</c> sırayla toplanır.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sıra kökten içeriyedir</b> ve bu şart: birleştirmede alt dizin üstü ezer. Ters sırada
/// okumak, kök yapılandırmasının alt dizini ezmesi demek olurdu.
/// </para>
/// <para>
/// <b>Kökün DIŞINA çıkılmaz.</b> İstenen dizin <see cref="PathContainment"/> ile doğrulanır;
/// <c>../</c> içeren bir istek yolu, komşu bir kiracının <c>.htaccess</c>'ini okutamaz.
/// </para>
/// <para>
/// <b>Ayrıştırma sonucu ÖNBELLEKLENİR</b> (yol + değişiklik damgası + boyut). Bu kod her istekte
/// çalışır; her istekte dosya okuyup ayrıştırmak, WordPress kökü için tek başına ölçülebilir bir
/// gecikme üretirdi. Damga değişince önbellek kendiliğinden düşer.
/// </para>
/// </remarks>
internal sealed class HtaccessCascadeLoader
{
    /// <summary>Apache'nin dosya adı. Windows'ta gizli dosya olması bir şeyi değiştirmez.</summary>
    public const string FileName = ".htaccess";

    private readonly IHtaccessFileSystem _dosyaSistemi;
    private readonly ConcurrentDictionary<string, Onbellek> _onbellek = new(StringComparer.OrdinalIgnoreCase);

    public HtaccessCascadeLoader(IHtaccessFileSystem dosyaSistemi) => _dosyaSistemi = dosyaSistemi;

    private sealed record Onbellek(DateTime LastWriteUtc, long Length, HtaccessParseResult Result);

    /// <summary>
    /// Site kökünden <paramref name="goreliDizin"/>'e kadar bulunan bütün dosyaları döndürür.
    /// </summary>
    public IReadOnlyList<HtaccessParseResult> Load(
        string siteRoot, string goreliDizin, HtaccessLimits sinirlar, List<HtaccessDiagnostic> bulgular)
    {
        var kok = PathContainment.Normalize(siteRoot);
        if (kok is null)
        {
            bulgular.Add(HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Fatal,
                HtaccessDiagnosticCodes.PathEscapesSiteRoot, siteRoot ?? string.Empty, 0, string.Empty,
                "Site kökü geçerli bir mutlak yol değil; .htaccess okunmadı."));
            return [];
        }

        var parcalar = (goreliDizin ?? string.Empty)
            .Replace('\\', '/')
            .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p != ".")
            .ToList();

        if (parcalar.Any(p => p == ".."))
        {
            bulgular.Add(HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Error,
                HtaccessDiagnosticCodes.PathEscapesSiteRoot, string.Empty, 0, string.Empty,
                $"'{goreliDizin}' yolu '..' içeriyor; site kökünün dışına çıkma denemesi REDDEDİLDİ."));
            return [];
        }

        if (parcalar.Count + 1 > sinirlar.MaxCascadeDepth)
        {
            bulgular.Add(HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Warning,
                HtaccessDiagnosticCodes.CascadeDepthExceeded, string.Empty, 0, string.Empty,
                $"Dizin derinliği {parcalar.Count + 1}, sınır {sinirlar.MaxCascadeDepth}. Yalnızca ilk {sinirlar.MaxCascadeDepth} seviye okundu."));
            parcalar = parcalar.Take(Math.Max(0, sinirlar.MaxCascadeDepth - 1)).ToList();
        }

        var sonuc = new List<HtaccessParseResult>();
        var fizikselYol = kok;
        var sanalYol = "/";

        for (var seviye = 0; seviye <= parcalar.Count; seviye++)
        {
            if (seviye > 0)
            {
                fizikselYol = Path.Combine(fizikselYol, parcalar[seviye - 1]);
                sanalYol += parcalar[seviye - 1] + "/";

                // Her seviyede yeniden doğrulanır: birleştirme sonrası yolun hâlâ kökün altında
                // olduğu ÖLÇÜLÜR, varsayılmaz.
                if (!PathContainment.IsInsideRoot(kok, fizikselYol))
                {
                    bulgular.Add(HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Error,
                        HtaccessDiagnosticCodes.PathEscapesSiteRoot, fizikselYol, 0, string.Empty,
                        "Dizin zinciri site kökünün dışına çıktı; okuma DURDURULDU."));
                    break;
                }
            }

            var dosyaYolu = Path.Combine(fizikselYol, FileName);
            if (!_dosyaSistemi.FileExists(dosyaYolu))
            {
                continue;
            }

            sonuc.Add(Oku(dosyaYolu, sanalYol, sinirlar));
        }

        return sonuc;
    }

    /// <summary>Tek bir dosyayı okur ve ayrıştırır; damga değişmediyse önbellekten verir.</summary>
    public HtaccessParseResult Oku(string dosyaYolu, string sanalYol, HtaccessLimits sinirlar)
    {
        var (damga, uzunluk) = _dosyaSistemi.Stamp(dosyaYolu);
        if (_onbellek.TryGetValue(dosyaYolu, out var kayit) &&
            kayit.LastWriteUtc == damga && kayit.Length == uzunluk &&
            kayit.Result.VirtualPath == sanalYol)
        {
            return kayit.Result;
        }

        // Boyut sınırı OKUMADAN ÖNCE ölçülür: dev bir dosyayı belleğe alıp sonra reddetmek,
        // korunmak istenen şeyin ta kendisini yapmak olurdu.
        if (uzunluk > sinirlar.MaxFileBytes)
        {
            return new HtaccessParseResult
            {
                FilePath = dosyaYolu,
                VirtualPath = sanalYol,
                SizeBytes = uzunluk,
                Diagnostics =
                [
                    HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Fatal, HtaccessDiagnosticCodes.FileTooLarge,
                        dosyaYolu, 0, string.Empty,
                        $"Dosya {uzunluk} bayt; sınır {sinirlar.MaxFileBytes} bayt. Bu boyuttaki bir .htaccess her istekte okunup ayrıştırılacağı için sitenin tamamını yavaşlatır. Dosya UYGULANMADI.")
                ]
            };
        }

        if (!_dosyaSistemi.TryReadText(dosyaYolu, sinirlar.MaxFileBytes, out var icerik, out var hata))
        {
            var basarisiz = new HtaccessParseResult
            {
                FilePath = dosyaYolu,
                VirtualPath = sanalYol,
                Diagnostics =
                [
                    HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Fatal, HtaccessDiagnosticCodes.FileUnreadable,
                        dosyaYolu, 0, string.Empty,
                        $".htaccess okunamadı ({hata}). Dosya UYGULANMADI — yarım uygulanmış bir yapılandırma, hiç uygulanmamış olandan tehlikelidir.")
                ]
            };

            // Okunamayan dosya ÖNBELLEĞE ALINMAZ: izin düzeltilince bir sonraki istekte okunmalı.
            return basarisiz;
        }

        var ayristirilmis = HtaccessParser.Parse(icerik, dosyaYolu, sanalYol, sinirlar);
        _onbellek[dosyaYolu] = new Onbellek(damga, uzunluk, ayristirilmis);
        return ayristirilmis;
    }

    /// <summary>Önbelleği düşürür. <paramref name="siteRoot"/> boşsa tamamı temizlenir.</summary>
    public void Invalidate(string? siteRoot)
    {
        if (string.IsNullOrWhiteSpace(siteRoot))
        {
            _onbellek.Clear();
            return;
        }

        foreach (var anahtar in _onbellek.Keys.Where(k => PathContainment.IsRootOrInside(siteRoot, k)).ToList())
        {
            _onbellek.TryRemove(anahtar, out _);
        }
    }
}
