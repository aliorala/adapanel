using System.Text.Json;
using AdaPanel.Infrastructure.Bootstrap;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// <c>scripts/audit-component-packages.ps1</c> betiginin GERCEKTEN calisan davranisi.
/// </summary>
/// <remarks>
/// <para>
/// IKI GERCEK OLAY, 0.5.0 paketlenirken:
/// </para>
/// <para>
/// (1) Denetim, <c>php-8.3</c>'un manifestteki STATIK adresini yokladi, 404 aldi ve YAYINI BLOKE
/// ETTI. Oysa kurulum saglamdi: <c>ComponentBootstrapper</c> php-8.3 icin canli surum cozumleyicisi
/// tasiyor ve paketi <c>releases.json</c>'dan aliyor; manifest adresi yalnizca YEDEK yol. Ayrim
/// yapilmadigi surece her PHP/MariaDB/nginx yamasi yayini durdurur.
/// </para>
/// <para>
/// (2) <c>Download-File</c>, hedef dosya varsa BOYUT/HASH BAKMADAN "indirilmis" sayip geciyordu.
/// Ayni kosuda <c>vc-redist-x64</c> indirmesi iki kez yarida koptu; <c>-KeepDownloads</c> ile
/// calistirilsaydi yarim dosya sessizce "gecerli" muamelesi gorecekti.
/// </para>
/// <para>
/// Testler betigin bir KOPYASINI degil, depodaki GERCEK dosyayi <c>-DefineFunctionsOnly</c> ile
/// dot-source eder; ag'a cikmaz (indirme testleri yerel bir soket sunucusu kullanir).
/// </para>
/// </remarks>
public sealed class ComponentPackageAuditScriptTests : IDisposable
{
    private readonly string calisma = Path.Combine(Path.GetTempPath(), $"ada-paket-denetimi-{Guid.NewGuid():N}");

    public ComponentPackageAuditScriptTests() => Directory.CreateDirectory(calisma);

    public void Dispose()
    {
        try { Directory.Delete(calisma, recursive: true); } catch { /* en iyi caba */ }
    }

    // --- 1) Canli cozumlenen bilesenlerin TURETILMESI -------------------------------------------

    /// <summary>
    /// Indirme adresi canli cozulen bilesenler, ComponentBootstrapper KAYNAGINDAN turetilir.
    /// </summary>
    /// <remarks>
    /// Beklenen kimlikler betikte ELLE YAZILI DEGIL: yazili olsaydi liste bir gun kodla ayrisir ve
    /// yanlis bilesen yanlis sinifa duserdi (ya yayin bosuna durur ya da olu bir yedek yol sessizce
    /// gecerdi). Bu test turetmenin GERCEK kaynak uzerinde calistigini olcer.
    /// </remarks>
    [Theory]
    [InlineData("php-8.3")]      // releases.json'dan guncel yama
    [InlineData("mariadb-11")]   // archive.mariadb.org
    [InlineData("mysql-8")]
    [InlineData("postgresql-16")]
    [InlineData("nginx")]        // nginx.org indirme sayfasi
    [InlineData("python-3")]
    [InlineData("redis")]        // GitHub releases
    [InlineData("adminer")]
    [InlineData("stalwart-mail")]
    [InlineData("win-acme")]
    public void Canli_adres_cozen_bilesenler_kaynaktan_turetilir(string bilesenId)
    {
        Assert.Contains(bilesenId, GercekKaynaktanTuretilenler());
    }

