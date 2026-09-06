using AdaPanel.Infrastructure.SelfUpdate;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// K12 — güncelleme paketinin GÜVEN kuralları.
/// </summary>
/// <remarks>
/// <para>
/// TEHDİT: indirilen exe <b>SYSTEM</b> olarak çalıştırılıyor (<c>WindowsUpdateInstallerLauncher</c>,
/// zamanlanmış görev, <c>-UserId 'SYSTEM' -RunLevel Highest</c>). Panelin "Administrator" rolü bir
/// Windows yöneticisi DEĞİLDİR — TEK istisna sunucu sahibidir (SYSTEM kabuğu veren terminal ucu
/// yalnız ona açık; bkz. <c>IServerOwnerService</c>). Dolayısıyla güncelleme kaynağını serbest
/// bırakmak, panelden yaratılan bir EK yönetici hesabını doğrudan SYSTEM'e yükseltmekti.
/// </para>
/// <para>
/// Bu kararların hepsi SAF fonksiyonlarda toplandı, çünkü bulaşık bir paket bu makinede
/// ÜRETİLEMEZ; üretilemeyen bir durumun kararı yine de doğrulanmak zorundadır.
/// </para>
/// </remarks>
public sealed class UpdatePackageTrustTests
{
    [Theory]
    [InlineData("aliorala/adapanel", "aliorala", "adapanel")]
    [InlineData("  aliorala/adapanel  ", "aliorala", "adapanel")]
    [InlineData("github.com/aliorala/adapanel", "aliorala", "adapanel")]
    [InlineData("https://github.com/aliorala/adapanel", "aliorala", "adapanel")]
    [InlineData("https://github.com/aliorala/adapanel.git", "aliorala", "adapanel")]
    [InlineData("https://github.com/aliorala/adapanel/releases", "aliorala", "adapanel")]
    public void DepoyuCozumle_KabulEttigiBicimler(string girdi, string owner, string name)
    {
        var cozum = UpdatePackageTrust.DepoyuCozumle(girdi);

        Assert.NotNull(cozum);
        Assert.Equal(owner, cozum!.Value.Owner);
        Assert.Equal(name, cozum.Value.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("adapanel")]
    [InlineData("/")]
    public void DepoyuCozumle_CozulemeyenlerNullDoner(string girdi)
        => Assert.Null(UpdatePackageTrust.DepoyuCozumle(girdi));

    /// <summary>
    /// İZİN LİSTESİ. Boş dize "GitHub kapalı" demektir ve geçerli bir durumdur; onun dışında
    /// yalnızca listedeki depo kabul edilir.
    /// </summary>
    [Theory]
    [InlineData("aliorala/adapanel", true)]
    [InlineData("ALIORALA/ADAPANEL", true)]                    // büyük/küçük harf duyarsız
    [InlineData("https://github.com/aliorala/adapanel", true)] // aynı depo, URL biçiminde
    [InlineData("", true)]                                      // GitHub kapalı
    [InlineData("saldirgan/adapanel", false)]                   // sahibi farklı
    [InlineData("aliorala/kotu-depo", false)]                   // deposu farklı
    [InlineData("https://github.com/saldirgan/adapanel", false)]
    // FAZLA SEGMENTLER ATILIR ve bu BİLİNÇLİ: "github.com/aliorala/adapanel/releases" gibi meşru
    // bir yapıştırma da fazladan segment taşır. Önemli olan, izin denetimi ile GitHub API çağrısının
    // AYNI ayrıştırıcıyı kullanması: ikisi de owner="aliorala", repo="adapanel" görür, yani izin
    // verilen depo ile gerçekten çekilen depo AYNIDIR. Bu satır o eşitliği sabitler — ayrıştırıcı
    // ikiye ayrılırsa (biri "geçerli", diğeri "geçersiz" derse) izin denetimi atlanabilir hâle gelir.
    [InlineData("aliorala/adapanel/../../saldirgan/adapanel", true)]
    public void DepoIzinli_YalnizcaListedekiDepoyuKabulEder(string repo, bool beklenen)
        => Assert.Equal(beklenen, UpdatePackageTrust.DepoIzinli(repo));

    /// <summary>
    /// Yukarıdaki "fazla segment" davranışının ASIL güvencesi: izin verilen dize ne olursa olsun,
    /// ondan çözülen owner/repo listedekiyle aynı olmalı. Yani izin denetimini geçen bir değer,
    /// başka bir depoya gitmenin yolu olamaz.
    /// </summary>
    [Theory]
    [InlineData("aliorala/adapanel/../../saldirgan/adapanel")]
    [InlineData("https://github.com/aliorala/adapanel/releases/latest")]
    public void DepoIzinli_GecenDeger_HEP_ayni_depoya_cozulur(string repo)
    {
        Assert.True(UpdatePackageTrust.DepoIzinli(repo));

        var cozum = UpdatePackageTrust.DepoyuCozumle(repo);
        Assert.NotNull(cozum);
        Assert.Equal("aliorala", cozum!.Value.Owner);
        Assert.Equal("adapanel", cozum.Value.Name);
    }

    /// <summary>
    /// İNDİRME ADRESİ. Adres, yapılandırılmış deponun release varlığına bağlanır; manifest diskte
    /// duran bir dosyadan da gelebildiği için sunucu/yol denetimi olmadan o dosyaya yazabilen biri
    /// SYSTEM olarak çalışacak exe'nin kaynağını belirlerdi.
    /// </summary>
    [Theory]
    [InlineData("https://github.com/aliorala/adapanel/releases/download/0.6.4/AdaPanelSetup.exe", true)]
    [InlineData("https://objects.githubusercontent.com/gh-abcdef?token=x", true)]
    [InlineData("https://release-assets.githubusercontent.com/x/y", true)]
    // HTTP: paket SYSTEM olarak çalışıyor, aradaki biri onu değiştirebilir.
    [InlineData("http://github.com/aliorala/adapanel/releases/download/0.6.4/AdaPanelSetup.exe", false)]
    // Başka sunucu.
    [InlineData("https://saldirgan.test/AdaPanelSetup.exe", false)]
    // Doğru sunucu, BAŞKA depo: yol bağlaması bunu yakalar.
    [InlineData("https://github.com/saldirgan/adapanel/releases/download/9.9.9/AdaPanelSetup.exe", false)]
    // Doğru depo ama release varlığı değil.
    [InlineData("https://github.com/aliorala/adapanel/raw/main/kotu.exe", false)]
    // Sunucu adı ekli alan adı hilesi.
    [InlineData("https://github.com.saldirgan.test/aliorala/adapanel/releases/download/1/x.exe", false)]
    [InlineData("", false)]
    public void IndirmeAdresiIzinli_AdresiYapilandirilmisDepoyaBaglar(string url, bool beklenen)
        => Assert.Equal(beklenen, UpdatePackageTrust.IndirmeAdresiIzinli(url, "aliorala/adapanel"));

    /// <summary>
    /// PAKET KİMLİĞİ — bu depodaki PE bulaştırıcısı olayının doğrudan karşılığı.
    /// SHA256 karşılaştırması bu sınıfa KÖRDÜR: kaynakta bulaşma varsa yayındaki özet de bulaşık
    /// dosyanın özetidir ve doğrulama "temiz" der. Yakalayan tek şey paketin KİMLİĞİdir.
    /// </summary>
    [Fact]
    public void PaketKimligiKusuru_TemizPaketiKabulEder()
        => Assert.Null(UpdatePackageTrust.PaketKimligiKusuru("AdaPanel", "AdaPanel"));

    [Fact]
    public void PaketKimligiKusuru_BulasmaImzasiniReddeder()
    {
        var kusur = UpdatePackageTrust.PaketKimligiKusuru("Synaptics Pointing Device Driver", "AdaPanel");

        Assert.NotNull(kusur);
        Assert.Contains("BULASMA", kusur, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    // Sürüm kaynağı silinmiş: her gerçek AdaPanelSetup.exe bu alanları taşır (ölçüldü).
    [InlineData("", "")]
    [InlineData(null, null)]
    [InlineData("Baska Yayinci", "AdaPanel")]
    [InlineData("AdaPanel", "Baska Urun")]
    public void PaketKimligiKusuru_BeklenmedikYayinciyiReddeder(string? sirket, string? urun)
        => Assert.NotNull(UpdatePackageTrust.PaketKimligiKusuru(sirket, urun));

    /// <summary>
    /// İMZA — bugün ZORUNLU DEĞİL ve bu bir eksiklik değil, ÖLÇÜLMÜŞ bir kısıt:
    /// üretilen paket <c>NotSigned</c> ve depoda hiçbir <c>signtool</c> adımı yok. Zorunlu kılmak
    /// kurulu HER paneli güncellenemez bırakırdı — güvenlik adına işleyen tek güncelleme yolunu
    /// kapatmak olurdu.
    /// </summary>
    [Fact]
    public void ImzaKusuru_ImzasizPaketBugunGecer()
        => Assert.Null(UpdatePackageTrust.ImzaKusuru("NotSigned", string.Empty, imzaZorunlu: false));

    /// <summary>
    /// Ama imza VARSA geçerli olmak zorunda: imzalı bir ikilinin imzasını kaybetmesi,
    /// değiştirilmiş olmasının en güçlü işaretidir.
    /// </summary>
    [Theory]
    [InlineData("HashMismatch")]
    [InlineData("NotTrusted")]
    [InlineData("UnknownError")]
    public void ImzaKusuru_BozukImzayiReddeder(string durum)
        => Assert.NotNull(UpdatePackageTrust.ImzaKusuru(durum, "CN=AdaPanel", imzaZorunlu: false));

    [Fact]
    public void ImzaKusuru_GecerliImzaHerZamanGecer()
        => Assert.Null(UpdatePackageTrust.ImzaKusuru("Valid", "CN=AdaPanel", imzaZorunlu: true));

    /// <summary>
    /// Sertifika alındığında tek yapılacak şey bayrağı açmak: kural HAZIR ve sınanmış durumda.
    /// "Ölçülemedi" (boş durum) de ZORUNLU modda reddedilir — ölçülemeyeni geçerli saymak yok.
    /// </summary>
    [Theory]
    [InlineData("NotSigned")]
    [InlineData("")]
    public void ImzaKusuru_ZorunluModdaImzasiziReddeder(string durum)
        => Assert.NotNull(UpdatePackageTrust.ImzaKusuru(durum, string.Empty, imzaZorunlu: true));

    /// <summary>
    /// KAYMA KORUMASI. Beklenen yayıncı ÜÇ yerde yaşıyor: <c>installer\AdaPanel.iss</c> (paketi
    /// damgalayan), <c>UpdatePackageTrust</c> (indirileni denetleyen) ve
    /// <c>scripts\verify-publish-output.ps1</c> (üretileni denetleyen). Biri değişip diğerleri
    /// kalırsa self-update SESSİZCE her paketi reddeder ve kurulu paneller güncellenemez hâle
    /// gelir. Bu yüzden üçü burada birbirine bağlanır.
    /// </summary>
    [Fact]
    public void BeklenenYayinci_isste_kodda_ve_denetim_betiginde_AYNI()
    {
        var iss = File.ReadAllText(ScriptTestHost.ScriptPath("installer", "AdaPanel.iss"));
        Assert.Contains(
            $"#define MyAppPublisher \"{UpdatePackageTrust.BeklenenYayinci}\"",
            iss,
            StringComparison.Ordinal);
        Assert.Contains(
            $"#define MyAppName \"{UpdatePackageTrust.BeklenenYayinci}\"",
            iss,
            StringComparison.Ordinal);

        var betik = File.ReadAllText(ScriptTestHost.ScriptPath("scripts", "verify-publish-output.ps1"));
        Assert.Contains(
            $"$script:BeklenenPaketSirketi = \"{UpdatePackageTrust.BeklenenYayinci}\"",
            betik,
            StringComparison.Ordinal);
        Assert.Contains(
            $"$script:BeklenenPaketUrunu = \"{UpdatePackageTrust.BeklenenYayinci}\"",
            betik,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// ÜRETİLEN PAKET ARTIK DENETLENİYOR. Denetim betiği ISCC'den ÖNCE koşuyor ve yalnızca publish
    /// klasörünü tarıyordu; <c>AdaPanelSetup.exe</c> hiç görülmüyordu. Bulaşmış bir installer,
    /// özeti tuttuğu için kurulu tüm panellere SYSTEM olarak dağıtılırdı.
    /// </summary>
    [Theory]
    [InlineData("build-installer.ps1")]
    [InlineData("package-installer.ps1")]
    public void PaketlemeBetikleri_ISCCden_SONRA_installeri_denetler(string betikAdi)
    {
        var betik = File.ReadAllText(ScriptTestHost.ScriptPath("scripts", betikAdi));

        var isccIndeksi = betik.LastIndexOf("$iscc \"/DMyAppVersion", StringComparison.Ordinal);
        var denetimIndeksi = betik.IndexOf("-InstallerPath", StringComparison.Ordinal);

        Assert.True(isccIndeksi >= 0, $"{betikAdi}: ISCC cagrisi bulunamadi.");
        Assert.True(denetimIndeksi >= 0, $"{betikAdi}: uretilen installer HIC denetlenmiyor.");
        Assert.True(
            denetimIndeksi > isccIndeksi,
            $"{betikAdi}: installer denetimi ISCC'den ONCE calisiyor -- o anda denetlenecek paket henuz YOK.");
    }
}
