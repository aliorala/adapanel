using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Seo;
using AdaPanel.Infrastructure.Statistics;
using Xunit;
using Xunit.Abstractions;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// W3C AYRIŞTIRICISI PANELDE TEK OLMALI — ve akış hâlinde çalışmalı.
/// </summary>
/// <remarks>
/// <para>
/// <b>Korunan hata sınıfı:</b> "aynı kuralın ikinci kopyası". Bu depoda beş kez ısırdı (motor
/// listesi, hedef türü listesi, günlük temizliği, ayrılmış aygıt adları, denetim kapsamı). Sütun
/// eşlemesi tam da o türden bir kuraldır: IIS'te alan kümesi yöneticinin seçimidir ve
/// <c>#Fields</c> satırı dosyanın ORTASINDA yeniden yazılabilir. İki kopyadan biri bunu unutursa
/// o modül SESSİZCE yanlış sütunu okur — hata vermez, yalnızca yanlış cevap verir.
/// </para>
/// </remarks>
public sealed class W3cLogReaderSingleSourceTests(ITestOutputHelper output)
{
    /// <summary>
    /// <b>NÖBETÇİ:</b> <c>#Fields</c> yönergesini kaynak içinde başka kimse ayrıştırmamalı.
    /// Yeni bir modül kendi ayrıştırıcısını yazarsa bu test onu hemen görünür kılar.
    /// </summary>
    [Fact]
    public void Panelde_W3C_alan_basligini_ayristiran_TEK_yer_var()
    {
        var kok = Kok();
        var desen = new Regex(@"""#Fields:?""|#Fields:\""|StartsWith\(\s*""#Fields", RegexOptions.Compiled);

        var yerler = new List<string>();
        foreach (var dosya in Directory.EnumerateFiles(Path.Combine(kok, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (dosya.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                dosya.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            if (desen.IsMatch(File.ReadAllText(dosya)))
            {
                yerler.Add(Path.GetFileName(dosya));
            }
        }

        Assert.Equal(["W3cLogReader.cs"], yerler.OrderBy(item => item, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// İSTATİSTİK EKRANI DA AYNI AYRIŞTIRICIYI KULLANIR. Ortak kaynağa çıkarmanın tek anlamlı
    /// ölçüsü budur; yeni bir dosya eklenip eskisi olduğu gibi bırakılsaydı iki kopya olurdu.
    /// </summary>
    [Fact]
    public void Istatistik_servisi_ortak_ayristiriciyi_kullanir()
    {
        var kaynak = File.ReadAllText(Path.Combine(
            Kok(), "src", "AdaPanel.Infrastructure", "Statistics", "WebStatisticsService.cs"));

        Assert.Contains("W3cLogReader.Read", kaynak, StringComparison.Ordinal);
        Assert.Contains("W3cLogReader.ReadAsync", kaynak, StringComparison.Ordinal);

        // Kendi eşlemesini kurmamalı.
        Assert.DoesNotContain("BuildFieldIndex", kaynak, StringComparison.Ordinal);
        Assert.DoesNotContain("Dictionary<string, int>", kaynak, StringComparison.Ordinal);
    }

    /// <summary>
    /// İSTATİSTİĞİN DAVRANIŞI DEĞİŞMEDİ: aynı satırlar, aynı sayılar. Ortak kaynağa çıkarma
    /// ancak var olan davranış SABİT KALDIĞINDA doğru yapılmıştır.
    /// </summary>
    [Fact]
    public void Istatistik_ciktisi_ayristirici_ortaklastiktan_sonra_da_ayni()
    {
        var satirlar = new[]
        {
            "#Software: Microsoft Internet Information Services 10.0",
            "#Fields: date time c-ip cs-uri-stem cs(Referer) sc-status sc-bytes",
            "2026-08-19 08:00:00 1.1.1.1 /a https://ornek.com/x 200 1000",
            "2026-08-19 08:00:01 1.1.1.1 /a - 404 50",
            "2026-08-19 08:00:02 2.2.2.2 /b https://ornek.com/y 500 10",
            "2026-08-18 07:00:00 3.3.3.3 /eski - 200 10"
        };

        var sonuc = WebStatisticsService.AnalyzeLines(satirlar, new DateTime(2026, 8, 19, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(3, sonuc.TotalRequests);
        Assert.Equal(2, sonuc.UniqueVisitors);
        Assert.Equal(1060, sonuc.TotalBytes);
        Assert.Equal(1, sonuc.Status2xx);
        Assert.Equal(1, sonuc.Status4xx);
        Assert.Equal(1, sonuc.Status5xx);
        Assert.Equal("/a", sonuc.TopPages.First().Key);
        Assert.Equal("ornek.com", sonuc.TopReferrers.Single().Key);
    }

    /// <summary>
    /// <b>ÖLÇÜM — bu depoda "uzun sürer mi" sorusu VARSAYIMLA cevaplanmaz.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Log döndürmenin ölçümünde tek bir sitenin BİR GÜNLÜK IIS erişim günlüğü 38,3 MB'tı. Bu test
    /// gerçek biçimde bir günlük üretir, hem ham hem ZSTD arşivinden akıtarak okur ve süreyi
    /// yazdırır. Amaç bir eşiği kovalamak DEĞİL (yük altında kırılgan test üretirdi); büyüklük
    /// mertebesini kayda geçirmek: analiz saniyeler mertebesindeyse ayrı bir "çalışan iş" kaydı
    /// gerekmez, dakikalar mertebesindeyse gerekir.
    /// </para>
    /// <para>
    /// Eşik BİLEREK çok gevşek (satır başına 100 mikrosaniye = beklenenin ~20 katı): burada
    /// korunan şey performans değil, <b>akış hâlinde okuma</b>. Dosya belleğe alınmaya
    /// başlansaydı bu test önce yavaşlar, sonra bellek yetmediğinde düşerdi.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Buyuk_gunluk_akis_halinde_okunur_ve_sure_kayda_gecer()
    {
        const int satirSayisi = 200_000;
        var klasor = Path.Combine(Path.GetTempPath(), "ada-w3c-olcum-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(klasor);

        try
        {
            var ham = Path.Combine(klasor, "u_ex260819.log");
            await UretAsync(ham, satirSayisi);
            var boyut = new FileInfo(ham).Length;

            var (hamSure, hamSayi) = await OkuAsync(ham);

            var arsiv = ham + BackupDumpCompression.CompressedExtension;
            await BackupDumpCompression.CompressFileAsync(ham, arsiv);
            var (arsivSure, arsivSayi) = await OkuAsync(arsiv);

            output.WriteLine(
                $"W3C okuma olcumu: {satirSayisi:N0} satir / {boyut / (1024.0 * 1024.0):F1} MB -> " +
                $"ham {hamSure.TotalSeconds:F2} sn, ZSTD arsiv {arsivSure.TotalSeconds:F2} sn " +
                $"(arsiv {new FileInfo(arsiv).Length / (1024.0 * 1024.0):F1} MB).");

            Assert.Equal(satirSayisi, hamSayi);
            Assert.Equal(satirSayisi, arsivSayi);

            // Akış hâlinde okunduğunun kaba kanıtı: satır başına 100 mikrosaniyeyi aşmıyor.
            Assert.True(
                hamSure.TotalMilliseconds < satirSayisi * 0.1,
                $"Ham gunluk okumasi beklenenden cok yavas: {hamSure.TotalSeconds:F2} sn / {satirSayisi} satir.");
            Assert.True(
                arsivSure.TotalMilliseconds < satirSayisi * 0.1,
                $"Arsiv okumasi beklenenden cok yavas: {arsivSure.TotalSeconds:F2} sn / {satirSayisi} satir.");
        }
        finally
        {
            try
            {
                Directory.Delete(klasor, recursive: true);
            }
            catch (IOException)
            {
                // Geçici klasör silinemediyse test sonucu değişmez.
            }
        }
    }

    private static async Task UretAsync(string path, int satirSayisi)
    {
        const string ua = "Mozilla/5.0+(compatible;+Googlebot/2.1;++http://www.google.com/bot.html)";
        await using var writer = new StreamWriter(path, append: false, Encoding.UTF8);

        await writer.WriteLineAsync("#Software: Microsoft Internet Information Services 10.0");
        await writer.WriteLineAsync("#Version: 1.0");
        await writer.WriteLineAsync("#Date: 2026-08-19 00:00:00");
        await writer.WriteLineAsync(
            "#Fields: date time s-ip cs-method cs-uri-stem cs-uri-query s-port cs-username c-ip cs(User-Agent) cs(Referer) sc-status sc-substatus sc-win32-status time-taken");

        for (var index = 0; index < satirSayisi; index++)
        {
            await writer.WriteLineAsync(
                $"2026-08-19 {index / 3600 % 24:D2}:{index / 60 % 60:D2}:{index % 60:D2} 10.0.0.5 GET " +
                $"/urun/{index % 5000} - 443 - 66.249.66.{index % 250 + 1} {ua} - 200 0 0 {index % 500}");
        }
    }

    private static async Task<(TimeSpan Sure, int Sayi)> OkuAsync(string path)
    {
        var analyzer = new SeoLogAnalyzer(new DateTime(2026, 8, 19, 0, 0, 0, DateTimeKind.Utc));
        var saat = Stopwatch.StartNew();
        var sayi = 0;

        using (var reader = SeoLogFileSet.OpenText(path))
        {
            await foreach (var record in W3cLogReader.ReadAsync(reader))
            {
                sayi++;
                analyzer.Add(record);
            }
        }

        saat.Stop();
        Assert.Equal(sayi, (int)analyzer.BotRequests);
        return (saat.Elapsed, sayi);
    }

    private static string Kok()
    {
        var klasor = AppContext.BaseDirectory;
        for (var derinlik = 0; derinlik < 8; derinlik++)
        {
            if (Directory.Exists(Path.Combine(klasor, "src")) && Directory.Exists(Path.Combine(klasor, "frontend")))
            {
                return klasor;
            }

            klasor = Path.GetFullPath(Path.Combine(klasor, ".."));
        }

        throw new DirectoryNotFoundException("Depo koku bulunamadi.");
    }
}
