using System.Diagnostics;
using System.Text;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// PAKETSİZ RELEASE YAYINLANAMAZ.
///
/// <para>
/// <b>Yaşanmış hata (0.7.5):</b> <c>publish-github-release.ps1</c> <c>-InstallerPath</c>
/// verilmeden çalıştırıldı. Betik release'i oluşturdu, notları yazdı, gövdeyi doğruladı,
/// "Tamamlandi" dedi ve <b>sıfır</b> hata koduyla döndü — ama yayında indirilecek hiçbir şey
/// yoktu. Paketsiz bir yayın, panelin kendi kendine güncellemesini KIRAR: güncelleme ekranı yeni
/// sürümü GÖRÜR, indirmeye gider ve varlık bulamaz.
/// </para>
/// <para>
/// <b>İlk düzeltmede kaçan ikinci hata:</b> koşul yalnız "<c>-InstallerPath</c> verildi mi"ye
/// bakıyordu. <c>-InstallerPath X -SkipAssetUpload</c> BİRLİKTE verilince nöbetçi atlanıyordu
/// (yol dolu) ve yükleme de atlanıyordu (bayrak) — yani engellenmek istenen sonucun ta kendisi
/// "Tamamlandi" diyerek üretilebiliyordu. Doğru soru "parametre verildi mi" değil,
/// <b>"bu çalışmanın sonunda yayında paket olacak mı"</b>.
/// </para>
/// <para>
/// <b>Bu dosya neden yeniden yazıldı:</b> önceki hâli üç testin üçünde de yalnızca KAYNAK METNİNİ
/// arıyordu (<c>Assert.Contains("Paketsiz yayin ENGELLENDI", betik)</c>). Koruma
/// <c>if ($false) { … }</c> içine alınsa üçü de YEŞİL kalırdı — yani nöbetçiyi ölçen bir nöbetçi
/// yoktu ve yukarıdaki ikinci hata tam da bu yüzden testlerden geçti. Artık karar ayrı bir saf
/// fonksiyonda (<c>scripts/lib/release-asset-guard.ps1</c>) ve testler onu GERÇEKTEN ÇALIŞTIRIYOR.
/// Fonksiyon ağ/kimlik istemez, bu yüzden çalıştırmak güvenlidir.
/// </para>
/// </summary>
public class ReleaseAssetGuardTests
{
    /// <summary>
    /// Dört bayrak birleşimi × "yayında paket var/yok" — kararın TAMAMI.
    /// </summary>
    /// <remarks>
    /// Kritik satır üçüncüsü: yol VE atlama bayrağı birlikte. Eski koşul burada "engelle"
    /// DEMİYORDU ve hata bu delikten geçmişti.
    /// </remarks>
    [Theory]
    // yol verildi, atlama yok -> her durumda yuklenir
    [InlineData("paket.exe", false, 0, "yukle")]
    [InlineData("paket.exe", false, 1, "yukle")]
    // yol yok -> yayindaki pakete bakilir
    [InlineData("", false, 0, "engelle")]
    [InlineData("", false, 1, "mevcut-yeter")]
    // YOL VAR AMA ATLAMA DA VAR: sonucta paket yuklenmez -> yayinda paket yoksa ENGELLENMELI
    [InlineData("paket.exe", true, 0, "engelle")]
    [InlineData("paket.exe", true, 1, "mevcut-yeter")]
    // acikca atlaniyor, yol da yok
    [InlineData("", true, 0, "engelle")]
    [InlineData("", true, 1, "mevcut-yeter")]
    // bos/bosluk yol "verilmemis" sayilir; aksi halde -InstallerPath "" nobetciyi atlatirdi
    [InlineData("   ", false, 0, "engelle")]
    public void Karar_bayrak_birlesimlerinin_TAMAMINDA_dogru(
        string kurulumYolu, bool yuklemeyiAtla, int mevcutVarlik, string beklenen)
    {
        var sonuc = KararCalistir(kurulumYolu, yuklemeyiAtla, mevcutVarlik);

        Assert.Equal(beklenen, sonuc);
    }

