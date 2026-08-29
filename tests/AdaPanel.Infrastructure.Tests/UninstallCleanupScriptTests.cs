using System.Reflection;
using AdaPanel.Infrastructure.Ftp;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Kaldirma betiginin GERCEKTEN calisan davranisi.
/// </summary>
/// <remarks>
/// <para>
/// KORUNAN SEY AGIR. Panel her FTP hesabi icin bir YEREL WINDOWS HESABI acar. Kaldirma betigi bu
/// hesaplari temizlemeye calisiyordu ama kayit dosyasinin yolunda bir ters bolu eksikti:
/// <c>Join-Path $DataRoot "configtp-users.json"</c>. Dosya HICBIR ZAMAN bulunmuyor, fonksiyon her
/// seferinde "FTP kullanici kaydi yok" deyip donuyordu. Sonuc: panel kaldirildiktan sonra
/// sunucuda, parolalari musterilerin elinde olan N adet yerel hesap kaliyordu.
/// </para>
/// <para>
/// Ayni ozellikte IKINCI, BAGIMSIZ bir hata daha vardi: veri koruma dali (VARSAYILAN secenek ve
/// sessiz kaldirmanin kullandigi dal) <c>exit 0</c> ile cikiyordu, hesap temizligi ise cok daha
/// asagida cagriliyordu -- yani varsayilan yolda o koda ZATEN HIC ULASILMIYORDU.
/// </para>
/// <para>
/// Bu testler betigin kopyasini degil, depodaki GERCEK dosyayi <c>-DefineFunctionsOnly</c> ile
/// dot-source eder. Ana akis (servis silme, klasor silme) asla kosturulmaz.
/// </para>
/// </remarks>
public sealed class UninstallCleanupScriptTests : IDisposable
{
    private readonly string calisma = Path.Combine(Path.GetTempPath(), $"ada-kaldirma-{Guid.NewGuid():N}");

    public UninstallCleanupScriptTests() => Directory.CreateDirectory(calisma);

    public void Dispose()
    {
        try { Directory.Delete(calisma, recursive: true); } catch { /* en iyi caba */ }
    }

    /// <summary>
    /// Kayit dosyasinin yolu, panelin GERCEKTEN yazdigi yol olmali.
    /// </summary>
    /// <remarks>
    /// Beklenen yol ELLE YAZILMAZ: <c>FtpService.StatePath</c> sabitinin degeri yansima ile
    /// okunur. Boylece test, panelin kendi tek kaynagina bagli kalir -- panel yolu degistirirse
    /// test de onunla degisir, tahmine dayali bir esitlik kurulmaz.
    /// </remarks>
    [Fact]
    public void Kayit_dosyasi_panelin_YAZDIGI_yolda_aranir()
    {
        var dataRoot = Path.Combine(calisma, "veri");
        var goreceliYol = FtpServiceStatePath();
        var kayitYolu = Path.Combine(dataRoot, goreceliYol.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(kayitYolu)!);
        File.WriteAllText(kayitYolu, """[{"UserName":"ftp_musteri1"},{"UserName":"ftp_musteri2"}]""");

        var sonuc = FonksiyonlariCalistir($$"""
            $hesaplar = @(Get-AdaFtpAccounts -DataRoot {{ScriptTestHost.PowerShellLiteral(dataRoot)}})
            Write-Output "SAYI=$($hesaplar.Count)"
            foreach ($h in $hesaplar) { Write-Output "AD=$($h.UserName)" }
            """);

        Assert.Equal(0, sonuc.ExitCode);
        // Yol yanlisken bu sayi HER ZAMAN 0 idi -- ozelligin olu olmasinin tam olcusu budur.
        Assert.Contains("SAYI=2", sonuc.Tumu);
        Assert.Contains("AD=ftp_musteri1", sonuc.Tumu);
        Assert.Contains("AD=ftp_musteri2", sonuc.Tumu);
    }

