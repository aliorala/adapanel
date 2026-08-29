using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Ftp;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// FTP EV KLASÖRÜ KAYITTAN DEĞİL DİSKTEN OKUNUR.
///
/// <para>
/// <b>Neden var — canlı sunucuda ölçülmüş kör nokta.</b> Müşteri FTP'ye bağlanamıyordu ve panel
/// FTP ekranında kullanıcı SAPASAĞLAM görünüyordu: <c>ExistsOnSystem</c> true (Windows hesabı
/// gerçekten duruyordu), <c>HomePath</c> dolu. Sunucu ise her denemede
/// <c>530 User cannot log in, home directory inaccessible.</c> dönüyordu. Panelin gösterdiği
/// hiçbir alan bunu yakalayamıyordu, çünkü hiçbiri eve BAKMIYORDU — <c>HomePath</c> bir KAYITTIR.
/// </para>
/// <para>
/// <b>En sinsi hâli — boşa düşmüş junction.</b> IIS FTP izolasyonu evi
/// <c>&lt;ftp-kök&gt;\LocalUser\&lt;kullanıcı&gt;</c> yolunda arar; panel oraya siteye giden bir
/// junction koyar. Site silindiğinde junction GERİDE KALIR: <c>Directory.Exists</c> onun için
/// <c>true</c> döner ve yüzeysel bir denetim "ev var" der — ama gösterdiği yer yoktur. Bu yüzden
/// bağlantının varlığı ile hedefinin varlığı İKİ AYRI ölçümdür.
/// </para>
/// </summary>
public sealed class FtpHomeProbeTests : IDisposable
{
    private readonly string kok = Path.Combine(Path.GetTempPath(), "ada-ftp-probe-" + Guid.NewGuid().ToString("N"));

    private FtpService Servis()
    {
        // ProbeHome yalnizca dataRoot'a bakar; kalan bagimliliklar bu olcum icin kullanilmaz.
        // TAM NITELIKLI: `AdaPanel.Infrastructure.Options` ad alani, `Microsoft.Extensions.Options`
        // icindeki `Options` sinifini golgeliyor.
        var secenekler = Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = kok });
        return new FtpService(null!, null!, null!, secenekler);
    }

    private string EvYolu(string kullanici) => Path.Combine(kok, "ftp-root", "LocalUser", kullanici);

    [Fact]
    public void Baglanti_hic_yoksa_yok_denir_hedefi_ise_SORULMAZ()
    {
        var sonuc = Servis().ProbeHome("olmayan");

        Assert.Equal(EvYolu("olmayan"), sonuc.LinkPath);
        Assert.False(sonuc.LinkExists);

        // Hedef "yok" DEGIL, SORULMADI: bagli olmayan bir yolun hedefi diye bir sey yoktur.
        // false donseydi arayuz "hedef silinmis" diye YANLIS bir sebep gosterirdi.
        Assert.Null(sonuc.TargetExists);
    }

    [Fact]
    public void Duz_klasor_ev_olarak_gecerlidir()
    {
        Directory.CreateDirectory(EvYolu("duz"));

        var sonuc = Servis().ProbeHome("duz");

        Assert.True(sonuc.LinkExists);
        Assert.True(sonuc.TargetExists);
    }

    [Fact]
    public void Junction_hedefi_DURUYORSA_saglam()
    {
        var hedef = Path.Combine(kok, "site-saglam");
        Directory.CreateDirectory(hedef);
        JunctionKur(EvYolu("saglam"), hedef);

        var sonuc = Servis().ProbeHome("saglam");

        Assert.True(sonuc.LinkExists);
        Assert.True(sonuc.TargetExists);
    }

    /// <summary>
    /// BU TESTIN ASIL SEBEBI: site silinince junction geride kalir ve "var" gorunur.
    /// </summary>
    [Fact]
    public void Junction_hedefi_SILINMISSE_bagalanti_var_gorunse_bile_hedef_YOK()
    {
        var hedef = Path.Combine(kok, "site-silinecek");
        Directory.CreateDirectory(hedef);
        var ev = EvYolu("bosa-dusmus");
        JunctionKur(ev, hedef);

        // Site silinir; junction'a DOKUNULMAZ -- gercek hayatta olan tam olarak budur.
        Directory.Delete(hedef, recursive: true);

        var sonuc = Servis().ProbeHome("bosa-dusmus");

        // Yuzeysel denetim burada "ev var" derdi; kirik kurulumu saglam gostermenin yolu budur.
        Assert.True(sonuc.LinkExists);
        Assert.False(sonuc.TargetExists);
    }

    /// <summary>Junction'i mklink ile kurar (yonetici yetkisi GEREKTIRMEZ; sembolik bag gerektirirdi).</summary>
    private static void JunctionKur(string bag, string hedef)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(bag)!);

        var islem = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{bag}\" \"{hedef}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;

        islem.WaitForExit(15000);

        // Kurulamadiysa test SESSIZCE gecmesin: olcemedigimiz bir seyi "dogru" saymak, tam da bu
        // testin kapatmaya calistigi hatadir.
        Assert.True(Directory.Exists(bag), $"junction kurulamadi: {bag} -> {hedef}");
    }

    public void Dispose()
    {
        if (!Directory.Exists(kok))
        {
            return;
        }

        // Junction'lar HEDEFE DOKUNMADAN silinmeli: Directory.Delete(recursive) bazi durumlarda
        // bagin ICINE girer. Once baglar tek tek koparilir.
        foreach (var yol in Directory.EnumerateDirectories(kok, "*", SearchOption.AllDirectories).Reverse())
        {
            try
            {
                if (Directory.ResolveLinkTarget(yol, returnFinalTarget: false) is not null)
                {
                    Directory.Delete(yol, recursive: false);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        try { Directory.Delete(kok, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