    /// <summary>
    /// Cozumleyicisi indirme ADRESINI degistirmeyen bilesenler bu gruba GIRMEZ.
    /// </summary>
    /// <remarks>
    /// composer ve phpMyAdmin icin cozumleyici yalnizca sha256 tazeler; docker-engine icin
    /// <c>return component</c> der. Ucunun de manifest adresi kurulumda GERCEKTEN kullanilir --
    /// kirilirsa kurulum kirilir, yani yayin DURMALIDIR. "Canli cozumleyicisi var" diye bunlari da
    /// bagislamak, gercek bir kopuklugu uyariya cevirirdi.
    /// </remarks>
    [Theory]
    [InlineData("composer")]
    [InlineData("phpmyadmin")]
    [InlineData("docker-engine")]
    [InlineData("vc-redist-x64")]
    [InlineData("mailenable-standard")]
    [InlineData("url-rewrite")]
    public void Adresi_manifeste_bagli_bilesenler_turetilen_listeye_GIRMEZ(string bilesenId)
    {
        Assert.DoesNotContain(bilesenId, GercekKaynaktanTuretilenler());
    }

    /// <summary>
    /// Turetilen her kimlik hem manifeste hem de panelin kendi "surumu canli sorgulanabilir"
    /// listesine uymali.
    /// </summary>
    /// <remarks>
    /// Iki bagimsiz tek-kaynak ile capraz kontrol: kimlik <c>components.json</c>'da yoksa betik
    /// olmayan bir bileseni bagisliyor demektir; <c>SupportsVersionCheck</c> false ise C# tarafi ile
    /// betik ayni kodu farkli okuyor demektir.
    /// </remarks>
    [Fact]
    public void Turetilen_kimlikler_manifest_ve_SupportsVersionCheck_ile_tutarli()
    {
        var manifestKimlikleri = ManifestKimlikleri();

        foreach (var kimlik in GercekKaynaktanTuretilenler())
        {
            Assert.Contains(kimlik, manifestKimlikleri);
            Assert.True(
                ComponentBootstrapper.SupportsVersionCheck(kimlik),
                $"{kimlik} icin adres canli cozuluyor ama SupportsVersionCheck false donuyor.");
        }
    }

    /// <summary>
    /// Turetme kurallari: adresi degistiren cozumleyici CANLI, sha256 tazeleyen ya da manifesti
    /// oldugu gibi donduren DEGIL. Ayrica kimlik ON EKI ile calisan genel dal da turetilir.
    /// </summary>
    /// <remarks>
    /// Gercek kimlikler yerine sahte bir kaynakla olculur: boylece test, kurallarin kendisini
    /// dogrular -- bugunun bilesen listesini ezberlemez.
    /// </remarks>
    [Fact]
    public void Turetme_kurali_adresi_degistiren_cozumleyiciyi_secer()
    {
        var kaynak = SahteBootstrapperYaz();

        var sonuc = FonksiyonlariCalistir($$"""
            $c = Get-AdaLiveDownloadUrlResolution -SourcePath {{ScriptTestHost.PowerShellLiteral(kaynak)}}
            foreach ($id in $c.Ids) { Write-Output "ID=$id" }
            foreach ($p in $c.Prefixes) { Write-Output "ONEK=$p" }
            Write-Output "SURUMLU=$(Test-AdaComponentHasLiveDownloadUrl -ComponentId 'ruby-3.2' -Resolution $c)"
            Write-Output "SURUMSUZ=$(Test-AdaComponentHasLiveDownloadUrl -ComponentId 'ruby-head' -Resolution $c)"
            """);

        Assert.Equal(0, sonuc.ExitCode);
        Assert.Contains("ID=canli-paket", sonuc.Tumu);
        Assert.DoesNotContain("ID=yalnizca-hash", sonuc.Tumu);   // sadece sha256 tazeliyor
        Assert.DoesNotContain("ID=sabit-paket", sonuc.Tumu);     // "return component"
        Assert.Contains("ONEK=ruby-", sonuc.Tumu);
        Assert.Contains("SURUMLU=True", sonuc.Tumu);
        Assert.Contains("SURUMSUZ=False", sonuc.Tumu);
    }