    /// <summary>
    /// Bozuk bir kayit dosyasi SESSIZCE "hesap yok" diye yorumlanmamali.
    /// </summary>
    /// <remarks>
    /// Eski kod okuma hatasini yakalayip <c>return</c> ediyordu; betigin tamami
    /// <c>SilentlyContinue</c> altinda kostugu icin de kimse gormuyordu. Okunamayan bir kayit,
    /// "silinecek hesap yok" ile AYNI SEY DEGILDIR.
    /// </remarks>
    [Fact]
    public void Bozuk_kayit_dosyasi_sessizce_bos_sayilmaz()
    {
        var dataRoot = Path.Combine(calisma, "bozuk");
        var kayitYolu = Path.Combine(dataRoot, FtpServiceStatePath().Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(kayitYolu)!);
        File.WriteAllText(kayitYolu, "{ bu gecerli JSON degil ");

        var sonuc = FonksiyonlariCalistir($$"""
            try {
                Get-AdaFtpAccounts -DataRoot {{ScriptTestHost.PowerShellLiteral(dataRoot)}} | Out-Null
                Write-Output "SONUC=sessiz"
            } catch {
                Write-Output "SONUC=hata"
            }
            """);

        Assert.Equal(0, sonuc.ExitCode);
        Assert.Contains("SONUC=hata", sonuc.Tumu);
    }

    /// <summary>
    /// Veri koruma modu hesaplari SILMEZ ama SESSIZ de kalmaz: rapora yazar.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Siteler calismaya devam ettigi icin FTP erisiminin birakilmasi KASITLIDIR. Kasitli olan sey
    /// gizli olmamalidir: sunucuda parolalari musteride olan yerel hesaplar kaldiysa, kaldirmayi
    /// yapan kisi bunu bir yerde GORMELIDIR.
    /// </para>
    /// <para>
    /// <b>OLCULEN SINIR:</b> bu test GERCEK yerel Windows hesabi ACAMAZ (yonetici gerektirir ve
    /// test makinesini kalici olarak degistirirdi). Bu yuzden hesaplarin durumu burada
    /// "zaten yok" olarak olculur; "hesap HALA VAR" dalinin metni
    /// <see cref="Var_olan_hesap_raporda_KALDI_olarak_isaretlenir"/> ile ayrica dogrulanir.
    /// Kayittan okuma ve rapora yazma zinciri ise bastan sona GERCEKTIR.
    /// </para>
    /// </remarks>
    [Fact]
    public void Veri_koruma_modunda_kalan_hesaplar_rapora_yazilir()
    {
        var dataRoot = Path.Combine(calisma, "korunan");
        var kayitYolu = Path.Combine(dataRoot, FtpServiceStatePath().Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(kayitYolu)!);
        File.WriteAllText(kayitYolu, """[{"UserName":"ftp_kalan1"},{"UserName":"ftp_kalan2"}]""");

        var sonuc = FonksiyonlariCalistir($$"""
            $kok = {{ScriptTestHost.PowerShellLiteral(dataRoot)}}
            $hesaplar = @(Remove-AdaFtpAccounts -DataRoot $kok -ListOnly)
            $satirlar = New-AdaKoruModuRaporu -DataRoot $kok -Hesaplar $hesaplar `
                -FirewallKurallari @('AdaPanel HTTPS 8890') -Sertifikalar @('Cert:\LocalMachine\My\ABC')
            $yol = Write-AdaKaldirmaRaporu -Yol (Get-AdaRaporYolu -DataRoot $kok) -Satirlar $satirlar
            Write-Output "RAPOR=$yol"
            """);

        Assert.Equal(0, sonuc.ExitCode);

        var raporYolu = Path.Combine(dataRoot, "kaldirma-raporu.txt");
        Assert.True(File.Exists(raporYolu), $"Kaldirma raporu yazilmadi: {raporYolu}");

        var rapor = File.ReadAllText(raporYolu);
        Assert.Contains("ftp_kalan1", rapor);
        Assert.Contains("ftp_kalan2", rapor);
        // Hesabin adini yazmak yetmez; NE OLDUGUNU da soylemeli.
        Assert.Contains("YEREL WINDOWS HESABI", rapor);
        Assert.Contains("AdaPanel HTTPS 8890", rapor);
        Assert.Contains("Cert:\\LocalMachine\\My\\ABC", rapor);
    }

