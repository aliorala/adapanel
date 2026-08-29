using System.Diagnostics;
using System.Text;
using AdaPanel.Infrastructure.Ftp;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// FTP YETKİ KURALININ KALDIRILMASI — panelin ÇALIŞTIRDIĞI dizgenin kendisi koşturularak.
/// </summary>
/// <remarks>
/// <para>
/// <b>Düzeltilen hata (19.08.2026, canlı ölçüm).</b> Kaldırma
/// <c>Clear-WebConfiguration … -AtElement @{users=$userName} -ErrorAction SilentlyContinue</c>
/// ile yazılmıştı. O cmdlet'in <c>-AtElement</c> parametresi YOKTUR — panelin kendi terminal
/// ucundan <c>-ErrorAction Stop</c> ile ölçüldü: <c>ParameterBindingException :: A parameter
/// cannot be found that matches parameter name 'AtElement'</c>. <c>SilentlyContinue</c> hatayı
/// yuttuğu için betik "başarılı" sayılıyordu. Sonuçları da ölçüldü: silinen kullanıcının
/// <c>Allow</c> kuralı <c>applicationHost.config</c>'de KALIYOR → aynı ad bir daha
/// oluşturulamıyor (<c>500 ftp.site_configuration_failed</c>) ve silme erişimi gerçekten iptal
/// etmiyor.
/// </para>
/// <para>
/// <b>Neden sahte cmdlet'ler:</b> gerçek IIS'e yazan bir test, geliştirme/müşteri makinesindeki
/// CANLI "AdaPanel FTP" sitesinin yapılandırmasını değiştirirdi. Bunun yerine cmdlet'ler,
/// <b>bu makinede ÖLÇÜLEN gerçek parametre listeleriyle</b> taklit ediliyor:
/// <c>Clear-WebConfiguration</c> → <c>Clr, Force, Location, Filter, PSPath</c>;
/// <c>Remove-WebConfigurationProperty</c> → <c>Name, Clr, AtElement, AtIndex, AtName, Force,
/// Location, Filter, PSPath</c>. Yani eski hatalı çağrı geri konursa taklit de tıpkı gerçeği gibi
/// bağlama hatası verir.
/// </para>
/// <para>
/// Testler <see cref="FtpService.RemoveUserAuthorizationSnippet"/> sabitinin TA KENDİSİNİ
/// çalıştırır (<c>RemoveUserRootSnippet</c> testlerindeki desen); kopya test etmek hiçbir şey
/// kanıtlamazdı. Doğrulama kaynak METNİNE değil, sahte cmdlet'lerin ürettiği ÇAĞRI İZİNE bakar.
/// </para>
/// </remarks>
public sealed class FtpAuthorizationRemovalBehaviourTests
{
    [Fact]
    public void Kural_DOGRU_cmdletle_ve_DOGRU_kullaniciyla_kaldirilir()
    {
        // Çağrı izi tek başına P0 mutasyon dedektörü: eski `Clear-WebConfiguration -AtElement`
        // geri konursa bu satır hiç yazılmaz (o cmdlet AtElement'i bağlayamaz) ve test kızarır.
        var cikti = SnippetiCalistir("ada_kullanici", silmeCalissin: true);

        Assert.Contains("SONUC:TAMAM", cikti);
        Assert.Contains("IZ:REMOVE|.|ada_kullanici", cikti);

        // BAŞKA kullanıcının kuralına dokunulmadı: -AtElement hedefli kaldırma yapmalı, koleksiyonu
        // süpürmemeli. (Clear-WebConfiguration'ın gerçekten yaptığı şey TÜM bölümü temizlemektir;
        // yanlışlıkla parametresiz çağrılırsa bütün müşterilerin FTP erişimi silinirdi.)
        Assert.Contains("KALAN:baska_kullanici", cikti);
    }

    [Fact]
    public void Clear_WebConfiguration_HIC_kullanilmaz()
    {
        // P0-1'in ADIYLA kilidi. İKİ ayrı iz birden aranıyor ve bu bilinçli — mutasyon denendiğinde
        // ölçüldü ki tek başına çağrı izi YETMİYOR: hatalı çağrı, sahte cmdlet'in GÖVDESİ hiç
        // çalışmadan parametre bağlamada düşüyor (gerçeğinde olduğu gibi), yani "IZ:CLEAR" asla
        // yazılmıyor ve test yanlışlıkla yeşil kalıyordu. Bağlama hatası ise -ErrorAction
        // SilentlyContinue ile yutulsa bile $Error'a düşer; çağrılan cmdlet'in adı oradan okunur.
        var cikti = SnippetiCalistir("ada_kullanici", silmeCalissin: true);

        Assert.DoesNotContain("IZ:CLEAR", cikti);
        Assert.DoesNotContain("Clear-WebConfiguration", cikti);
    }

