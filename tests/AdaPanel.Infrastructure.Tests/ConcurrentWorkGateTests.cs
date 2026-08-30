using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// AĞIR dosya uçlarında EŞZAMANLILIK tavanı.
/// </summary>
/// <remarks>
/// <para>
/// <b>KAPATILAN AÇIK (ölçüldü):</b> <c>FilesController</c> <c>Customer</c> rolüne açık ve ağır
/// uçlarının hiçbirinde eşzamanlılık denetimi yoktu. <c>FileService</c>'in öge/süre bütçeleri TEK
/// bir isteğin ne kadar süreceğini sınırlıyor, kaç isteğin AYNI ANDA koşacağını sınırlamıyordu.
/// En ucuz yol boyut hesabıydı: kimlik doğrulamalı bir <c>POST</c>, hız sınırı yok ve her çağrı
/// tam hızda 15 saniyeye kadar ağaç yürüyüşü — N istek = N tarama, hepsi LocalSystem olarak.
/// </para>
/// <para>
/// <b>NE ÖLÇÜLÜYOR:</b> (1) kapının kendisi gerçekten sayıyor mu, (2) kiracılar birbirinin
/// bütçesini yiyor mu, (3) ağır uçların HEPSİ kapıdan geçiyor mu. Üçüncüsü kaynak taramasıdır ve
/// bilinçlidir: korunan şey tek bir davranış değil, "yeni bir ağır uç eklenince korumayı unutmak"
/// hata sınıfıdır — bu depoda aynı sınıf kota denetiminde zaten bir kez gerçekleşti (kota yalnız
/// yükleme ucunun gövdesine gömülüydü, kopyalama/yazma/çıkarma korumasız doğdu).
/// </para>
/// </remarks>
public sealed class ConcurrentWorkGateTests
{
    private const string Anahtar = "files.heavy:kiraci-a";

    /// <summary>Tavan dolunca sıradaki istek BEKLETİLMEZ, 429 ile REDDEDİLİR.</summary>
    /// <remarks>
    /// Beklemek kullanıcıya "çalışıyor" izlenimi verirken sunucuda bağlantı ve görev tutar; yani
    /// kaynak tüketimi açığı kuyruk tüketimi açığına dönüşürdü. (Mutasyon: <c>Wait(0)</c> yerine
    /// tavansız <c>Wait()</c> yazılırsa bu test SONSUZA KADAR asılır ve zaman aşımıyla kırılır.)
    /// </remarks>
    [Fact]
    public void Tavan_dolunca_sonraki_istek_429_alir()
    {
        var kapi = new ConcurrentWorkGate();

        using var birinci = kapi.Enter(Anahtar, 2, "files.too_many_operations");
        using var ikinci = kapi.Enter(Anahtar, 2, "files.too_many_operations");

        var hata = Assert.Throws<AppException>(() => kapi.Enter(Anahtar, 2, "files.too_many_operations"));

        Assert.Equal(429, hata.HttpStatusCode);
        Assert.Equal("files.too_many_operations", hata.MessageKey);
    }

    /// <summary>İş bitince yer GERİ VERİLİR; aksi hâlde kapı bir kez dolup bir daha açılmazdı.</summary>
    [Fact]
    public void Is_bitince_yer_geri_verilir()
    {
        var kapi = new ConcurrentWorkGate();

        using (kapi.Enter(Anahtar, 1, "files.too_many_operations"))
        {
            Assert.Throws<AppException>(() => kapi.Enter(Anahtar, 1, "files.too_many_operations"));
        }

        // Kapsam kapandı: yer yeniden alınabilmeli.
        using var sonraki = kapi.Enter(Anahtar, 1, "files.too_many_operations");
        Assert.NotNull(sonraki);
    }

    /// <summary>
    /// ÇİFT <c>Dispose</c> sayacı ŞİŞİRMEZ.
    /// </summary>
    /// <remarks>
    /// Şişirseydi koruma sessizce gevşerdi: tavan 2 iken 3, 4, 5 iş geçmeye başlardı ve bunu
    /// hiçbir hata mesajı göstermezdi.
    /// </remarks>
    [Fact]
    public void Cift_dispose_tavani_sismez()
    {
        var kapi = new ConcurrentWorkGate();

        var kapsam = kapi.Enter(Anahtar, 1, "files.too_many_operations");
        kapsam.Dispose();
        kapsam.Dispose();

        using var birinci = kapi.Enter(Anahtar, 1, "files.too_many_operations");
        Assert.Throws<AppException>(() => kapi.Enter(Anahtar, 1, "files.too_many_operations"));
    }

    /// <summary>
    /// KİRACI İZOLASYONU: bir kiracının dolu bütçesi KOMŞUYU etkilemez.
    /// </summary>
    /// <remarks>
    /// Bütçe makine geneli olsaydı koruma, kapatmaya çalıştığı şeyin aynısını üretirdi: yeterince
    /// ağır iş açan tek kiracı bütün panelin dosya yöneticisini kilitlerdi.
    /// (Mutasyon: <c>Enter</c> anahtarı yok sayıp sabit bir anahtar kullanırsa bu test kırılır.)
    /// </remarks>
    [Fact]
    public void Bir_kiracinin_dolu_butcesi_komsuyu_engellemez()
    {
        var kapi = new ConcurrentWorkGate();

        using var kiraciA = kapi.Enter("files.heavy:kiraci-a", 1, "files.too_many_operations");
        Assert.Throws<AppException>(() => kapi.Enter("files.heavy:kiraci-a", 1, "files.too_many_operations"));

        using var kiraciB = kapi.Enter("files.heavy:kiraci-b", 1, "files.too_many_operations");
        Assert.NotNull(kiraciB);
    }