    /// <summary>
    /// Sunucuda HALA DURAN bir hesap raporda "KALDI" olarak ve NE YAPILACAGIYLA birlikte gorunur.
    /// </summary>
    /// <remarks>
    /// Adi yazmak yetmez. Kaldirmayi yapan kisi "bu nedir, ne yapmaliyim" sorusunun cevabini
    /// raporda bulmalidir; yoksa satiri okur ve gecer.
    /// </remarks>
    [Fact]
    public void Var_olan_hesap_raporda_KALDI_olarak_isaretlenir()
    {
        var dataRoot = Path.Combine(calisma, "duran");
        Directory.CreateDirectory(dataRoot);

        var sonuc = FonksiyonlariCalistir($$"""
            $hesap = [pscustomobject]@{ UserName = 'ftp_duran'; Durum = 'KALDI (veri koruma modu)' }
            $satirlar = New-AdaKoruModuRaporu -DataRoot {{ScriptTestHost.PowerShellLiteral(dataRoot)}} -Hesaplar @($hesap)
            $satirlar -join "`n"
            """);

        Assert.Equal(0, sonuc.ExitCode);
        Assert.Contains("ftp_duran", sonuc.Tumu);
        Assert.Contains("KALDI", sonuc.Tumu);
        // Kullaniciya ne yapacagi da soylenmeli.
        Assert.Contains("Remove-LocalUser", sonuc.Tumu);
    }

    /// <summary>
    /// Hicbir hesap kayitli degilse rapor bunu ACIKCA soyler (bos birakmaz).
    /// </summary>
    [Fact]
    public void Kayit_yoksa_rapor_bunu_acikca_yazar()
    {
        var dataRoot = Path.Combine(calisma, "bos");
        Directory.CreateDirectory(dataRoot);

        var sonuc = FonksiyonlariCalistir($$"""
            $kok = {{ScriptTestHost.PowerShellLiteral(dataRoot)}}
            $hesaplar = @(Remove-AdaFtpAccounts -DataRoot $kok -ListOnly)
            Write-Output "SAYI=$($hesaplar.Count)"
            $satirlar = New-AdaKoruModuRaporu -DataRoot $kok -Hesaplar $hesaplar
            Write-AdaKaldirmaRaporu -Yol (Get-AdaRaporYolu -DataRoot $kok) -Satirlar $satirlar | Out-Null
            """);

        Assert.Equal(0, sonuc.ExitCode);
        Assert.Contains("SAYI=0", sonuc.Tumu);
        Assert.Contains("kayitli hesap yok", File.ReadAllText(Path.Combine(dataRoot, "kaldirma-raporu.txt")));
    }

    /// <summary>
    /// BAGLANTI COZULEMEDIYSE AGAC SILINMEZ.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Eski dolasim, <c>EnumerateDirectories</c> hata verince <c>continue</c> ediyordu: o alt
    /// agactaki junction'lar KALDIRILMADAN kaliyor, hemen ardindan gelen
    /// <c>Remove-Item -Recurse</c> ise onlari TAKIP EDIYORDU. Yani erisim reddedilen tek bir alt
    /// klasor, onlenmeye calisilan musteri-dosyasi kaybini geri getiriyordu.
    /// </para>
    /// <para>
    /// "Erisim reddedildi" durumu bu makinede guvenilir sekilde uretilemiyor (testler yonetici
    /// olarak kosarsa ACL asilir), bu yuzden listeleyici enjekte edilir. Enjekte edilen tek sey
    /// LISTELEME; karar mantigi ve silme cagrisi betigin GERCEK kodudur.
    /// </para>
    /// </remarks>
    [Fact]
    public void Listelenemeyen_bir_alt_agac_varsa_silme_REDDEDILIR()
    {
        var kok = Path.Combine(calisma, "veri-koku");
        Directory.CreateDirectory(Path.Combine(kok, "site-dosyalari"));
        File.WriteAllText(Path.Combine(kok, "site-dosyalari", "index.php"), "site");

        var sonuc = FonksiyonlariCalistir($$"""
            $kok = {{ScriptTestHost.PowerShellLiteral(kok)}}
            $sonuc = Remove-AdaAgacGuvenli -Root $kok -Enumerator { param($p) throw 'Erisim reddedildi.' }
            Write-Output "SILINDI=$($sonuc.Silindi)"
            Write-Output "BASARISIZ=$($sonuc.Basarisiz.Count)"
            """);

        Assert.Equal(0, sonuc.ExitCode);
        Assert.Contains("SILINDI=False", sonuc.Tumu);
        Assert.Contains("BASARISIZ=1", sonuc.Tumu);

        // Asil olcut: klasor DURUYOR. Silmeyi reddetmek, musterinin sitesini silme riskini alir.
        Assert.True(Directory.Exists(kok), "Baglanti cozulemedigi halde agac silinmis.");
        Assert.True(File.Exists(Path.Combine(kok, "site-dosyalari", "index.php")));
    }

