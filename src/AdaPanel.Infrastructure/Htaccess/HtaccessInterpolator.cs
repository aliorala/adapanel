using System.Text;
using AdaPanel.Core.Dtos.Htaccess;

namespace AdaPanel.Infrastructure.Htaccess;

/// <summary>
/// <c>$1</c>, <c>%1</c>, <c>%{HTTP_HOST}</c> gibi yer tutucuların açılması.
/// </summary>
/// <remarks>
/// <para>
/// Apache'nin genişletme sırası taklit edilir: <c>%N</c> (RewriteCond geri referansı),
/// <c>$N</c> (RewriteRule geri referansı), <c>%{DEGISKEN}</c> ve <c>${eslem:...}</c>.
/// <c>\</c> ile kaçırılmış karakterler harfi harfine geçer — <c>\$1</c> gerçekten <c>$1</c> yazar.
/// </para>
/// <para>
/// <b>Tek geçişte açılır ve sonuç TEKRAR açılmaz.</b> Bu bilinçlidir: aksi hâlde
/// <c>%{QUERY_STRING}</c> içindeki bir <c>%1</c>, saldırganın seçtiği bir değeri yer tutucuya
/// çevirebilirdi (yansıtılmış genişletme).
/// </para>
/// </remarks>
internal static class HtaccessInterpolator
{
    public static string Expand(
        string? template,
        HtaccessRewriteState state,
        List<HtaccessDiagnostic> bulgular,
        string file,
        int line)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        var sonuc = new StringBuilder(template.Length + 32);
        var i = 0;

        while (i < template.Length)
        {
            var k = template[i];

            if (k == '\\' && i + 1 < template.Length)
            {
                sonuc.Append(template[i + 1]);
                i += 2;
                continue;
            }

            if (k == '$' && i + 1 < template.Length && HtaccessPortableApi.AsciiRakamMi(template[i + 1]))
            {
                sonuc.Append(state.RuleBackrefs[template[i + 1] - '0'] ?? string.Empty);
                i += 2;
                continue;
            }

            if (k == '%' && i + 1 < template.Length && HtaccessPortableApi.AsciiRakamMi(template[i + 1]))
            {
                sonuc.Append(state.CondBackrefs[template[i + 1] - '0'] ?? string.Empty);
                i += 2;
                continue;
            }

            if (k == '%' && i + 1 < template.Length && template[i + 1] == '{')
            {
                var kapanis = KapanisBul(template, i + 1);
                if (kapanis < 0)
                {
                    sonuc.Append(k);
                    i++;
                    continue;
                }

                var ad = template[(i + 2)..kapanis];
                sonuc.Append(HtaccessServerVariables.Resolve(ad, state, bulgular, file, line));
                i = kapanis + 1;
                continue;
            }

            if (k == '$' && i + 1 < template.Length && template[i + 1] == '{')
            {
                var kapanis = KapanisBul(template, i + 1);
                if (kapanis < 0)
                {
                    sonuc.Append(k);
                    i++;
                    continue;
                }

                // ${eslem:anahtar} → RewriteMap. Desteklenmiyor; yer tutucu HARFİ HARFİNE bırakılır
                // ki kullanıcı sonuçta görsün, boş dizgeyle sessizce kaybolmasın.
                var ifade = template[(i + 1)..(kapanis + 1)];
                Bildir(bulgular, file, line,
                    $"${ifade} bir RewriteMap araması; RewriteMap desteklenmiyor ve ifade OLDUĞU GİBİ bırakıldı.");
                sonuc.Append('$').Append(ifade);
                i = kapanis + 1;
                continue;
            }

            sonuc.Append(k);
            i++;
        }

        return sonuc.ToString();
    }

    /// <summary>
    /// Apache'nin <c>ap_escape_uri</c> karşılığı: <c>[NE]</c> bayrağı YOKSA yönlendirme hedefi
    /// kaçırılır.
    /// </summary>
    /// <remarks>
    /// <c>%</c> de kaçırılır (<c>%25</c>) — <c>[NE]</c> bayrağının var olma sebebi tam olarak budur.
    /// Kaçırmayı atlarsak zaten kodlanmış hedefler iki kez kodlanmaz ama kodlanmamış boşluklu
    /// hedefler bozuk <c>Location</c> üretirdi.
    /// </remarks>
    public static string EscapeUri(string value)
    {
        var sonuc = new StringBuilder(value.Length + 16);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var k = (char)b;
            if (HtaccessPortableApi.AsciiHarfVeyaRakamMi(k) || Kacirilmayan.Contains(k))
            {
                sonuc.Append(k);
            }
            else
            {
                sonuc.Append('%').Append(b.ToString("X2"));
            }
        }

        return sonuc.ToString();
    }

    /// <summary>Apache'nin yol kaçışında dokunmadığı karakterler.</summary>
    private const string Kacirilmayan = "/-_.!~*'()$,;:@&=+";

    private static int KapanisBul(string metin, int acilis)
    {
        var derinlik = 0;
        for (var i = acilis; i < metin.Length; i++)
        {
            if (metin[i] == '{')
            {
                derinlik++;
            }
            else if (metin[i] == '}')
            {
                derinlik--;
                if (derinlik == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static void Bildir(List<HtaccessDiagnostic> bulgular, string file, int line, string mesaj)
    {
        if (bulgular.Any(t => t.Code == HtaccessDiagnosticCodes.UnsupportedDirective && t.Message == mesaj))
        {
            return;
        }

        bulgular.Add(HtaccessParser.Bulgu(HtaccessDiagnosticLevel.Unsupported,
            HtaccessDiagnosticCodes.UnsupportedDirective, file, line, "RewriteMap", mesaj));
    }
}