    /// <summary>
    /// Kaynak beklenen bicimde okunamazsa SESSIZ KALINMAZ.
    /// </summary>
    /// <remarks>
    /// Bos liste dondurmek en tehlikeli davranis olurdu: hicbir bilesen "canli" sayilmaz, denetim
    /// eski haline doner ve kimse fark etmez. Ayristirma kodla ayrisirsa denetim GURULTULU
    /// bicimde durmali.
    /// </remarks>
    [Fact]
    public void Cozumleyici_dagitimi_bulunamazsa_denetim_durur()
    {
        var kaynak = Path.Combine(calisma, "Bozuk.cs");
        File.WriteAllText(kaynak, "namespace Sahte;\n\npublic sealed class Bos\n{\n}\n");

        var sonuc = FonksiyonlariCalistir($$"""
            try {
                $null = Get-AdaLiveDownloadUrlResolution -SourcePath {{ScriptTestHost.PowerShellLiteral(kaynak)}}
                Write-Output "SONUC=sessiz"
            }
            catch {
                Write-Output "SONUC=hata"
                Write-Output "MESAJ=$($_.Exception.Message)"
            }
            """);

        Assert.Contains("SONUC=hata", sonuc.Tumu);
        Assert.Contains("ResolveDynamicComponentAsync", sonuc.Tumu);
    }

    // --- 2) Basarisizligin SINIFLANDIRILMASI ----------------------------------------------------

    /// <summary>
    /// Canli cozumleyicisi olan bilesenin bayat yedek adresi yayini BLOKE ETMEZ ama sessiz de kalmaz.
    /// </summary>
    [Fact]
    public void Bayat_yedek_adres_ayri_ve_bloke_etmeyen_kayit_uretir()
    {
        var sonuc = FonksiyonlariCalistir("""
            $k = New-AdaComponentAuditFailure -ComponentId 'php-8.3' -Message '404 Not Found' -HasLiveDownloadUrl $true
            Write-Output "DURUM=$($k.status)"
            Write-Output "BLOKE=$($k.blocking)"
            Write-Output "AYRINTI=$($k.detail)"
            """);

        Assert.Equal(0, sonuc.ExitCode);
        Assert.Contains("DURUM=StaleFallback", sonuc.Tumu);
        Assert.Contains("BLOKE=False", sonuc.Tumu);
        // Uyari, sorunun ne oldugunu SOYLEMELI: "gecti" ile karistirilamaz.
        Assert.Contains("404 Not Found", sonuc.Tumu);
    }

    /// <summary>
    /// Adresi manifeste bagli bilesenin kirilmasi yayini DURDURUR (eski davranis korunur).
    /// </summary>
    [Fact]
    public void Manifeste_bagli_bilesenin_hatasi_yayini_durdurur()
    {
        var sonuc = FonksiyonlariCalistir("""
            $k = New-AdaComponentAuditFailure -ComponentId 'docker-engine' -Message 'SHA256 uyusmazligi' -HasLiveDownloadUrl $false
            Write-Output "DURUM=$($k.status)"
            Write-Output "BLOKE=$($k.blocking)"
            """);

        Assert.Equal(0, sonuc.ExitCode);
        Assert.Contains("DURUM=Failed", sonuc.Tumu);
        Assert.Contains("BLOKE=True", sonuc.Tumu);
    }

    /// <summary>
    /// Denetim dongusu siniflandirmayi GERCEKTEN kullanmali (kendi kopyasini degil).
    /// </summary>
    /// <remarks>
    /// Metin tabanlidir ve kirilgandir; alternatifi 24 bilesenin tamamini agdan indirmekti.
    /// Kirilganligi kabul edip donguyle siniflandirma arasindaki bagi koruyoruz.
    /// </remarks>
    [Fact]
    public void Denetim_dongusu_siniflandirmaya_baglidir()
    {
        var betik = File.ReadAllText(ScriptTestHost.ScriptPath("scripts", "audit-component-packages.ps1"));

        Assert.Contains("New-AdaComponentAuditFailure", betik);
        Assert.Contains("if ($kayit.blocking)", betik);
        // Bloke etmeyen durum ozetlenmeli: uyari ekrandan kayarsa denetim SESSIZ kalmis olur.
        Assert.Contains("$staleFallbacks", betik);
    }