    /// <summary>
    /// Guvenli silme, MESRU silmeyi engellememeli: sorun yoksa agac gercekten gider.
    /// </summary>
    /// <remarks>
    /// Reddetme davranisinin degeri, "her seyi reddet" olmamasindadir. Bu test onu sabitler.
    /// </remarks>
    [Fact]
    public void Sorun_yoksa_agac_gercekten_silinir()
    {
        var kok = Path.Combine(calisma, "silinecek");
        Directory.CreateDirectory(Path.Combine(kok, "alt", "daha-alt"));
        File.WriteAllText(Path.Combine(kok, "alt", "dosya.txt"), "veri");

        var sonuc = FonksiyonlariCalistir($$"""
            $sonuc = Remove-AdaAgacGuvenli -Root {{ScriptTestHost.PowerShellLiteral(kok)}}
            Write-Output "SILINDI=$($sonuc.Silindi)"
            """);

        Assert.Equal(0, sonuc.ExitCode);
        Assert.Contains("SILINDI=True", sonuc.Tumu);
        Assert.False(Directory.Exists(kok));
    }

    /// <summary>
    /// GERCEK bir junction ile: baglanti kalkar, HEDEFTEKI musteri dosyalari durur, agac silinir.
    /// </summary>
    [Fact]
    public void Junction_iceren_agac_silinirken_hedefteki_site_dosyalari_KORUNUR()
    {
        var siteKoku = Path.Combine(calisma, "musteri-sitesi");
        Directory.CreateDirectory(Path.Combine(siteKoku, "wp-content"));
        File.WriteAllText(Path.Combine(siteKoku, "index.php"), "site");
        File.WriteAllText(Path.Combine(siteKoku, "wp-content", "tema.css"), "stil");

        var kok = Path.Combine(calisma, "veri");
        var kullaniciKoku = Path.Combine(kok, "ftp-root", "LocalUser", "musteri");
        Directory.CreateDirectory(Path.GetDirectoryName(kullaniciKoku)!);

        var kurulum = ScriptTestHost.Run(
            $"New-Item -ItemType Junction -Path {ScriptTestHost.PowerShellLiteral(kullaniciKoku)} " +
            $"-Target {ScriptTestHost.PowerShellLiteral(siteKoku)} | Out-Null");
        Assert.Equal(0, kurulum.ExitCode);
        Assert.True(Directory.Exists(kullaniciKoku), "Junction kurulamadi; test onkosulu saglanmadi.");

        var sonuc = FonksiyonlariCalistir($$"""
            $sonuc = Remove-AdaAgacGuvenli -Root {{ScriptTestHost.PowerShellLiteral(kok)}}
            Write-Output "SILINDI=$($sonuc.Silindi)"
            """);

        Assert.Equal(0, sonuc.ExitCode);
        Assert.Contains("SILINDI=True", sonuc.Tumu);
        Assert.False(Directory.Exists(kok));

        // HEDEFTEKI HICBIR DOSYAYA dokunulmamis olmali.
        Assert.True(Directory.Exists(siteKoku));
        Assert.True(File.Exists(Path.Combine(siteKoku, "index.php")));
        Assert.True(File.Exists(Path.Combine(siteKoku, "wp-content", "tema.css")));
    }

    // --- duzenek ---