    /// <summary>Sınır 0/negatif verilirse kapı UYGULANMAZ (açıkça "sınırsız" diyen çağıran için).</summary>
    [Fact]
    public void Sifir_sinir_kapiyi_uygulamaz()
    {
        var kapi = new ConcurrentWorkGate();

        using var birinci = kapi.Enter(Anahtar, 0, "files.too_many_operations");
        using var ikinci = kapi.Enter(Anahtar, 0, "files.too_many_operations");

        Assert.NotNull(ikinci);
    }

    // ===============================================================================================
    // KAPSAM NOBETCISI: agir uclarin HEPSI kapidan geciyor mu
    // ===============================================================================================

    /// <summary>
    /// Ağır sayılan uçlar ve HER BİRİNİN neden ağır olduğu.
    /// </summary>
    /// <remarks>
    /// Bu bir DAHİL ETME listesidir ve elle yazılmıştır — ama kayması GÖRÜNÜR: aşağıdaki ikinci
    /// test, listedeki bir metodun dosyada gerçekten var olduğunu da ölçer, yani ad değişince
    /// liste sessizce ölemez.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> AgirUclar = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["CompressAsync"] = "Tum agaci okuyup ZIP yazar; CPU + disk, istek is parcaciginda.",
        ["ExtractAsync"] = "Arsivi acar: sikistirilmis boyutun katlari kadar yazma.",
        ["CopyAsync"] = "Agac yuruyusu + tam kopyalama; olcumun kendisi de bir yuruyustur.",
        ["CalculateSizeAsync"] = "Her cagri tam hizda 15 saniyeye kadar agac yuruyusu; en ucuz saldiri yoluydu.",
        ["SearchAsync"] = "5.000 dosyaya kadar gezer ve her uygun dosyanin 2 MB'a kadarini ICERIK olarak okur."
    };

    /// <summary>Ağır uçların HEPSİ eşzamanlılık kapısından geçer.</summary>
    /// <remarks>
    /// MUTASYON (ölçüldü): herhangi bir uçtan <c>AgirIseGirisAsync</c> satırı silindiğinde test o
    /// ucun adıyla KIRMIZI yandı.
    /// </remarks>
    [Fact]
    public void Agir_uclarin_hepsi_esZamanlilik_kapisindan_geciyor()
    {
        var kaynak = DenetleyiciKaynagi();

        var kapsanmayan = AgirUclar.Keys
            .Where(uc => !MetotGovdesi(kaynak, uc).Contains("AgirIseGirisAsync(", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            kapsanmayan.Length == 0,
            "Esszamanlilik kapisindan GECMEYEN agir uclar var; tek kiraci sunucuyu tuketebilir:\n  "
            + string.Join("\n  ", kapsanmayan.Select(uc => $"{uc} -- {AgirUclar[uc]}")));
    }

    /// <summary>
    /// Liste ÇÜRÜMEZ: adı değişen ya da silinen bir uç, listeyi sessizce anlamsız bırakamaz.
    /// </summary>
    [Fact]
    public void Agir_uc_listesi_curumus_girdi_icermez()
    {
        var kaynak = DenetleyiciKaynagi();

        var olmayan = AgirUclar.Keys
            .Where(uc => !kaynak.Contains($" {uc}(", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            olmayan.Length == 0,
            "Agir uc listesinde artik var olmayan metotlar var: " + string.Join(", ", olmayan));
    }

    private static string DenetleyiciKaynagi()
    {
        var yol = Path.Combine(BulKok(), "src", "AdaPanel.Api", "Controllers", "FilesController.cs");
        Assert.True(File.Exists(yol), $"FilesController bulunamadi: {yol}");
        return File.ReadAllText(yol);
    }

    /// <summary>
    /// Metodun gövdesini kabaca ayırır: imzadan, SONRAKİ üye başlangıcına kadar.
    /// </summary>
    /// <remarks>
    /// Bütün dosyada arama yapmak yanlış pozitif verirdi — tek bir uçta çağrı bulunması bütün
    /// uçları "kapsanmış" gösterirdi. Sınır olarak 4 boşluk girintili sonraki üye başlangıcı
    /// (<c>///</c>, öznitelik ya da <c>private</c>) kullanılıyor; bu denetleyicideki tüm üyeler
    /// bu biçimde yazılı.
    /// </remarks>
    private static string MetotGovdesi(string kaynak, string metotAdi)
    {
        var baslangic = kaynak.IndexOf($" {metotAdi}(", StringComparison.Ordinal);
        Assert.True(baslangic > 0, $"Metot bulunamadi: {metotAdi}");

        var sonraki = Regex.Match(kaynak[baslangic..], @"\n    (///|\[|private )");
        return sonraki.Success ? kaynak.Substring(baslangic, sonraki.Index) : kaynak[baslangic..];
    }

    private static string BulKok()
    {
        var dizin = new DirectoryInfo(AppContext.BaseDirectory);
        while (dizin is not null && !File.Exists(Path.Combine(dizin.FullName, "AdaPanel.sln")))
        {
            dizin = dizin.Parent;
        }

        Assert.NotNull(dizin);
        return dizin!.FullName;
    }
}