    // --- 3) ATOMIK indirme ----------------------------------------------------------------------

    /// <summary>
    /// Yarida kesilen indirme hedef adiyla ASLA birakilmaz.
    /// </summary>
    /// <remarks>
    /// Eski kod dogrudan hedef dosyaya yaziyordu: sunucu 4096 bayt vaat edip 100 bayt gonderdiginde
    /// diskte 100 baytlik "gecerli gorunumlu" bir dosya kaliyordu ve sonraki kosum onu
    /// <c>Test-Path</c> ile "indirilmis" sayiyordu.
    /// </remarks>
    [Fact]
    public void Yarim_indirme_hedef_dosya_olarak_birakilmaz()
    {
        var hedef = Path.Combine(calisma, "paket.bin");
        var sonuc = IndirmeyiCalistir(hedef, bildirilenUzunluk: 4096, gonderilenBayt: 100);

        Assert.Contains("SONUC=hata", sonuc.Tumu);
        Assert.Contains("HEDEF_VAR=False", sonuc.Tumu);
        Assert.Contains("PARTIAL_VAR=False", sonuc.Tumu);
        Assert.False(File.Exists(hedef), "Yarim indirme hedef adiyla diskte kaldi.");
    }

    /// <summary>
    /// Tamamlanan indirme atomik olarak hedefe tasinir; yarim dosya kalintisi kalmaz.
    /// </summary>
    [Fact]
    public void Tamamlanan_indirme_hedefe_tasinir()
    {
        var hedef = Path.Combine(calisma, "tam.bin");
        var sonuc = IndirmeyiCalistir(hedef, bildirilenUzunluk: 512, gonderilenBayt: 512);

        Assert.Contains("SONUC=basarili", sonuc.Tumu);
        Assert.Contains("HEDEF_VAR=True", sonuc.Tumu);
        Assert.Contains("PARTIAL_VAR=False", sonuc.Tumu);
        Assert.Equal(512, new FileInfo(hedef).Length);
    }

    /// <summary>
    /// Onceki kosumdan kalan bir ".partial" dosyasi yeni indirmeye KARISMAZ.
    /// </summary>
    [Fact]
    public void Onceki_kosumun_yarim_dosyasi_yeni_indirmeye_karismaz()
    {
        var hedef = Path.Combine(calisma, "kalinti.bin");
        File.WriteAllBytes(hedef + ".partial", new byte[7]);

        var sonuc = IndirmeyiCalistir(hedef, bildirilenUzunluk: 256, gonderilenBayt: 256);

        Assert.Contains("SONUC=basarili", sonuc.Tumu);
        Assert.Equal(256, new FileInfo(hedef).Length);
        Assert.False(File.Exists(hedef + ".partial"));
    }

    /// <summary>
    /// Tamamlanmis dosya yeniden indirilmez (-KeepDownloads ile tekrar kosum ucuz kalmali).
    /// </summary>
    /// <remarks>
    /// Erken donus artik guvenli, cunku hedef adiyla YALNIZCA tamamlanmis dosya bulunabilir.
    /// Kapali bir port kullanilir: ag'a cikilsaydi test hata verirdi.
    /// </remarks>
    [Fact]
    public void Tamamlanmis_dosya_yeniden_indirilmez()
    {
        var hedef = Path.Combine(calisma, "onceden.bin");
        File.WriteAllBytes(hedef, new byte[64]);

        var sonuc = FonksiyonlariCalistir($$"""
            $hedef = {{ScriptTestHost.PowerShellLiteral(hedef)}}
            try {
                Download-File 'http://127.0.0.1:9/paket.bin' $hedef
                Write-Output "SONUC=basarili"
            }
            catch {
                Write-Output "SONUC=hata"
                Write-Output "MESAJ=$($_.Exception.Message)"
            }
            Write-Output "BOYUT=$((Get-Item -LiteralPath $hedef).Length)"
            """);

        Assert.Contains("SONUC=basarili", sonuc.Tumu);
        Assert.Contains("BOYUT=64", sonuc.Tumu);
    }

