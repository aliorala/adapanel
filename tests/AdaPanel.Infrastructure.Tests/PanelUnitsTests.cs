using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Billing;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// KOTA ÖLÇEĞİNİN TABANI TEK KAYNAKTAN GELİR — ve o taban ikiliktir (1024).
///
/// <para>
/// <b>Neden var — ölçülmüş, sessiz kalabilecek hata.</b> Kota tabanı beş ayrı yerde HAM SAYIYLA
/// yazılıydı: <c>CustomerScopeService</c> (bayt → MiB), <c>SubscriptionEnforcementService</c>
/// (GiB → bayt), <c>FileQuotaGuard</c> (iki kez) ve <c>BillingUnits</c> (iki sabit). Beşi de aynı
/// tabandaydı, yani ortada hesap hatası YOKTU. Kapatılan şey hatanın kendisi değil MÜMKÜN
/// OLMASIYDI: birini 1000 tabanına çeviren bir düzenleme hiçbir yerde kırmızı yakmadan geçerdi.
/// </para>
/// <para>
/// <b>Sessiz kalmasının bedeli.</b> Arayüz kullanımı <c>frontend/src/utils/bytes.ts</c> ile 1024
/// tabanında biçimler ve ekrana "GiB"/"MiB" yazar. Sunucu 1000'e kayarsa ekran ile YAPTIRIM
/// ayrışır: kullanıcı %80 dolu görürken sınır çoktan geçilmiş olur (fark ~%7), ya da tersi.
/// Trafik tarafında yaptırım ASKIYA ALMAKTIR — bütün siteler yayından kalkar. Yani bu sapmanın
/// sonucu bir görüntü hatası değil, müşterinin sitelerinin beklenmedik anda kapanmasıdır.
/// </para>
/// <para>
/// <b>Faturalama ayağı.</b> WHMCS'e giden sayı da aynı tabandan gelmek zorundadır; ayrışırsa
/// müşteriye gönderilen fatura ile panelin uyguladığı sınır birbirini tutmaz.
/// </para>
/// </summary>
public sealed class PanelUnitsTests
{
    private const long Mebibyte = 1024L * 1024L;

    [Fact]
    public void Taban_IKILIKTIR_ondalik_degil()
    {
        Assert.Equal(1_048_576L, PanelUnits.BytesPerMebibyte);
        Assert.Equal(1024L, PanelUnits.MebibytesPerGibibyte);

        // AYIRT EDİCİ ÖLÇÜM: 2.000.000 bayt ikilik tabanda 1 MiB, ondalık tabanda 2 MB'dir.
        // Bu tek satır, tabanın 1000'e kaymasını yakalar; `1_048_576` gibi bir değer ise İKİ
        // tabanda da 1 döner ve hiçbir şey kanıtlamazdı.
        Assert.Equal(1L, PanelUnits.BytesToMebibytes(2_000_000L));

        // Aynı ayrım yukarı yönde: 1 GiB = 1.073.741.824 bayt, 1 GB olsaydı 1.000.000.000 olurdu.
        Assert.Equal(1_073_741_824L, PanelUnits.GibibytesToBytes(1));
        Assert.Equal(1024L, PanelUnits.GibibytesToMebibytes(1));
    }

    [Fact]
    public void Negatif_ve_sifir_girdiler_sifira_duser()
    {
        // Eksi kullanım diye bir şey yoktur. Eksi bir sayı kota ORANINI negatife çevirip
        // göstergeyi bozar, WHMCS'e yazılırsa da her hesabı "kotayı aşmış" gösterirdi.
        Assert.Equal(0L, PanelUnits.BytesToMebibytes(-1));
        Assert.Equal(0L, PanelUnits.MebibytesToBytes(-1));
        Assert.Equal(0L, PanelUnits.GibibytesToBytes(-1));
        Assert.Equal(0L, PanelUnits.GibibytesToMebibytes(-1));
        Assert.Equal(0L, PanelUnits.BytesToMebibytes(0));
    }

    [Fact]
    public void Asagi_yuvarlar_musterinin_lehine()
    {
        // 1 MiB'nin bir bayt eksiği 0 MiB'dir: kimsenin yazmadığı baytı kotadan düşmeyiz.
        Assert.Equal(0L, PanelUnits.BytesToMebibytes(Mebibyte - 1));
        Assert.Equal(1L, PanelUnits.BytesToMebibytes(Mebibyte));
        Assert.Equal(1L, PanelUnits.BytesToMebibytes((2 * Mebibyte) - 1));
    }

    /// <summary>
    /// WHMCS'e giden sayı ile kotayı UYGULAYAN sayı aynı tabandan gelmeli.
    /// </summary>
    /// <remarks>
    /// <c>BillingUnits</c> artık kendi sabitini tutmuyor, <c>PanelUnits</c>'e devrediyor. Bu test
    /// devretmenin GERÇEKTEN yapıldığını ölçer: biri geri alınıp kendi sabitini yazsaydı, aşağıdaki
    /// eşitliklerden biri bozulurdu.
    /// </remarks>
    [Fact]
    public void Faturalama_ayni_tabandan_besleniyor()
    {
        Assert.Equal(PanelUnits.BytesToMebibytes(2_000_000L), BillingUnits.BytesToMegabytes(2_000_000L));
        Assert.Equal(PanelUnits.GibibytesToMebibytes(1), BillingUnits.GigabytesToMegabytes(1));
        Assert.Equal(1024L, BillingUnits.GigabytesToMegabytes(1));
    }

