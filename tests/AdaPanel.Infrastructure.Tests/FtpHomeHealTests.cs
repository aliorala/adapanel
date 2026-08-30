using AdaPanel.Infrastructure.Ftp;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// ESKI FTP KULLANICILARI DA IYILESTIRILIR — yoksa duzeltme YARIM kalir.
///
/// <para>
/// <b>Neden var — kurulum sonrasi olcumle bulundu.</b> IIS'in hangi klasor duzenini aradigi
/// makineye gore degisiyor, o yuzden panel ev klasorunu IKI yola birden kurar. Ama bu, duzeltme
/// yazildiktan SONRA acilan kullanicilar icin gecerliydi: makinede zaten duran hesaplar tek
/// yollu kaldigi icin guncellemeden sonra da FTP'ye giremiyorlardi.
/// </para>
/// <para>
/// Kurulum dogrulama betigi (<c>scripts/kurulum-dogrula.ps1</c>) 0.8.4 kurulumundan sonra tam
/// bunu raporladi: iki mevcut kullanicida makine adli yol YOKTU. Bir hatayi duzeltip mevcut
/// kurulumlari iyilestirmemek, hatanin yarisini birakmaktir -- musteri "guncelledim ama
/// duzelmedi" der ve HAKLI olur.
/// </para>
/// </summary>
public sealed class FtpHomeHealTests : IDisposable
{
    private readonly string kok = Path.Combine(Path.GetTempPath(), "ada-ftp-heal-" + Guid.NewGuid().ToString("N"));

    private FtpService Servis()
    {
        var secenekler = Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = kok });
        return new FtpService(null!, new JsonFileStore(secenekler), null!, secenekler);
    }

    private string Yol(string alt, string kullanici)
        => Path.Combine(kok, "ftp-root", alt, kullanici);

    /// <summary>
    /// KAPATILAN BOSLUGUN TA KENDISI: tek yollu eski kullanici, ikinci yolu kazanir.
    /// </summary>
    [Fact]
    public async Task Eksik_ikinci_yol_VAR_OLAN_hedefe_kurulur()
    {
        var hedef = Path.Combine(kok, "site");
        Directory.CreateDirectory(hedef);
        await KayitYazAsync("eski");
        JunctionKur(Yol("LocalUser", "eski"), hedef);

        var makineliYol = Yol(Environment.MachineName, "eski");
        Assert.False(Directory.Exists(makineliYol));

        var onarilan = await Servis().HealHomeLinksAsync(CancellationToken.None);

        Assert.Equal(1, onarilan);
        Assert.True(Directory.Exists(makineliYol));

        // Hedef AYNI olmali: ikinci yol yeni bir bos klasor DEGIL, ayni siteye baglanti.
        Assert.Equal(hedef, Directory.ResolveLinkTarget(makineliYol, returnFinalTarget: true)!.FullName);
    }

    /// <summary>
    /// HEDEFI OLMAYAN kullanici ATLANIR: bozuklugu cogaltmayiz.
    /// </summary>
    /// <remarks>
    /// Site silinince junction geride kalir ve hedefi yoktur. Ona ikinci bir baglanti kurmak,
    /// kirik bir kurulumu iki kat kirik yapmaktan baska bir sey degildir. Panel bu durumu zaten
    /// olcup "hedefi silinmis" diye gosteriyor (bkz. FtpHomeProbeTests).
    /// </remarks>
    [Fact]
    public async Task Hedefi_silinmis_kullanici_ATLANIR()
    {
        var hedef = Path.Combine(kok, "silinen-site");
        Directory.CreateDirectory(hedef);
        await KayitYazAsync("bosa-dusmus");
        JunctionKur(Yol("LocalUser", "bosa-dusmus"), hedef);
        Directory.Delete(hedef, recursive: true);

        var onarilan = await Servis().HealHomeLinksAsync(CancellationToken.None);

        Assert.Equal(0, onarilan);
        Assert.False(Directory.Exists(Yol(Environment.MachineName, "bosa-dusmus")));
    }

    /// <summary>
    /// Iki yolu da olan kullaniciya DOKUNULMAZ — onarim tekrar tekrar is yapmaz.
    /// </summary>
    [Fact]
    public async Task Zaten_saglam_kullanici_icin_hicbir_sey_yapilmaz()
    {
        var hedef = Path.Combine(kok, "saglam-site");
        Directory.CreateDirectory(hedef);
        await KayitYazAsync("saglam");
        JunctionKur(Yol("LocalUser", "saglam"), hedef);
        JunctionKur(Yol(Environment.MachineName, "saglam"), hedef);

        Assert.Equal(0, await Servis().HealHomeLinksAsync(CancellationToken.None));
    }

    /// <summary>Kayit yoksa onarim sessizce hicbir sey yapmaz (yeni kurulum).</summary>
    [Fact]
    public async Task Kullanici_yoksa_onarim_bos_gecer()
    {
        Assert.Equal(0, await Servis().HealHomeLinksAsync(CancellationToken.None));
    }

    private async Task KayitYazAsync(params string[] kullanicilar)
    {
        var secenekler = Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = kok });
        var store = new JsonFileStore(secenekler);
        var kayitlar = kullanicilar
            .Select(ad => new { userName = ad, homePath = "", siteName = "", createdAtUtc = DateTime.UtcNow, isEnabled = true })
            .ToArray();

        await store.WriteAsync("config/ftp-users.json", kayitlar, CancellationToken.None);
    }

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
        Assert.True(Directory.Exists(bag), $"junction kurulamadi: {bag}");
    }

    public void Dispose()
    {
        if (!Directory.Exists(kok))
        {
            return;
        }

        // Junction'lar HEDEFE DOKUNMADAN koparilir; recursive silme baglarin ICINE girebilir.
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
