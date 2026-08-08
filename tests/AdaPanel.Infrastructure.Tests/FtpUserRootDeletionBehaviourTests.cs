using System.Diagnostics;
using AdaPanel.Infrastructure.Ftp;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// FTP kullanıcı kökünün silinmesi — GERÇEK dosyalarla, gerçek junction'la.
/// </summary>
/// <remarks>
/// <para>
/// Bu davranışın mevcut testleri kaynak kodda DİZGE arıyordu (<c>Assert.Contains("[IO.Directory]::Delete…")</c>).
/// Böyle bir test, korunan şeyi değil onun bugünkü yazımını doğrular: değişken adı değişse ya da
/// çağrı ölü koda taşınsa test YEŞİL kalır ve hata geri gelir. Oysa burada korunan şey ağır:
/// FTP kullanıcı kökü müşterinin site klasörüne bir junction'dır ve <c>Remove-Item -Recurse</c>
/// Windows PowerShell 5.1'de bağlantıyı TAKİP EDER — yani bir FTP hesabını silmek müşterinin bütün
/// site dosyalarını silebilir. Yedekten dönmeden geri alınamaz.
/// </para>
/// <para>
/// Bu test panelin ÇALIŞTIRDIĞI dizgenin ta kendisini (<c>FtpService.RemoveUserRootSnippet</c>)
/// koşturur; bir kopyasını değil. Kopya test etmek hiçbir şey kanıtlamazdı.
/// </para>
/// <para>
/// <b>ÖNEMLİ SINIR — ölçüldü:</b> bu test tek başına korumanın VARLIĞINI kanıtlamaz. Koruma
/// kaldırılıp yerine düz <c>Remove-Item -Recurse</c> konarak denendi ve test YİNE GEÇTİ. Sebebi
/// ölçüldü: bu makinedeki PowerShell 5.1 (5.1.19041.3996) junction'ı zaten takip etmiyor — hata
/// sonraki 5.1 yapılarında düzeltilmiş. Yani tehlikeli davranış burada üretilemiyor.
/// </para>
/// <para>
/// Bu yüzden <c>ReparsePointSafetyTests</c> içindeki metin tabanlı denetimler KALDIRILMADI. Onlar
/// kırılgandır (yazım değişirse yanıltır) ama koruyucu dalın var olduğunu bu makinede doğrulayan
/// tek şeydir. Buradaki test ise SONUCU doğrular: bağlantı kalkar, hedefteki müşteri dosyaları
/// durur — hangi mekanizmayla olursa olsun. İkisi birlikte, tek başına her birinden iyidir.
/// </para>
/// </remarks>
public sealed class FtpUserRootDeletionBehaviourTests : IDisposable
{
    private readonly string calisma = Path.Combine(Path.GetTempPath(), $"ada-ftp-sil-{Guid.NewGuid():N}");

    public FtpUserRootDeletionBehaviourTests() => Directory.CreateDirectory(calisma);

    public void Dispose()
    {
        try { Directory.Delete(calisma, recursive: true); } catch { /* en iyi çaba */ }
    }

    [Fact]
    public void Kullanici_koku_bir_BAGLANTI_ise_hedefteki_musteri_dosyalari_KORUNUR()
    {
        // Müşterinin gerçek site klasörü ve içindeki dosyalar.
        var siteKoku = Path.Combine(calisma, "musteri-sitesi");
        Directory.CreateDirectory(Path.Combine(siteKoku, "wp-content"));
        File.WriteAllText(Path.Combine(siteKoku, "index.php"), "site");
        File.WriteAllText(Path.Combine(siteKoku, "wp-config.php"), "gizli");
        File.WriteAllText(Path.Combine(siteKoku, "wp-content", "tema.css"), "stil");

        // FTP kullanıcı kökü: siteye işaret eden bir junction.
        var kullaniciKoku = Path.Combine(calisma, "ftp-root", "LocalUser", "musteri");
        Directory.CreateDirectory(Path.GetDirectoryName(kullaniciKoku)!);
        JunctionOlustur(kullaniciKoku, siteKoku);
        Assert.True(Directory.Exists(kullaniciKoku), "Junction kurulamadı; test önkoşulu sağlanmadı.");

        SnippetiCalistir(kullaniciKoku);

        // Bağlantı gitmeli...
        Assert.False(Directory.Exists(kullaniciKoku));
        // ...ama HEDEFTEKİ HİÇBİR DOSYAYA dokunulmamalı.
        Assert.True(Directory.Exists(siteKoku));
        Assert.True(File.Exists(Path.Combine(siteKoku, "index.php")));
        Assert.True(File.Exists(Path.Combine(siteKoku, "wp-config.php")));
        Assert.True(File.Exists(Path.Combine(siteKoku, "wp-content", "tema.css")));
    }

    [Fact]
    public void Kullanici_koku_GERCEK_klasor_ise_normal_silinir()
    {
        // Güvenli silme, MEŞRU silmeyi engellememelidir: bağlantı olmayan bir kök gerçekten gider.
        var kullaniciKoku = Path.Combine(calisma, "gercek-kok");
        Directory.CreateDirectory(Path.Combine(kullaniciKoku, "alt"));
        File.WriteAllText(Path.Combine(kullaniciKoku, "dosya.txt"), "veri");

        SnippetiCalistir(kullaniciKoku);

        Assert.False(Directory.Exists(kullaniciKoku));
    }

    [Fact]
    public void Kullanici_koku_YOKSA_hata_vermez()
    {
        // Silme akışı idempotent olmalı: hesap zaten temizlenmişse ikinci çağrı düşmemeli.
        var yok = Path.Combine(calisma, "hic-olmayan");

        var cikis = SnippetiCalistir(yok);

        Assert.Equal(0, cikis);
    }

    // --- düzenek ---

    /// <summary>Panelin gerçekten çalıştırdığı dizgeyi koşturur — kopyasını değil.</summary>
    private static int SnippetiCalistir(string userRoot)
    {
        var betik = $"$userRoot = '{userRoot.Replace("'", "''")}'\n{FtpService.RemoveUserRootSnippet}";
        return PowerShellCalistir(betik);
    }

    private static void JunctionOlustur(string link, string hedef)
        => PowerShellCalistir($"New-Item -ItemType Junction -Path '{link.Replace("'", "''")}' -Target '{hedef.Replace("'", "''")}' | Out-Null");

    private static int PowerShellCalistir(string betik)
    {
        var yol = Path.Combine(Path.GetTempPath(), $"ada-test-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(yol, "$ErrorActionPreference = 'Stop'\n" + betik);
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

            surec.WaitForExit(60_000);
            return surec.ExitCode;
        }
        finally
        {
            try { File.Delete(yol); } catch { /* en iyi çaba */ }
        }
    }
}
