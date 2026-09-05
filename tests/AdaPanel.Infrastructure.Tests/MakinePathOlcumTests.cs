using AdaPanel.Core.Dtos.Scheduler;
using AdaPanel.Infrastructure.Scheduler;
using AdaPanel.Infrastructure.Support;
using Xunit;
using Xunit.Abstractions;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// ADAY 4 -- <c>SchedulerService.AddPythonToMachinePathAsync</c>: HKLM PATH yazimi,
/// <c>-ErrorAction Stop</c> YOK, geri okuma YOK, ISTENEN sayi raporlaniyor.
/// </summary>
/// <remarks>
/// <para>
/// <b>OLCULEN HATA.</b> Basilan <c>ADAPANEL-DEGISIKLIK=</c> sayisi EKLENMESI ISTENEN klasor
/// sayisiydi; <c>CronController</c> tam onu "Python added to machine PATH (N folder(s))" diye
/// yaziyordu. Bu MAKINE GENELI bir yazmadir ve hicbir olcume dayanmiyordu.
/// </para>
/// <para>
/// <b>BU MAKINEDE GERCEKTEN OLCULDU.</b> Geri okuma parcasi GERCEK <c>powershell.exe</c>'de,
/// GERCEK HKLM PATH'i uzerinde kosturuluyor -- YALNIZ OKUYARAK. Hicbir sey yazilmiyor, hicbir
/// kayit degistirilmiyor (surec zaten YUKSELTILMEMIS).
/// </para>
/// <para>
/// <b>OLCULEMEYEN, ACIKCA.</b> HKLM'e GERCEK yazmanin (<c>Set-ItemProperty ... -ErrorAction
/// Stop</c>) davranisi OLCULMEDI: surec yukseltilmemis ve makine geneli PATH'i degistirmek bu
/// turun kapsami disindadir. OLCULEN sey, panelin sonucu NASIL OKUDUGUDUR.
/// </para>
/// </remarks>
public sealed class MakinePathOlcumTests(ITestOutputHelper cikti)
{
    private static async Task<string> SondaCiktisiAsync(params string[] klasorler)
    {
        var betik = RuntimeLocator.BuildMachinePathProbeScript(klasorler);
        var sonuc = await ProcessRunner.RunPowerShellAsync(betik);

        Assert.True(
            sonuc.Success,
            "Yalniz-okuyan sonda GERCEK powershell.exe'de dusmemeli. Hata: " + sonuc.StandardError);

        return sonuc.StandardOutput;
    }

    // =====================================================================================
    // 1) GERCEK POWERSHELL, GERCEK HKLM PATH -- YALNIZ OKUMA
    // =====================================================================================

    /// <summary>
    /// <c>C:\Windows</c> her Windows kurulumunda makine PATH'indedir. Sonda bunu BULMALI --
    /// yoksa "dogrulandi" dali hic calismiyor demektir (sessizce olu bir kod yolu).
    /// </summary>
    [Fact]
    public async Task PATH_te_OLAN_klasor_DOGRULANDI_sayilir()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var ciktiMetni = await SondaCiktisiAsync(windows);
        cikti.WriteLine(ciktiMetni);

        var olcum = MachinePathMeasurement.Olc(ciktiMetni);

        Assert.True(olcum.Measured);
        Assert.Equal(1, olcum.Expected);
        Assert.Equal(1, olcum.Verified);

