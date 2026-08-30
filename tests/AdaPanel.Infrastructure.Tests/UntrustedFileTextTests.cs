using System.Text;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// <see cref="UntrustedFileText"/> — boyutunu KİRACININ belirlediği dosyaların sınırlı okunması.
/// </summary>
/// <remarks>
/// Kapatılan açık: panel, kiracının kendi yazabildiği dosyaları (<c>web.config</c>,
/// <c>wp-config.php</c>) <c>File.ReadAllText</c> ile tamamen belleğe alıyordu. Dizin korumasında
/// bu bir DÖNGÜ içindeydi — tek istekte N dosya, hem dosya boyutu hem dosya sayısı kiracıda.
/// </remarks>
public sealed class UntrustedFileTextTests : IDisposable
{
    private readonly string klasor = Path.Combine(Path.GetTempPath(), "adapanel-untrusted-" + Guid.NewGuid().ToString("N"));

    public UntrustedFileTextTests() => Directory.CreateDirectory(klasor);

    public void Dispose()
    {
        try { Directory.Delete(klasor, recursive: true); } catch { /* temizlik zorunlu değil */ }
    }

    private string Yaz(string ad, string icerik)
    {
        var yol = Path.Combine(klasor, ad);
        File.WriteAllText(yol, icerik, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return yol;
    }

    /// <summary>
    /// FAZLA KAPATMA NÖBETÇİSİ: gerçek bir yapılandırma dosyası HÂLÂ okunmalı.
    /// </summary>
    /// <remarks>
    /// Yalnız "büyük dosya reddedildi" ölçmek, HER dosyayı reddeden bir düzeltmeyi de yeşil
    /// gösterirdi — o düzeltme dizin korumasını ve WordPress güvenlik kartını sessizce ölü
    /// bırakırdı. Panel o zaman yanlış cevap verirdi, ki bu okumamaktan kötüdür.
    /// </remarks>
    [Fact]
    public void Normal_boyutlu_dosya_okunur()
    {
        var yol = Yaz("web.config", "<configuration><!-- AdaPanelKoruma --></configuration>");

        Assert.True(UntrustedFileText.TryRead(yol, out var icerik));
        Assert.Contains("AdaPanelKoruma", icerik, StringComparison.Ordinal);
    }

    [Fact]
    public void Sinirin_ustundeki_dosya_OKUNMAMIS_sayilir()
    {
        var yol = Yaz("sisik.config", new string('x', 4096));

        Assert.False(UntrustedFileText.TryRead(yol, out var icerik, maxBytes: 1024));
        Assert.Equal(string.Empty, icerik);
    }

    [Fact]
    public void Sinirin_tam_ustu_ve_tam_alti_ayrilir()
    {
        // Sınırın hangi tarafta olduğu KARARDIR, kaza değil: tam sınıra eşit dosya OKUNUR.
        // Test bunu sabitler ki bir gün ">=" yazan biri meşru dosyaları elemeye başlamasın.
        var tam = Yaz("tam.config", new string('a', 100));
        var birFazla = Yaz("fazla.config", new string('a', 101));

        Assert.True(UntrustedFileText.TryRead(tam, out _, maxBytes: 100));
        Assert.False(UntrustedFileText.TryRead(birFazla, out _, maxBytes: 100));
    }

    [Fact]
    public void Olmayan_ve_gecersiz_girdiler_false_doner()
    {
        Assert.False(UntrustedFileText.TryRead(Path.Combine(klasor, "yok.config"), out _));
        Assert.False(UntrustedFileText.TryRead("   ", out _));
        Assert.False(UntrustedFileText.TryRead(Yaz("var.config", "x"), out _, maxBytes: 0));
        Assert.False(UntrustedFileText.TryRead(klasor, out _));   // dizin: fırlatmamalı
    }

    /// <summary>
    /// Dosyayı YAZAN süreç onu açık tutarken de okunabilmeli.
    /// </summary>
    /// <remarks>
    /// <c>File.ReadAllText</c> dosyayı <c>FileShare.Read</c> ile açar ve yazan bir tutamakla
    /// çakışır. <c>wp-config.php</c> ve <c>web.config</c> tam da PHP/IIS tarafından açık
    /// tutulabilecek dosyalardır; eski yol tam o anda düşerdi.
    /// </remarks>
    [Fact]
    public void Yazan_surec_dosyayi_acik_tutarken_de_okunur()
    {
        var yol = Yaz("kilitli.config", "DISALLOW_FILE_EDIT");
        using var yazan = new FileStream(yol, FileMode.Open, FileAccess.Write, FileShare.Read);

        Assert.Throws<IOException>(() => File.ReadAllText(yol));
        Assert.True(UntrustedFileText.TryRead(yol, out var icerik));
        Assert.Equal("DISALLOW_FILE_EDIT", icerik);
    }

    /// <summary>
    /// İki çağrı yeri GERÇEKTEN bu kapıdan geçiyor mu.
    /// </summary>
    /// <remarks>
    /// Bu depoda "koruma yazılmış ama o yolda uygulanmıyor" tekrar eden hata sınıfıdır; bu
    /// oturumda PHP izolasyonu tam bu şekilde ölü bulundu. Yasak ŞEKLE konur: kiracının
    /// yazabildiği dosyayı sınırsız okuyan çağrı KALMAMALI.
    /// </remarks>
    [Theory]
    [InlineData("src/AdaPanel.Infrastructure/Iis/DirectoryProtectionService.cs", "web.config")]
    [InlineData("src/AdaPanel.Infrastructure/AppStore/WordPressToolkitService.cs", "DISALLOW_FILE_EDIT")]
    public void Cagri_yerleri_sinirli_okuyucudan_geciyor(string goreliYol, string yakinlik)
    {
        var kaynak = File.ReadAllText(Path.Combine(KaynakKoku(), goreliYol.Replace('/', Path.DirectorySeparatorChar)));

        Assert.Contains("UntrustedFileText.TryRead(", kaynak, StringComparison.Ordinal);

        // İlgili bölümde artık sınırsız okuma olmamalı. Dosyanın TAMAMINDA yasaklamıyoruz:
        // aynı dosyada panelin KENDİ ürettiği (yani boyutunu panelin belirlediği) dosyaları
        // okuyan meşru çağrılar olabilir; yasak kiracının yazdığı dosyanın yanındaki okumaya konur.
        var indeks = kaynak.IndexOf(yakinlik, StringComparison.Ordinal);
        Assert.True(indeks > 0, $"beklenen isaret bulunamadi: {yakinlik}");

        var pencere = kaynak[Math.Max(0, indeks - 1200)..Math.Min(kaynak.Length, indeks + 1200)];
        Assert.DoesNotContain("File.ReadAllText(configPath)", pencere, StringComparison.Ordinal);
        Assert.DoesNotContain("File.ReadAllText(webConfig)", pencere, StringComparison.Ordinal);
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
