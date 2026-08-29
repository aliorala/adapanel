using System.Text.RegularExpressions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// DENETİM KAYDININ AYRINTI SATIRI SABİT TÜRKÇE CÜMLE TAŞIYAMAZ.
/// <para>
/// <b>ÖLÇÜLEN HATA:</b> Denetim Kayıtları ekranı (<c>AuditLogsView.vue</c>) eylem ADINI
/// <c>panel.activity.action.*</c> sözlüğünden çevirir, ama AYRINTI satırını HAM basar
/// (<c>{{ item.details }}</c>). Arka uç oraya Türkçe cümle yazdığı için dili İngilizce olan
/// kullanıcı kayıt listesinde Türkçe okuyordu: "DNS bölge ayarları kaydedildi: ornek.com…",
/// "HTTPS bağlaması kaldırıldı: …", "MCP aracı çağrıldı: …" (ölçüldü: 18 dizge, 4 dosya).
/// </para>
/// <para>
/// <b>KURAL VE NEDENİ:</b> ayrıntı satırı ÇEVRİLMEZ, CÜMLEDEN ARINDIRILIR. Çünkü cümlenin
/// kendisi zaten GEREKSİZDİ — eylem etiketi ("DNS zone updated") aynı şeyi söylüyordu; ayrıntının
/// tek işi ÖLÇÜMÜ taşımaktır (<c>zone=ornek.com; nameServers=…</c>). Ölçüm dilden bağımsızdır,
/// yani çeviri anahtarı üretmek de gerekmez: iki sözlüğe de aynı satırı kopyalayan ölü anahtarlar
/// doğardı. Bu, görevin "her şeyi anahtara çevirmek zorunda değilsin" kuralının tam örneğidir.
/// </para>
/// <para>
/// <b>NEDEN TEST:</b> denetim kaydı yazan 40'tan fazla nokta var ve her yeni uç bir tane daha
/// ekliyor. Hata istisna fırlatmaz, günlüğe düşmez, derlemeyi kızartmaz — yalnızca yanlış dil
/// gösterir. Nöbetçi olmadan sınıf ilk yeni uçta geri gelir.
/// </para>
/// </summary>
public sealed class AuditDetailLanguageTests
{
    /// <summary>Türkçeye özgü harfler: biri geçiyorsa yazılan şey ölçüm değil, CÜMLEDİR.</summary>
    private const string TurkceHarfler = "çÇğĞıİöÖşŞüÜ";

    /// <summary>
    /// <c>AuditAsync(...)</c> / <c>WriteAsync(...)</c> çağrısının argüman listesindeki dizgeler.
    /// Çağrının açılış parantezinden dengeli kapanışına kadar okunur; tek satırlık desen
    /// yetmezdi, çünkü bu çağrıların NORMAL yazımı 4-6 satıra yayılır.
    /// </summary>
    private static readonly Regex AuditCagrisi = new(@"\b(?:AuditAsync|WriteAsync)\s*\(", RegexOptions.Compiled);

    private static readonly Regex Dizge = new(@"""(?:\\.|[^""\\\n])*""", RegexOptions.Compiled);

    [Fact]
    public void Denetim_Kaydi_Ayrintisinda_Sabit_Turkce_Cumle_Yok()
    {
        var dosyalar = KaynakDosyalari().ToArray();

        // ÖLÇEMEDİK ile SORUN YOK ayrımı: tarama bozulursa test sessizce geçer ve bir daha
        // hiçbir şeyi korumaz.
        Assert.True(dosyalar.Length > 100, $"Kaynak taraması bozuk olabilir: {dosyalar.Length} dosya bulundu.");

        var toplamCagri = 0;
        var ihlaller = new List<string>();

        foreach (var dosya in dosyalar)
        {
            var metin = File.ReadAllText(dosya);
            foreach (Match eslesme in AuditCagrisi.Matches(metin))
            {
                var govde = ArgumanGovdesi(metin, eslesme.Index + eslesme.Length);
                if (govde is null)
                {
                    continue;
                }

                toplamCagri++;
                foreach (Match dizge in Dizge.Matches(govde))
                {
                    var deger = dizge.Value;
                    if (!deger.Any(harf => TurkceHarfler.Contains(harf)))
                    {
                        continue;
                    }

                    ihlaller.Add($"{Path.GetFileName(dosya)}:{SatirNo(metin, eslesme.Index)} {deger}");
                }
            }
        }

        Assert.True(toplamCagri > 20, $"Denetim çağrısı taraması bozuk olabilir: {toplamCagri} çağrı bulundu.");

        Assert.True(
            ihlaller.Count == 0,
            "Denetim kaydı ayrıntısında sabit Türkçe metin var; Denetim Kayıtları ekranı ayrıntıyı HAM basar, " +
            "yani İngilizce panelde de aynen görünür. Cümleyi atıp ölçümü bırakın (zone=…; mode=…):\n  " +
            string.Join("\n  ", ihlaller));
    }