        // FAZLA KAPATMA NOBETCISI: her sey yolundayken panel HALA sade "yapildi" demeli.
        Assert.True(olcum.Applied);
        Assert.Equal(string.Empty, olcum.NoticeKey);
    }

    /// <summary>
    /// DISKTE OLMAYAN bir klasor HEDEFE SAYILMAZ (var olmayan bir klasoru PATH'e koymayiz).
    /// Bu, "hedef 0 / dogrulanan 0" ile "hedef 2 / dogrulanan 0"i ayirt eder.
    /// </summary>
    [Fact]
    public async Task DISKTE_OLMAYAN_klasor_hedefe_sayilmaz()
    {
        var ciktiMetni = await SondaCiktisiAsync("C:\\adapanel-boyle-bir-klasor-yok-" + Guid.NewGuid().ToString("N"));
        cikti.WriteLine(ciktiMetni);

        var olcum = MachinePathMeasurement.Olc(ciktiMetni);

        Assert.True(olcum.Measured);
        Assert.Equal(0, olcum.Expected);
        Assert.Equal(0, olcum.Verified);

        // Hedef YOKSA "uygulandi" da denmez -- yoksa hicbir sey yapmayan bir kosum yesil olurdu.
        Assert.False(olcum.Applied);
    }

    /// <summary>
    /// ASIL VAKA: klasor DISKTE VAR ama PATH'te DEGIL. Eski kod bu durumda da "eklendi (N)"
    /// diyordu; artik EKSIK olarak olculuyor.
    /// </summary>
    [Fact]
    public async Task DISKTE_VAR_ama_PATH_te_YOK_ise_EKSIK_olculur()
    {
        // Diskte kesin var, makine PATH'inde kesin yok olan bir klasor: gecici klasorun kendisi.
        var gecici = Path.Combine(Path.GetTempPath(), "ada-path-olcum-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(gecici);

        var ciktiMetni = await SondaCiktisiAsync(gecici);
        cikti.WriteLine(ciktiMetni);

        var olcum = MachinePathMeasurement.Olc(ciktiMetni);

        Assert.True(olcum.Measured);
        Assert.Equal(1, olcum.Expected);
        Assert.Equal(0, olcum.Verified);
        Assert.False(olcum.Applied);
        Assert.True(olcum.Incomplete);
        Assert.Equal("cron.python_path_incomplete", olcum.NoticeKey);
    }

    /// <summary>Karisik durum: biri PATH'te, biri degil -> KISMI BASARI SAYIYLA yazilir.</summary>
    [Fact]
    public async Task Kismi_basari_SAYIYLA_raporlanir()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var gecici = Path.Combine(Path.GetTempPath(), "ada-path-olcum-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(gecici);

        var ciktiMetni = await SondaCiktisiAsync(windows, gecici);
        cikti.WriteLine(ciktiMetni);

        var olcum = MachinePathMeasurement.Olc(ciktiMetni);

        Assert.Equal(2, olcum.Expected);
        Assert.Equal(1, olcum.Verified);
        Assert.False(olcum.Applied);
        Assert.True(olcum.Incomplete);
        Assert.Contains("hedef 2", olcum.Detail, StringComparison.Ordinal);
    }

    // =====================================================================================
    // 2) DAMGA YOKSA: "SIFIR EKLENDI" DEGIL, OLCULEMEDI
    // =====================================================================================

    /// <summary>
    /// BU DEPODA OLCULMUS KURAL: sorgu HATASI ile BOS SONUC ayni goruntuye DUSMEMELI.
    /// Eski kod damga bulunamayinca <c>0</c> alip devam ediyordu.
    /// </summary>
    [Fact]
    public void Geri_okuma_damgasi_YOKSA_sonuc_SIFIR_degil_OLCULEMEDI_dir()
    {
        var olcum = MachinePathMeasurement.Olc("ADAPANEL-DEGISIKLIK=2\r\n");

        Assert.True(olcum.Unverified);
        Assert.False(olcum.Measured);
        Assert.False(olcum.Applied);
        Assert.False(olcum.Incomplete);
        Assert.Equal("cron.python_path_unverified", olcum.NoticeKey);

        // Hicbir cikti yokken de ayni.
        Assert.True(MachinePathMeasurement.Olc(null).Unverified);
        Assert.True(MachinePathMeasurement.Olc(string.Empty).Unverified);
    }

    /// <summary>
    /// ISTENEN sayi ile DOGRULANAN sayi AYRI alanlardir; panel artik dogrulananı raporlar.
    /// </summary>
    [Fact]
    public void Istenen_ile_dogrulanan_AYRI_tutulur()
    {
        var olcum = MachinePathMeasurement.Olc(
            "ADAPANEL-DEGISIKLIK=2\nADAPANEL-PATH-HEDEF=2\nADAPANEL-PATH-DOGRULANDI=1\n");

        Assert.Equal(2, olcum.Requested);
        Assert.Equal(2, olcum.Expected);
        Assert.Equal(1, olcum.Verified);
        Assert.False(olcum.Applied);
    }

    // =====================================================================================
    // 3) URETILEN BETIK GERCEK POWERSHELL'DE AYRISTIRILABILIYOR MU
    // =====================================================================================

    /// <summary>
    /// Ekleme betigi HKLM'e yazar; burada KOSTURULMAZ. Ama sozdiziminin gecerli oldugu
    /// GERCEK ayristiriciyla olculur -- yoksa dogrulama parcasi hic calismadan betik duserdi
    /// ve sonuc sessizce "olculemedi"ye takilirdi.
    /// </summary>
    [Fact]
    public async Task Ekleme_betigi_GERCEK_powershell_ayristiricisindan_gecer()
    {
        var betik = RuntimeLocator.BuildAddToMachinePathScript("C:\\Python313\\python.exe");

        // Yalniz AYRISTIR, KOSTURMA: hicbir kayit degistirilmez.
        var gecici = Path.Combine(Path.GetTempPath(), "ada-path-betik-" + Guid.NewGuid().ToString("N") + ".ps1");
        await File.WriteAllTextAsync(gecici, betik);

        var sonuc = await ProcessRunner.RunPowerShellAsync(
            "$h = $null; $t = $null; "
            + $"[void][System.Management.Automation.Language.Parser]::ParseFile('{gecici}', [ref]$t, [ref]$h); "
            + "[Console]::Out.WriteLine('ADAPANEL-HATA=' + @($h).Count)");

        Assert.True(sonuc.Success, sonuc.StandardError);
        Assert.Contains("ADAPANEL-HATA=0", sonuc.StandardOutput, StringComparison.Ordinal);

        // Ve dogrulama parcasi GERCEKTEN betigin icinde (tek kaynak birlestirmesi kopmamis).
        Assert.Contains("ADAPANEL-PATH-DOGRULANDI=", betik, StringComparison.Ordinal);
    }

    // =====================================================================================
    // 4) `-ErrorAction Stop` NEDEN GEREKLI -- GERCEK POWERSHELL'DE OLCULDU
    // =====================================================================================

    /// <summary>
    /// <b>BU MAKINEDE OLCULDU.</b> <c>Set-ItemProperty</c> BULUNMAYAN bir kayit yoluna
    /// yazmaya calisirken SONLANDIRICI OLMAYAN bir hata uretir: betik devam eder ve
    /// <c>powershell.exe</c> SIFIR ile cikar. <c>-ErrorAction Stop</c> ile ayni cagri
    /// sonlandirici olur ve cikis kodu SIFIR DEGILDIR.
    /// </summary>
    /// <remarks>
    /// HICBIR SEY YAZILMAZ: hedef anahtar YOKTUR ve <c>Set-ItemProperty</c> anahtar
    /// OLUSTURMAZ (<c>New-Item</c>'in aksine). Olculen sey yalniz HATA YAYILIMIDIR.
    /// </remarks>
    [Fact]
    public async Task ErrorAction_Stop_YOKSA_basarisiz_yazma_SIFIR_ile_cikar()
    {
        var yokYol = "HKLM:\\SOFTWARE\\AdaPanelOlcumYok-" + Guid.NewGuid().ToString("N");

        // URETIM BETIGININ SEKLI: yazma tek basina degil, ARDINDAN baska ifadeler gelir
        // (damga basma + geri okuma). PowerShell'in cikis kodu SON ifadenin sonucudur --
        // bu depoda ZATEN OLCULMUS landmine tam olarak budur. Tek ifadelik bir olcum
        // yaniltir: onda gevsek cagri da 1 doner.
        var gevsek = await ProcessRunner.RunPowerShellAsync(
            $"Set-ItemProperty -Path '{yokYol}' -Name Path -Value 'x'\n"
            + "[Console]::Out.WriteLine('ADAPANEL-DEVAM-ETTI')");

        var siki = await ProcessRunner.RunPowerShellAsync(
            "$ErrorActionPreference = 'Stop'\n"
            + $"Set-ItemProperty -Path '{yokYol}' -Name Path -Value 'x' -ErrorAction Stop\n"
            + "[Console]::Out.WriteLine('ADAPANEL-DEVAM-ETTI')");

        cikti.WriteLine($"gevsek: kod={gevsek.ExitCode} cikti='{gevsek.StandardOutput.Trim()}'");
        cikti.WriteLine($"siki  : kod={siki.ExitCode} cikti='{siki.StandardOutput.Trim()}'");

        // ISTE HATA SINIFI: yazma DUSTU ama betik akmaya devam etti ve surec SIFIR ile cikti.
        // Panel bu kosumu "basarili" sayiyordu.
        Assert.True(gevsek.Success, "Olculen landmine: -ErrorAction Stop olmadan basarisiz yazma SIFIR ile cikiyor.");
        Assert.Contains("ADAPANEL-DEVAM-ETTI", gevsek.StandardOutput, StringComparison.Ordinal);

        // -ErrorAction Stop ile ayni hata GORULEBILIR olur: betik akmaz, cikis kodu sifir degil.
        Assert.False(siki.Success);
        Assert.DoesNotContain("ADAPANEL-DEVAM-ETTI", siki.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// Uretilen betikteki HER <c>Set-ItemProperty</c> satiri <c>-ErrorAction Stop</c>
    /// TASIMALI.
    /// </summary>
    /// <remarks>
    /// Bu sinama SATIR BAZLIDIR, dosyada "-ErrorAction Stop" GECIYOR MU degil. Fark bu
    /// oturumda olculdu: dosyanin baska bir yerindeki bir <c>-ErrorAction Stop</c>, yazma
    /// satirindan kaldirilan korumayi GIZLIYORDU ve mutasyon YESIL kaliyordu. Ust taraftaki
    /// test hata yayiliminin GERCEK davranisini olcer; bu test o korumanin DOGRU SATIRDA
    /// oldugunu olcer.
    /// </remarks>
    [Fact]
    public void Yazma_satirlarinin_HEPSI_ErrorAction_Stop_tasir()
    {
        var betik = RuntimeLocator.BuildAddToMachinePathScript("C:\\Python313\\python.exe");

        var yazmaSatirlari = betik
            .Split('\n')
            .Select(satir => satir.Trim())
            .Where(satir => satir.StartsWith("Set-ItemProperty", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.NotEmpty(yazmaSatirlari);
        Assert.All(yazmaSatirlari, satir =>
            Assert.Contains("-ErrorAction Stop", satir, StringComparison.Ordinal));

        // Okuma satirlari da korumali olmali: PATH okunamazsa "bos PATH" sanilmamali.
        var okumaSatirlari = betik
            .Split('\n')
            .Where(satir => satir.Contains("Get-ItemProperty", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.NotEmpty(okumaSatirlari);
        Assert.All(okumaSatirlari, satir =>
            Assert.Contains("-ErrorAction Stop", satir, StringComparison.Ordinal));
    }
}