    [Fact]
    public void Kaldiramama_SESSIZCE_gecmez()
    {
        // P0-2'nin kalbi: "cmdlet şikâyet etmedi" ile "kural gitti" aynı şey değildir. Sahte cmdlet
        // hiç hata vermeden HİÇBİR ŞEY yapmıyor — tam olarak eski hatanın davranışı. Geri okuma
        // olmasaydı bu senaryo yine "başarılı" dönerdi ve silme erişimi iptal etmemeye devam ederdi.
        var cikti = SnippetiCalistir("ada_kullanici", silmeCalissin: false);

        Assert.Contains("SONUC:HATA", cikti);
        Assert.Contains("ada_kullanici", cikti);
    }

    [Fact]
    public void Kural_ZATEN_yoksa_hata_vermez()
    {
        // Silme yolu idempotent olmalı: zaten temizlenmiş bir hesabın ikinci silinmesi düşmemeli.
        // Bu, gerçek cmdlet'in ölçülmüş davranışıyla da uyumlu — kural yokken HATA değil UYARI verip
        // sıfırla çıkıyor, bu yüzden -ErrorAction Stop güvenle kullanılabiliyor.
        var cikti = SnippetiCalistir("hic_olmayan_kullanici", silmeCalissin: true);

        Assert.Contains("SONUC:TAMAM", cikti);
    }

    // --- düzenek ---

    /// <summary>
    /// Panelin gerçekten çalıştırdığı dizgeyi, ölçülmüş parametre listelerine sahip sahte
    /// cmdlet'lerin altında koşturur.
    /// </summary>
    /// <param name="silmeCalissin">
    /// <c>false</c> ise sahte kaldırma HİÇBİR ŞEY yapmaz — sessiz başarısızlığı üretir.
    /// </param>
    private static string SnippetiCalistir(string userName, bool silmeCalissin)
    {
        var silmeGovdesi = silmeCalissin
            ? "$script:kurallar = @($script:kurallar | Where-Object { $_ -ne $AtElement.users })"
            : "# sessiz basarisizlik: cmdlet sikayet etmez ama kurali KALDIRMAZ";

        var betik = $$"""
            $ErrorActionPreference = 'Stop'
            $script:kurallar = @('ada_kullanici','baska_kullanici')
            $script:iz = @()

            # Gercek parametre listesi (bu makinede olculdu): Clr, Force, Location, Filter, PSPath.
            # -AtElement BILEREK YOK; eski hatali cagri geri konursa gercegi gibi baglama hatasi verir.
            function Clear-WebConfiguration {
              [CmdletBinding()]
              param($Clr, $Force, $Location, $Filter, $PSPath)
              $script:iz += 'CLEAR'
            }

            # Gercek parametre listesi (olculdu): Name, Clr, AtElement, AtIndex, AtName, Force,
            # Location, Filter, PSPath.
            function Remove-WebConfigurationProperty {
              [CmdletBinding()]
              param($Name, $Clr, $AtElement, $AtIndex, $AtName, $Force, $Location, $Filter, $PSPath)
              $script:iz += ('REMOVE|' + $Name + '|' + $AtElement.users)
              {{silmeGovdesi}}
            }

            function Get-WebConfiguration {
              [CmdletBinding()]
              param($PSPath, $Location, $Filter, $Name)
              [pscustomobject]@{ Collection = @($script:kurallar | ForEach-Object { [pscustomobject]@{ users = $_ } }) }
            }

            $siteName = 'AdaPanel FTP'
            $userName = '{{userName.Replace("'", "''")}}'

            try {
            {{FtpService.RemoveUserAuthorizationSnippet}}
              Write-Output 'SONUC:TAMAM'
            } catch {
              Write-Output ('SONUC:HATA ' + $_.Exception.Message)
            }
            foreach ($s in $script:iz) { Write-Output ('IZ:' + $s) }
            # YUTULAN hatalar da gorunur olsun: -ErrorAction SilentlyContinue bir baglama hatasini
            # ekrandan gizler ama $Error'dan gizleyemez.
            # InvocationInfo.LINE kullaniliyor, InvocationName DEGIL: olculdu ki parametre baglama
            # hatasinda InvocationName BOS gelir (cmdlet hic calistirilamadigi icin) -- yani cagrilan
            # cmdlet'in adini yalnizca hataya sebep olan KAYNAK SATIRI tasiyor.
            foreach ($h in $Error) { Write-Output ('HATA:' + ($h.InvocationInfo.Line -replace '\s+', ' ') + ' :: ' + $h.Exception.Message) }
            Write-Output ('KALAN:' + ($script:kurallar -join ','))
            """;

        return PowerShellCalistir(betik);
    }

    private static string PowerShellCalistir(string betik)
    {
        var yol = Path.Combine(Path.GetTempPath(), $"ada-ftp-yetki-{Guid.NewGuid():N}.ps1");
        // BOM'lu UTF-8: PowerShell 5.1 BOM yoksa dosyayi sistem kod sayfasiyla okur.
        File.WriteAllText(yol, betik, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try
        {
            using var surec = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{yol}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            })!;

            var cikti = surec.StandardOutput.ReadToEnd();
            var hata = surec.StandardError.ReadToEnd();
            surec.WaitForExit(60_000);
            return cikti + hata;
        }
        finally
        {
            try { File.Delete(yol); } catch { /* en iyi çaba */ }
        }
    }
}