    /// <summary>
    /// Karar dosyası hiçbir şey ÇALIŞTIRMAMALI: testin onu dot-source edebilmesi buna bağlı.
    /// Bir gün dosyaya yan etkili bir satır eklenirse test bunu yakalar (çıktı kirlenir).
    /// </summary>
    [Fact]
    public void Karar_dosyasi_dot_source_edilince_YAN_ETKI_uretmez()
    {
        var cikti = PowerShellCalistir(
            $". '{KararDosyasi()}'; Write-Output 'BOS-DEGIL-KONTROL'");

        Assert.Equal("BOS-DEGIL-KONTROL", cikti);
    }

    /// <summary>
    /// Yayın betiği kararı KENDİ yeniden yazmamalı, ortak dosyadan almalı — yoksa iki kopya
    /// ayrışır ve biri düzeltilirken diğeri açık kalır (bu depoda tekrarın bedeli budur).
    /// </summary>
    [Fact]
    public void Yayin_betigi_karari_ORTAK_dosyadan_alir()
    {
        var betik = File.ReadAllText(Path.Combine(KaynakKoku(), "scripts", "publish-github-release.ps1"));

        Assert.Contains("lib\\release-asset-guard.ps1", betik);
        Assert.Contains("Get-AdaReleaseAssetPlan", betik);
        Assert.Contains("Get-AdaReleaseAssetBlockMessage", betik);

        // Engel, notlar GONDERILMEDEN once olmali: sonra olsaydi release yine olusurdu ve hata
        // yalnizca varlik adiminda patlardi -- yani tam olarak yasanan durum.
        Assert.True(
            betik.IndexOf("Get-AdaReleaseAssetBlockMessage", StringComparison.Ordinal)
                < betik.IndexOf("$release = Publish-AdaGitHubRelease", StringComparison.Ordinal),
            "paketsiz yayin denetimi, release olusturulduktan SONRA yapiliyor");
    }

