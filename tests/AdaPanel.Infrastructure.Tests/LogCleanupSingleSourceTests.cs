using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// GÜNLÜK SİLEN TEK BİR KOD OLMALI (denetim Y6).
/// </summary>
/// <remarks>
/// <para>
/// <b>ÖLÇÜLEN AÇIK:</b> panelde günlük silen İKİ kod vardı ve <b>aynı klasörlere</b> bakıyorlardı.
/// <c>LogRotationService</c> her silmeyi <c>LogFileGuard</c>'dan geçiriyordu: yalnız doğrudan alt
/// dosyalar, uzantı beyaz listesi, <c>ReparsePoint</c> reddi, <c>FileShare.None + DeleteOnClose</c>,
/// özyineleme YOK. <c>AutomationService</c> ise aynı klasörü korumasız ikinci kopyaya
/// (<c>SiteLogCleanup</c>) veriyordu:
/// </para>
/// <code>
/// Directory.EnumerateFiles(directory, "*.log", SearchOption.AllDirectories)  // özyinelemeli
/// ... info.Delete();                                                        // korumasız
/// </code>
/// <para>
/// Ve ölçüldü: özyinelemeli sayım kavşağı (junction) <b>TAKİP EDİYOR</b>. Bir sitenin
/// <c>W3SVC&lt;id&gt;</c> klasörü müşterinin klasörüne bağlanmışsa gece otomasyonu müşterinin
/// dosyalarını siliyordu — <c>LogFileGuard</c>'ın belgesinde adıyla anılan, bu depoda bir kez
/// yaşanmış hata sınıfı. Sıkı kapı dururken korumasız ikinci kopyayı yaşatmak, korumayı
/// sıfırlamaktır.
/// </para>
/// <para>
/// Bu testler "düzeltildi mi"yi değil, <b>ikinci kopyanın geri gelmediğini</b> korur.
/// </para>
/// </remarks>
public sealed class LogCleanupSingleSourceTests
{
    /// <summary>Korumasız ikinci kopya depoda ARTIK YOK.</summary>
    [Fact]
    public void TheUnguardedSecondLogDeleter_NoLongerExists()
    {
        var kok = DepoKoku();

        Assert.False(
            File.Exists(Path.Combine(kok, "src", "AdaPanel.Infrastructure", "Automation", "SiteLogCleanup.cs")),
            "SiteLogCleanup geri gelmis: korumasiz, ozyinelemeli ikinci silme yolu.");
    }

    /// <summary>Otomasyon artık kendi silmesini yapmıyor; tek kaynağa delege ediyor.</summary>
    [Fact]
    public void TheAutomationJob_DelegatesToTheSingleRotationService()
    {
        var kaynak = OkuKaynak("src", "AdaPanel.Infrastructure", "Automation", "AutomationService.cs");

        Assert.Contains("Logs.ILogRotationService logRotationService", kaynak, StringComparison.Ordinal);
        Assert.Contains("await logRotationService.RunAsync(null, cancellationToken)", kaynak, StringComparison.Ordinal);

        // YORUMLAR HARİÇ: kaldırılan kopyanın adı gerekçe yorumunda BİLEREK geçiyor; testin
        // koruduğu şey o metin değil, ÇALIŞAN KODUN kendisi.
        Assert.DoesNotContain("SiteLogCleanup", YorumsuzKod(kaynak), StringComparison.Ordinal);
    }

    /// <summary>
    /// Otomasyonun HİÇBİR YERİNDE kendi başına dosya silen bir yol kalmamalı — ikinci kopya
    /// çoğu zaman "küçük bir yardımcı" olarak geri gelir.
    /// </summary>
    [Fact]
    public void TheAutomationService_DeletesNoFileOnItsOwn()
    {
        var kod = YorumsuzKod(OkuKaynak("src", "AdaPanel.Infrastructure", "Automation", "AutomationService.cs"));

        Assert.DoesNotContain("SearchOption.AllDirectories", kod, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Delete(", kod, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.Delete(", kod, StringComparison.Ordinal);
    }

    /// <summary>
    /// Politika KAPALIYKEN "Tamamlandı" denmez: hiçbir şey yapmadan başarı raporlamak, tam olarak
    /// bu denetimin kapatmaya çalıştığı yalan sınıfıdır.
    /// </summary>
    [Fact]
    public void WhenTheRotationPolicyIsDisabled_TheAutomationJobSaysSo()
    {
        var kaynak = OkuKaynak("src", "AdaPanel.Infrastructure", "Automation", "AutomationService.cs");

        Assert.Contains("logs.rotation_disabled", kaynak, StringComparison.Ordinal);
        Assert.Contains("Log rotation policy is disabled", kaynak, StringComparison.Ordinal);
    }

    /// <summary>Silme kapısı hâlâ tek yerde: <c>LogFileGuard</c>.</summary>
    [Fact]
    public void TheRotationService_StillDeletesOnlyThroughTheGuard()
    {
        var kaynak = OkuKaynak("src", "AdaPanel.Infrastructure", "Logs", "LogRotationService.cs");

        Assert.Contains("LogFileGuard.SafeDelete", kaynak, StringComparison.Ordinal);
        Assert.DoesNotContain("SearchOption.AllDirectories", YorumsuzKod(kaynak), StringComparison.Ordinal);
    }

    /// <summary>
    /// Metin denetiminin kırılganlığına karşı: yalnız ÇALIŞAN kod satırlarına bak. Gerekçe
    /// yorumları kaldırılan kodun adını anmak zorunda — yoksa "neden böyle" bilgisi kaybolur.
    /// </summary>
    private static string YorumsuzKod(string kaynak) =>
        string.Join(
            '\n',
            kaynak
                .ReplaceLineEndings("\n")
                .Split('\n')
                .Where(satir =>
                {
                    var kirpik = satir.TrimStart();
                    return !kirpik.StartsWith("//", StringComparison.Ordinal)
                           && !kirpik.StartsWith("*", StringComparison.Ordinal)
                           && !kirpik.StartsWith("/*", StringComparison.Ordinal);
                }));

    private static string DepoKoku()
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

        throw new DirectoryNotFoundException("Depo koku bulunamadi.");
    }

    private static string OkuKaynak(params string[] parcalar) =>
        File.ReadAllText(Path.Combine(new[] { DepoKoku() }.Concat(parcalar).ToArray()));
}