    /// <summary>
    /// Depodaki GERCEK betigi dot-source eder; ana akisi kosturmaz.
    /// </summary>
    private static ScriptTestHost.ScriptResult FonksiyonlariCalistir(string govde)
    {
        var betikYolu = ScriptTestHost.ScriptPath("scripts", "uninstall-cleanup.ps1");
        Assert.True(File.Exists(betikYolu), $"Kaldirma betigi bulunamadi: {betikYolu}");

        return ScriptTestHost.Run(
            $". {ScriptTestHost.PowerShellLiteral(betikYolu)} -DefineFunctionsOnly\n{govde}");
    }

    /// <summary>
    /// Panelin FTP kayit yolunu KENDI kaynagindan okur (tahmin etmez).
    /// </summary>
    private static string FtpServiceStatePath()
    {
        var alan = typeof(FtpService).GetField("StatePath", BindingFlags.NonPublic | BindingFlags.Static)
                   ?? typeof(FtpService).GetField("StatePath", BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(alan);
        var deger = alan!.GetRawConstantValue() as string;
        Assert.False(string.IsNullOrWhiteSpace(deger), "FtpService.StatePath okunamadi.");
        return deger!;
    }

    /// <summary>
    /// Tepsi görevi kurulduğu ADLA kaldırılmalı.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ÖLÇÜLDÜ.</b> <c>install-tray-task.ps1</c> görevi <c>schtasks /Create /TN 'AdaPanel Tepsi'</c>
    /// ile KLASÖRSÜZ, yani görev ağacının kökünde oluşturuyor. <c>uninstall-cleanup.ps1</c> ise iki
    /// yoldan temizlik yapıyor: sabit bir ad listesi ve <c>\AdaPanel\</c> klasör süpürmesi. Görev
    /// kökte durduğu için klasör süpürmesine takılmıyordu ve sabit listede de yoktu — yani kaldırma
    /// sonrası Windows'ta kalıyordu (kurulu makinede <c>schtasks /Query</c> ile görüldü).
    /// </para>
    /// <para>
    /// Bu test ADI iki dosyadan da OKUR ve eşleştirir; iki yerde iki sabit metin bırakmak, adın
    /// birinde değişip diğerinde kalmasıyla aynı sızıntıyı sessizce geri getirirdi.
    /// </para>
    /// </remarks>
    [Fact]
    public void Tepsi_gorevi_KURULDUGU_ADLA_kaldirilir()
    {
        var kurulum = File.ReadAllText(ScriptTestHost.ScriptPath("scripts", "install-tray-task.ps1"));

        // Adı TAHMİN ETMİYORUZ: kuran betikteki varsayılandan okuyoruz.
        var eslesme = System.Text.RegularExpressions.Regex.Match(
            kurulum,
            @"\$TaskName\s*=\s*'([^']+)'");

        Assert.True(eslesme.Success, "install-tray-task.ps1 icinde $TaskName varsayilani bulunamadi.");
        var gorevAdi = eslesme.Groups[1].Value;

        var kaldirma = File.ReadAllText(ScriptTestHost.ScriptPath("scripts", "uninstall-cleanup.ps1"));

        // YALNIZCA $taskNames DIZISININ ICINE bakiyoruz.
        //
        // MUTASYONLA OLCULDU: once dosyanin TAMAMINDA arama yapiyorduk ve dizi girdisi silindigi
        // halde test YESIL kaliyordu -- cunku ad, dizinin hemen ustundeki ACIKLAMA METNINDE de
        // gecmektedir. Yani test korumayi degil, kendi yorumunu dogruluyordu. Aranan sey artik
        // GERCEKTEN CALISAN kod.
        var dizi = System.Text.RegularExpressions.Regex.Match(
            kaldirma,
            @"\$taskNames\s*=\s*@\((?<govde>[^)]*)\)",
            System.Text.RegularExpressions.RegexOptions.Singleline);

        Assert.True(dizi.Success, "uninstall-cleanup.ps1 icinde $taskNames dizisi bulunamadi.");

        var govde = dizi.Groups["govde"].Value;
        Assert.Contains($"\"{gorevAdi}\"", govde);
    }
}
