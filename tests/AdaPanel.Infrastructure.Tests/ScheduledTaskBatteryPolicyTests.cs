using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// "ILK GUN CALISIR, SONRA SESSIZCE OLUR" sinifi — pil ayagi.
///
/// <para>
/// <b>Neden var.</b> <c>New-ScheduledTaskSettingsSet</c>'in varsayilanlari DIZUSTU BILGISAYAR icin
/// secilmistir; bir sunucu zamanlayicisi icin yanlistir. Olculdu (varsayilan hal):
/// <c>DisallowStartIfOnBatteries=True</c>, <c>StopIfGoingOnBatteries=True</c>. Sanallastirilmis
/// sunucular (Hyper-V, bircok VPS) sik sik bir batarya/UPS aygiti bildirir; oyle bir makinede
/// Gorev Zamanlayici gorevi BASLATMAYI REDDEDER — kullanicinin gordugu kod
/// <c>0x800710E0</c> (ERROR_OPERATOR_OR_ADMINISTRATOR_HAS_REFUSED_THE_REQUEST).
/// </para>
/// <para>
/// <b>Neden dosya bazli degil SINIF bazli tarama.</b> Bu ayni hata cron yolunda bir kez olculup
/// duzeltildi (<c>SchedulerService</c>), ama duzeltme yalnizca O cagri yerine uygulandi. Denetimde
/// <c>WindowsUpdateInstallerLauncher</c> hala ciplak varsayilanlarla kayit yapiyordu ve bu makinede
/// kayitli gercek <c>AdaPanel-SelfUpdate</c> gorevi okundugunda ikisi de <c>True</c> cikti. Zincirin
/// tamami sessiz: <c>Register-ScheduledTask</c> basarili doner, arayuz "panel yeniden baslatiliyor"
/// der, hicbir sey olmaz — ve bu GUNCELLEME yolu oldugu icin musteri bir daha hicbir duzeltme
/// alamaz. Tek bir dosyayi kontrol eden test bunu YAKALAYAMAZDI; o yuzden kaynak agacindaki HER
/// cagri yeri taraniyor: bir sonraki gorev kaydi da dogustan bu kurala uyar.
/// </para>
/// </summary>
public sealed class ScheduledTaskBatteryPolicyTests
{
    private const string Cmdlet = "New-ScheduledTaskSettingsSet";

    /// <summary>
    /// Kaynak agacindaki her <c>New-ScheduledTaskSettingsSet</c> cagrisi pil bayraklarini
    /// ACIKCA vermeli.
    /// </summary>
    /// <remarks>
    /// Tarama SATIR KAPSAMLIDIR: bayraklar cmdlet ile ayni satirda aranir. Bir yazar cagriyi
    /// satirlara bolerse test KIRMIZI yanar — bilerek boyle: yanlis yonde sessiz kalmaktansa
    /// yanlis yonde gurultu cikarsin, cunku bu korumanin varlik sebebi tam da sessizlikti.
    /// </remarks>
    [Fact]
    public void Her_gorev_ayari_pil_modunda_calismaya_izin_verir()
    {
        var kaynakDizini = Path.Combine(KaynakKoku(), "src");
        Assert.True(Directory.Exists(kaynakDizini), $"kaynak dizini bulunamadi: {kaynakDizini}");

        var ihlaller = new List<string>();
        var bulunanCagri = 0;

        foreach (var dosya in Directory.EnumerateFiles(kaynakDizini, "*.cs", SearchOption.AllDirectories))
        {
            var satirlar = File.ReadAllLines(dosya);
            for (var i = 0; i < satirlar.Length; i++)
            {
                var satir = satirlar[i];
                if (!satir.Contains(Cmdlet, StringComparison.Ordinal) || YorumSatiri(satir))
                {
                    continue;
                }

                bulunanCagri++;
                if (!Eksikler(satir).Any())
                {
                    continue;
                }

                ihlaller.Add(
                    $"{Path.GetRelativePath(KaynakKoku(), dosya)}:{i + 1} eksik: "
                    + string.Join(", ", Eksikler(satir)));
            }
        }

        // Taramanin KENDISI olculuyor: cagri yeri bulunamadiysa test hicbir sey kanitlamamis
        // demektir (dosya tasindi, cmdlet adi degisti). Sessizce yesil yanmasin.
        Assert.True(
            bulunanCagri > 0,
            $"'{Cmdlet}' cagrisi hic bulunamadi; bu koruma artik bir sey olcmuyor.");

        Assert.True(
            ihlaller.Count == 0,
            "Zamanlanmis gorev ayarlari Windows'un dizustu varsayilanlarina birakilmis. "
            + "Batarya/UPS bildiren bir sunucuda gorev 0x800710E0 ile REDDEDILIR:\r\n"
            + string.Join("\r\n", ihlaller));
    }

    /// <summary>
    /// Taramanin GERCEKTEN yakaladiginin kaniti — sentetik girdiyle.
    /// </summary>
    /// <remarks>
    /// Bu depoda ayni oturumda UC koruma dogustan olu cikti; hepsi yesil gorunuyordu. Bir testin
    /// var olmasi bir sey olctugu anlamina gelmez. Asagisi, duzeltilen SATIRIN eski hali ile yeni
    /// halini yan yana koyup denetcinin ikisini AYIRT ETTIGINI gosterir.
    /// </remarks>
    [Fact]
    public void Denetci_ciplak_varsayilani_gercekten_yakaliyor()
    {
        const string EskiHal = "$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Hours 2) -StartWhenAvailable";
        const string YeniHal = "$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 2) -StartWhenAvailable";

        Assert.Equal(
            ["-AllowStartIfOnBatteries", "-DontStopIfGoingOnBatteries"],
            Eksikler(EskiHal));
        Assert.Empty(Eksikler(YeniHal));

        // Tek bayragin unutulmasi da yakalanmali; "ikisinden biri varsa gec" bos bir guvence olurdu.
        Assert.Equal(
            ["-DontStopIfGoingOnBatteries"],
            Eksikler("New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -StartWhenAvailable"));

        // Yorum satiri (cmdlet adini ANLATAN metin) ihlal sayilmamali.
        Assert.True(YorumSatiri("        // (New-ScheduledTaskSettingsSet, varsayilan hali):"));
        Assert.True(YorumSatiri("            # New-ScheduledTaskSettingsSet'in varsayilanlari"));
        Assert.False(YorumSatiri("            $settings = New-ScheduledTaskSettingsSet -Foo"));
    }

    private static string[] Eksikler(string satir) =>
        new[] { "-AllowStartIfOnBatteries", "-DontStopIfGoingOnBatteries" }
            .Where(bayrak => !satir.Contains(bayrak, StringComparison.Ordinal))
            .ToArray();

    /// <summary>
    /// C# (<c>//</c>, <c>*</c>) ve gomulu PowerShell (<c>#</c>) yorum satirlarini eler: cmdlet'i
    /// ANLATAN metin bir cagri degildir.
    /// </summary>
    private static bool YorumSatiri(string satir)
    {
        var kirpik = satir.TrimStart();
        return kirpik.StartsWith("//", StringComparison.Ordinal)
            || kirpik.StartsWith("#", StringComparison.Ordinal)
            || kirpik.StartsWith("*", StringComparison.Ordinal);
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