    [Fact]
    public void Yeni_yayin_dosya_yuklenmeden_once_yalniz_taslak_olur()
    {
        var publisher = Path.Combine(KaynakKoku(), "scripts", "publish-github-release.ps1").Replace("'", "''");
        var output = PowerShellCalistir($$"""
            . '{{publisher}}'
            function Invoke-AdaGitHubApi {
                param($Uri, $Method = 'GET', $Token, $BodyObject)
                if ($Uri -match '/tags/') { return $null }
                if ($Method -eq 'POST') {
                    $script:created = [pscustomobject]@{ id=42; body=$BodyObject.body; draft=$BodyObject.draft }
                }
                return $script:created
            }
            $info = [pscustomobject]@{tag_name='1.2.3';name='Test';body='Notes'}
            $release = Publish-AdaGitHubRelease -RepoSlug 'example/test' -Token 'unused' -ReleaseInfo $info 6>$null
            if ($release.draft -ne $true) { throw 'Empty release became public before upload' }
            Write-Output 'DRAFT-OK'
            """);
        Assert.Equal("DRAFT-OK", output);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Retry_reuses_existing_draft_even_when_tag_lookup_returns_nothing(int page)
    {
        var publisher = Path.Combine(KaynakKoku(), "scripts", "publish-github-release.ps1").Replace("'", "''");
        var output = PowerShellCalistir($$"""
            . '{{publisher}}'
            $script:draft = [pscustomobject]@{ id=73; tag_name='1.2.3'; body='Notes'; draft=$true }
            function Invoke-AdaGitHubApi {
                param($Uri, $Method = 'GET', $Token, $BodyObject)
                if ($Method -eq 'POST') { throw 'Created duplicate draft' }
                if ($Uri -match '/tags/') { return $null }
                if ($Uri -match 'page={{page}}$') { return ,@([pscustomobject]@{id=99;tag_name='other';draft=$true}, $script:draft) }
                if ($Uri -match 'per_page=100') { return ,@(1..100 | ForEach-Object { [pscustomobject]@{tag_name="other-$_"} }) }
                if ($Uri -match '/73$') { return $script:draft }
                throw "Unexpected request: $Uri"
            }
            $info = [pscustomobject]@{tag_name='1.2.3';name='Test';body='Notes'}
            $release = Publish-AdaGitHubRelease -RepoSlug 'example/test' -Token 'unused' -ReleaseInfo $info 6>$null
            if ($release.id -ne 73 -or $release.draft -ne $true) { throw 'Draft was not reused' }
            Write-Output 'REUSED-OK'
            """);
        Assert.Equal("REUSED-OK", output);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(22)]
    public void Curl_upload_keeps_token_on_stdin_and_rejects_failed_transfer(int exitCode)
    {
        var publisher = Path.Combine(KaynakKoku(), "scripts", "publish-github-release.ps1").Replace("'", "''");
        var output = PowerShellCalistir($$"""
            . '{{publisher}}'
            function curl.exe {
                $script:curlArgs = $args
                $script:curlInput = $input | Out-String
                $global:LASTEXITCODE = {{exitCode}}
                '{"name":"setup.exe","state":"uploaded"}'
            }
            $failed = $false
            try { $asset = Send-AdaCurlReleaseFile -Uri 'https://uploads.github.com/example' -Token 'dummy_private_value' -FilePath 'C:\folder with spaces\setup.exe' }
            catch { $failed = $true }
            if ($failed -ne ({{exitCode}} -ne 0)) { throw 'Wrong transfer result' }
            if (($script:curlArgs -join ' ') -match 'dummy_private_value') { throw 'Credential leaked into process arguments' }
            if ($script:curlInput -notmatch 'Authorization: token dummy_private_value') { throw 'Missing stdin credential' }
            if ($script:curlArgs -notcontains '@C:\folder with spaces\setup.exe') { throw 'File argument was split' }
            if ($script:curlArgs -contains '--retry' -or $script:curlArgs -contains '--location') { throw 'Unsafe automatic retry or redirect' }
            if (-not $failed -and $asset.state -ne 'uploaded') { throw 'Response was not parsed' }
            Write-Output 'TRANSFER-OK'
            """);
        Assert.Equal("TRANSFER-OK", output);
    }

    private static string KararCalistir(string kurulumYolu, bool yuklemeyiAtla, int mevcutVarlik)
    {
        // Yol tek tirnak icinde gecirilir; PowerShell'de tek tirnakli dize genisletilmez.
        var kacisli = kurulumYolu.Replace("'", "''");
        var atla = yuklemeyiAtla ? "$true" : "$false";

        return PowerShellCalistir(
            $". '{KararDosyasi()}'; " +
            $"Get-AdaReleaseAssetPlan -InstallerPath '{kacisli}' -SkipAssetUpload:{atla} -MevcutVarlikSayisi {mevcutVarlik}");
    }

    private static string PowerShellCalistir(string komut)
    {
        var baslangic = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        baslangic.ArgumentList.Add("-NoProfile");
        baslangic.ArgumentList.Add("-NonInteractive");
        baslangic.ArgumentList.Add("-ExecutionPolicy");
        baslangic.ArgumentList.Add("Bypass");
        baslangic.ArgumentList.Add("-Command");
        baslangic.ArgumentList.Add(komut);

        using var surec = Process.Start(baslangic)!;
        var cikti = surec.StandardOutput.ReadToEnd();
        var hata = surec.StandardError.ReadToEnd();

        // Asili kalan bir test kimsenin kosturmadigi bir test paketi demektir.
        Assert.True(surec.WaitForExit(30_000), "powershell 30 sn icinde bitmedi");
        Assert.True(string.IsNullOrWhiteSpace(hata), $"powershell hata yazdi: {hata}");

        return cikti.Trim();
    }

    private static string KararDosyasi()
        => Path.Combine(KaynakKoku(), "scripts", "lib", "release-asset-guard.ps1");

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