    // --- duzenek --------------------------------------------------------------------------------

    /// <summary>
    /// Yerel bir soket sunucusu ile <c>Download-File</c>'i GERCEKTEN kosturur.
    /// </summary>
    /// <remarks>
    /// Sunucu, Content-Length'te <paramref name="bildirilenUzunluk"/> vaat eder ama yalnizca
    /// <paramref name="gonderilenBayt"/> gonderip baglantiyi kapatir -- CDN'in yarida kopmasinin
    /// birebir karsiligi.
    /// </remarks>
    [Fact]
    public void Stalled_response_body_times_out_and_removes_partial_download()
    {
        var target = Path.Combine(calisma, "stalled.bin");
        var result = IndirmeyiCalistir(target, 256, 1, stall: true);
        Assert.Contains("SONUC=hata", result.Tumu);
        var elapsedLine = result.StdOut.Split('\n').Single(line => line.StartsWith("ELAPSED="));
        Assert.True(long.Parse(elapsedLine[8..]) < 8000, result.Tumu);
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".partial"));
    }

    private ScriptTestHost.ScriptResult IndirmeyiCalistir(string hedef, int bildirilenUzunluk, int gonderilenBayt, bool stall = false)
    {
        return FonksiyonlariCalistir($$"""
            $dinleyici = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
            $dinleyici.Start()
            $port = ([System.Net.IPEndPoint]$dinleyici.LocalEndpoint).Port
            $govde = New-Object byte[] {{gonderilenBayt}}

            $sunucuKodu = {
                param($dinleyici, $bildirilen, $govde)
                $istemci = $dinleyici.AcceptTcpClient()
                $akis = $istemci.GetStream()
                $tampon = New-Object byte[] 8192
                $null = $akis.Read($tampon, 0, $tampon.Length)
                $baslik = "HTTP/1.1 200 OK`r`nContent-Type: application/octet-stream`r`nContent-Length: $bildirilen`r`nConnection: close`r`n`r`n"
                $baslikBayt = [Text.Encoding]::ASCII.GetBytes($baslik)
                $akis.Write($baslikBayt, 0, $baslikBayt.Length)
                if ($govde.Length -gt 0) { $akis.Write($govde, 0, $govde.Length) }
                $akis.Flush()
                {{(stall ? "Start-Sleep -Seconds 15" : "")}}
                $istemci.Close()
            }

            $sunucu = [powershell]::Create()
            $null = $sunucu.AddScript($sunucuKodu.ToString()).AddArgument($dinleyici).AddArgument({{bildirilenUzunluk}}).AddArgument($govde)
            $null = $sunucu.BeginInvoke()

            $hedef = {{ScriptTestHost.PowerShellLiteral(hedef)}}
            $watch = [Diagnostics.Stopwatch]::StartNew()
            try {
                Download-File "http://127.0.0.1:$port/paket.bin" $hedef {{(stall ? "-TimeoutSeconds 1" : "")}}
                Write-Output "SONUC=basarili"
            }
            catch {
                Write-Output "SONUC=hata"
                Write-Output "MESAJ=$($_.Exception.Message)"
            }
            finally {
                Write-Output "ELAPSED=$($watch.ElapsedMilliseconds)"
                try { $sunucu.Stop(); $sunucu.Dispose() } catch { }
                try { $dinleyici.Stop() } catch { }
            }

            Write-Output "HEDEF_VAR=$(Test-Path -LiteralPath $hedef)"
            Write-Output "PARTIAL_VAR=$(Test-Path -LiteralPath ($hedef + '.partial'))"
            """);
    }

    /// <summary>Depodaki GERCEK betigi dot-source eder; denetim akisini kosturmaz.</summary>
    private static ScriptTestHost.ScriptResult FonksiyonlariCalistir(string govde)
    {
        var betikYolu = ScriptTestHost.ScriptPath("scripts", "audit-component-packages.ps1");
        Assert.True(File.Exists(betikYolu), $"Denetim betigi bulunamadi: {betikYolu}");

        return ScriptTestHost.Run(
            $". {ScriptTestHost.PowerShellLiteral(betikYolu)} -DefineFunctionsOnly\n{govde}");
    }

    /// <summary>Betigi GERCEK ComponentBootstrapper kaynagi uzerinde kosturup kimlikleri okur.</summary>
    private static IReadOnlyCollection<string> GercekKaynaktanTuretilenler()
    {
        var kaynak = Path.Combine(
            ScriptTestHost.RepoRoot, "src", "AdaPanel.Infrastructure", "Bootstrap", "ComponentBootstrapper.cs");
        Assert.True(File.Exists(kaynak), $"ComponentBootstrapper bulunamadi: {kaynak}");

        var sonuc = FonksiyonlariCalistir($$"""
            $c = Get-AdaLiveDownloadUrlResolution -SourcePath {{ScriptTestHost.PowerShellLiteral(kaynak)}}
            foreach ($id in $c.Ids) { Write-Output "ID=$id" }
            """);

        Assert.Equal(0, sonuc.ExitCode);
        var kimlikler = sonuc.StdOut
            .Split('\n')
            .Select(satir => satir.Trim())
            .Where(satir => satir.StartsWith("ID=", StringComparison.Ordinal))
            .Select(satir => satir[3..])
            .ToArray();

        Assert.NotEmpty(kimlikler);
        return kimlikler;
    }

    private static IReadOnlyCollection<string> ManifestKimlikleri()
    {
        using var belge = JsonDocument.Parse(File.ReadAllText(Path.Combine(ScriptTestHost.RepoRoot, "components.json")));
        return belge.RootElement.GetProperty("components")
            .EnumerateArray()
            .Select(bilesen => bilesen.GetProperty("id").GetString()!)
            .ToArray();
    }

    /// <summary>
    /// Turetme KURALLARINI olcmek icin sahte bir dagitim metodu (gercek kimlikler kullanilmaz).
    /// </summary>
    private string SahteBootstrapperYaz()
    {
        var yol = Path.Combine(calisma, "SahteBootstrapper.cs");
        File.WriteAllText(yol, """
            namespace Sahte;

            public sealed class SahteBootstrapper
            {
                private async Task<Item> ResolveDynamicComponentAsync(Item component, HttpClient client, CancellationToken cancellationToken)
                {
                    var normalizedId = component.Id.Trim().ToLowerInvariant();
                    switch (normalizedId)
                    {
                        case "canli-paket":
                            return await ResolveLatestThingAsync(component, client, cancellationToken);

                        case "yalnizca-hash":
                            return await ResolveChecksumOnlyAsync(component, client, cancellationToken);

                        case "sabit-paket":
                            return component;
                    }

                    if (!component.Id.StartsWith("ruby-", StringComparison.OrdinalIgnoreCase))
                    {
                        return component;
                    }

                    return CopyComponent(component, downloadUrl: "https://ornek.invalid/ruby.zip");
                }

                private async Task<Item> ResolveLatestThingAsync(Item component, HttpClient client, CancellationToken cancellationToken)
                {
                    return CopyComponent(component, version: "1.0", downloadUrl: "https://ornek.invalid/canli.zip");
                }

                private async Task<Item> ResolveChecksumOnlyAsync(Item component, HttpClient client, CancellationToken cancellationToken)
                {
                    return CopyComponent(component, sha256: "abc");
                }
            }
            """);

        return yol;
    }
}