    /// <summary>
    /// TEKRAR GERİ GELMESİN: kota dosyalarında ham ölçek çarpanı kalmamalı.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kapsam BİLEREK dar: panelde <c>1024 * 1024</c> yazan başka yerler var (yükleme sınırı,
    /// Dropbox parça boyu, günlük döndürme aralığı) ve onlar kota ölçeği DEĞİL — hepsini yasaklayan
    /// bir tarama yanlış yerde kırmızı yakardı. Bu test yalnızca tabanı PanelUnits'e devredilmiş
    /// DÖRT dosyaya bakar.
    /// </para>
    /// <para>
    /// Kırmızı yanarsa anlamı: bu dosyalardan biri tabanı yeniden elle türetmeye başlamış.
    /// Çözümü çarpanı gizlemek değil, <c>PanelUnits</c>'teki uygun fonksiyonu çağırmaktır.
    /// </para>
    /// </remarks>
    [Fact]
    public void Kota_dosyalarinda_HAM_olcek_carpani_kalmadi()
    {
        foreach (var (ad, yol) in KotaDosyalari())
        {
            var kaynak = YorumlariSil(File.ReadAllText(yol));

            // Kendi kendini denetleyen ön koşul: devir GERÇEKTEN yapıldıysa dosya PanelUnits'i
            // anıyor olmalı. Bu satır olmasaydı, dosya yeniden adlandırıldığında tarama boş bir
            // metin okuyup "ham çarpan yok" diye YEŞİL kalırdı.
            Assert.True(
                kaynak.Contains("PanelUnits", StringComparison.Ordinal),
                $"{ad} artik PanelUnits'i anmiyor: taban devri geri mi alindi?");

            var hamCarpanlar = Regex.Matches(kaynak, @"1024\s*L?\s*\*\s*1024")
                .Select(e => e.Value)
                .ToList();

            Assert.True(
                hamCarpanlar.Count == 0,
                $"{ad} icinde kota olcek carpani ELLE yeniden yazilmis ({hamCarpanlar.Count} yer). "
                + "Taban tek kaynaktan gelmeli: AdaPanel.Core.Common.PanelUnits.");
        }
    }

    /// <summary>
    /// Yukarıdaki taramanın bir şey ölçtüğünün kanıtı — sentetik girdiyle.
    /// </summary>
    /// <remarks>
    /// Tarama bugün dört dosya da temiz olduğu için geçer; regex hiç eşleşmeseydi de geçerdi.
    /// Kapsamın kendisi ölçülmeli, yoksa koruma sessizce boşa düşer.
    /// </remarks>
    [Fact]
    public void Ham_carpan_taramasi_gercekten_yakaliyor()
    {
        const string Kirli = "var tavan = (long)maxDiskMb * 1024L * 1024L;";
        const string Temiz = "var tavan = PanelUnits.MebibytesToBytes(maxDiskMb);";

        Assert.Matches(@"1024\s*L?\s*\*\s*1024", Kirli);
        Assert.DoesNotMatch(@"1024\s*L?\s*\*\s*1024", Temiz);

        // Yorum temizleyici de ölçülür: gerekçe yorumlarında eski kod ÖRNEK olarak geçebilir
        // (FileQuotaGuard'da gerçekten geçiyor) ve tarama onu gerçek kod sanmamalı.
        Assert.DoesNotMatch(@"1024\s*L?\s*\*\s*1024", YorumlariSil("// eski: file.Length / (1024 * 1024);"));
        Assert.DoesNotMatch(@"1024\s*L?\s*\*\s*1024", YorumlariSil("/* var x = 1024 * 1024; */"));
    }

    private static (string Ad, string Yol)[] KotaDosyalari()
        => new[]
        {
            ("CustomerScopeService.cs", Path.Combine("src", "AdaPanel.Infrastructure", "Security", "CustomerScopeService.cs")),
            ("SubscriptionEnforcementService.cs", Path.Combine("src", "AdaPanel.Infrastructure", "Subscriptions", "SubscriptionEnforcementService.cs")),
            ("FileQuotaGuard.cs", Path.Combine("src", "AdaPanel.Infrastructure", "Files", "FileQuotaGuard.cs")),
            ("BillingUnits.cs", Path.Combine("src", "AdaPanel.Infrastructure", "Billing", "BillingUnits.cs"))
        }
        .Select(e => (e.Item1, Path.Combine(KaynakKoku(), e.Item2)))
        .ToArray();

    /// <summary>Blok ve satır yorumlarını siler (dize içindeki <c>//</c> korunur).</summary>
    private static string YorumlariSil(string kaynak)
    {
        var metin = Regex.Replace(kaynak, @"/\*.*?\*/", " ", RegexOptions.Singleline);

        var satirlar = metin.Split('\n').Select(satir =>
        {
            var tirnak = 0;
            for (var i = 0; i + 1 < satir.Length; i++)
            {
                if (satir[i] is '"' or '\'')
                {
                    tirnak++;
                    continue;
                }

                if (satir[i] == '/' && satir[i + 1] == '/' && tirnak % 2 == 0)
                {
                    return satir[..i];
                }
            }

            return satir;
        });

        return string.Join("\n", satirlar);
    }

    private static string KaynakKoku()
    {
        var yol = AppContext.BaseDirectory;
        while (yol is not null && !File.Exists(Path.Combine(yol, "AdaPanel.sln")))
        {
            yol = Path.GetDirectoryName(yol);
        }

        Assert.NotNull(yol);
        return yol!;
    }
}