    /// <summary>
    /// Açılış parantezinden dengeli kapanışa kadar olan argüman metni — YORUMLARI ATILMIŞ.
    /// </summary>
    /// <remarks>
    /// YORUM ELEME BİR SÜSLEME DEĞİL, ÖLÇÜLMÜŞ BİR YANLIŞ POZİTİFTİ: <c>SiteBuilderController</c>
    /// denetim çağrısının argüman listesinin İÇİNDE gerekçe yorumu taşıyor ve o yorum tırnaklı bir
    /// Türkçe alıntı içeriyor (<c>// … "sitem neden değişti" sorusunun cevabı …</c>). Yorumu
    /// elemeyen ilk sürüm bu iki satırı ihlal saydı; oysa denetim kaydına yazılan metin
    /// İngilizceydi. Nöbetçi yanlış yere işaret ederse ilk yapılan şey onu susturmaktır.
    /// </remarks>
    private static string? ArgumanGovdesi(string metin, int baslangic)
    {
        var govde = new System.Text.StringBuilder();
        var derinlik = 1;
        var tirnakta = false;

        for (var i = baslangic; i < metin.Length && i - baslangic < 4000; i++)
        {
            var karakter = metin[i];
            if (tirnakta)
            {
                govde.Append(karakter);
                if (karakter == '\\' && i + 1 < metin.Length)
                {
                    govde.Append(metin[++i]);
                }
                else if (karakter == '"')
                {
                    tirnakta = false;
                }

                continue;
            }

            // Satır yorumu / blok yorumu: tırnak DIŞINDAYKEN atlanır. "https://" gibi dizge
            // içindeki eğik çizgiler bu dala hiç girmez.
            if (karakter == '/' && i + 1 < metin.Length && metin[i + 1] == '/')
            {
                var son = metin.IndexOf('\n', i);
                if (son < 0)
                {
                    return null;
                }

                i = son;
                govde.Append('\n');
                continue;
            }

            if (karakter == '/' && i + 1 < metin.Length && metin[i + 1] == '*')
            {
                var son = metin.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (son < 0)
                {
                    return null;
                }

                i = son + 1;
                continue;
            }

            switch (karakter)
            {
                case '"':
                    tirnakta = true;
                    break;
                case '(':
                    derinlik++;
                    break;
                case ')':
                    derinlik--;
                    if (derinlik == 0)
                    {
                        return govde.ToString();
                    }

                    break;
            }

            govde.Append(karakter);
        }

        return null;
    }

    private static int SatirNo(string metin, int konum) => metin.Take(konum).Count(karakter => karakter == '\n') + 1;

    /// <summary>
    /// <c>Recovery</c> ve <c>Tray</c> KAPSAM DIŞI: konsol kurtarma aracı ve masaüstü tepsi
    /// penceresi panelin i18n sistemine bağlı değil, kendi metin tablolarını kullanır
    /// (<c>MessageKeyCoverageTests</c> da aynı gerekçeyle bu yolları eler).
    /// </summary>
    private static IEnumerable<string> KaynakDosyalari()
    {
        var kok = Path.Combine(BulKok(), "src");
        return Directory.EnumerateFiles(kok, "*.cs", SearchOption.AllDirectories)
            .Where(dosya =>
                !dosya.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !dosya.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !dosya.Contains($"{Path.DirectorySeparatorChar}Recovery{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !dosya.Contains($"{Path.DirectorySeparatorChar}Tray{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static string BulKok()
    {
        var klasor = AppContext.BaseDirectory;
        for (var derinlik = 0; derinlik < 8; derinlik++)
        {
            if (File.Exists(Path.Combine(klasor, "AdaPanel.sln")))
            {
                return Path.GetFullPath(klasor);
            }

            klasor = Path.Combine(klasor, "..");
        }

        throw new DirectoryNotFoundException("Depo kökü bulunamadı.");
    }
}
