using System.Text;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// <see cref="FileTail"/> — dönmeyen günlüklerin son birkaç KB'sini, dosyanın tamamını belleğe
/// almadan okuma.
/// </summary>
/// <remarks>
/// Kapatılan açık: iki yer (<c>NginxInstallationSupport.ReadErrorLog</c>,
/// <c>WebmailService</c> Roundcube günlüğü) <c>File.ReadAllText</c> ile TÜM dosyayı okuyup son
/// 4 KB / 1,5 KB'sini alıyordu. Bu günlükler dönmez; 1,5 KB göstermek için gigabaytlar okunabilir.
/// </remarks>
public sealed class FileTailTests : IDisposable
{
    private readonly string klasor = Path.Combine(Path.GetTempPath(), "adapanel-filetail-" + Guid.NewGuid().ToString("N"));

    public FileTailTests() => Directory.CreateDirectory(klasor);

    public void Dispose()
    {
        // Yalnız BU testin kendi oluşturduğu geçici klasör; başka hiçbir şeye dokunulmaz.
        try { Directory.Delete(klasor, recursive: true); } catch { /* temizlik zorunlu değil */ }
    }

    private string Yaz(string ad, string icerik)
    {
        var yol = Path.Combine(klasor, ad);
        File.WriteAllText(yol, icerik, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return yol;
    }

    [Fact]
    public void Son_baytlari_dondurur_ve_bastan_kirpar()
    {
        var yol = Yaz("buyuk.log", new string('a', 5000) + "SON-PARCA");

        var kuyruk = FileTail.ReadLastBytes(yol, 100);

        Assert.EndsWith("SON-PARCA", kuyruk, StringComparison.Ordinal);
        Assert.Equal(100, Encoding.UTF8.GetByteCount(kuyruk));
    }

    [Fact]
    public void Dosya_sinirdan_kucukse_tamami_doner()
    {
        var yol = Yaz("kucuk.log", "kisa icerik");

        Assert.Equal("kisa icerik", FileTail.ReadLastBytes(yol, 4000));
    }

    /// <summary>
    /// ASIL KAZANÇ: günlüğü YAZAN süreç dosyayı açık tutarken de okunabilmeli.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>File.ReadAllText</c> dosyayı <c>FileShare.Read</c> ile açar: "başkaları okuyabilir" der
    /// ama YAZMASINA izin vermez. nginx/PHP günlüğü yazmak için açık tuttuğunda bu istek Windows
    /// paylaşım kurallarına takılır ve <c>IOException</c> atar.
    /// </para>
    /// <para>
    /// Bu test önce ESKİ yolun gerçekten düştüğünü ölçer — yoksa "düzeltme" var olmayan bir
    /// sorunu çözüyor olurdu ve testi de hiçbir şeyi kanıtlamazdı.
    /// </para>
    /// </remarks>
    [Fact]
    public void Gunlugu_yazan_surec_dosyayi_acik_tutarken_de_okunur()
    {
        var yol = Yaz("kilitli.log", "nginx: baglanti reddedildi");

        // Günlüğü yazan sürecin tuttuğu tutamak: yazmak için açık, başkalarına yalnız okuma izni.
        using var yazan = new FileStream(yol, FileMode.Open, FileAccess.Write, FileShare.Read);

        Assert.Throws<IOException>(() => File.ReadAllText(yol));
        Assert.Equal("nginx: baglanti reddedildi", FileTail.ReadLastBytes(yol, 4000));
    }

    /// <summary>
    /// Dosyanın ORTASINDAN başlandığı için ilk bayt bir karakterin ortasına denk gelebilir.
    /// </summary>
    [Fact]
    public void Yarim_kalan_utf8_karakteri_metnin_basina_bozuk_isaret_koymaz()
    {
        // "ağz" = 61 | C4 9F | 7A  — yani 4 bayt, ortadaki 'ğ' iki baytlı.
        // maxBytes=2 son iki baytı ister: [9F, 7A]. 9F bir DEVAM baytıdır (10xxxxxx), tek başına
        // çözülürse metnin başına "�" koyar. Doğru davranış onu atıp "z" dönmektir.
        //
        // İLK YAZIMIM YANLIŞTI ve testi koşturmak yakaladı: "ağ" içeriğiyle son 2 baytı istemiştim,
        // ama o 2 bayt 'ğ'in TAMAMI -- yani sınır hiç ortaya denk gelmiyordu. Kesişmeyi ölçtüğünü
        // sanan, aslında hiçbir şey ölçmeyen bir testti. Şimdi kesikten SONRA gerçek içerik var,
        // böylece "boş döndü" ile "temiz kırptı" birbirine karışmıyor.
        var yol = Yaz("turkce.log", "ağz");

        var kuyruk = FileTail.ReadLastBytes(yol, 2);

        Assert.DoesNotContain('�', kuyruk);
        Assert.Equal("z", kuyruk);
    }

    [Fact]
    public void Tam_sinirdan_baslarsa_karakter_korunur()
    {
        var yol = Yaz("turkce2.log", "ağz");

        // 3 bayt = [C4 9F 7A] = 'ğ' + 'z'; sınır geçerli bir karakter BAŞLANGICINA denk geliyor,
        // dolayısıyla hiçbir şey atılmamalı. Bir önceki testle çifti tamamlar: biri kırpmayı,
        // bu da KIRPMAMAYI ölçer -- yoksa "her zaman baştan bir şey at" da testi geçerdi.
        Assert.Equal("ğz", FileTail.ReadLastBytes(yol, 3));
    }

    [Fact]
    public void Olmayan_bos_ve_gecersiz_girdiler_bos_dize_doner()
    {
        Assert.Equal(string.Empty, FileTail.ReadLastBytes(Path.Combine(klasor, "yok.log"), 4000));
        Assert.Equal(string.Empty, FileTail.ReadLastBytes(Yaz("bos.log", string.Empty), 4000));
        Assert.Equal(string.Empty, FileTail.ReadLastBytes(Yaz("var.log", "icerik"), 0));
        Assert.Equal(string.Empty, FileTail.ReadLastBytes("   ", 4000));
    }

    /// <summary>
    /// Teşhis okuması ÇAĞIRANIN hatasının yerine geçmemeli.
    /// </summary>
    /// <remarks>
    /// Nginx yolunda çağrı <c>throw new AppException("nginx.start_failed", …, ReadErrorLog(…))</c>
    /// ifadesinin İÇİNDEdir ve etrafında try/catch yoktur. Okuma fırlatsaydı dosya hatası, anlamlı
    /// "nginx başlatılamadı" hatasının YERİNE geçerdi — teşhis tam da teşhise ihtiyaç duyulan anda
    /// kaybolurdu. Bir dizin yolu vererek okumayı bilerek imkânsız kılıp bunu ölçüyoruz.
    /// </remarks>
    [Fact]
    public void Okuma_imkansizsa_firlatmaz()
    {
        var hata = Record.Exception(() => FileTail.ReadLastBytes(klasor, 4000));

        Assert.Null(hata);
    }

    /// <summary>
    /// Çağrı yerleri gerçekten bu kapıyı kullanıyor mu — "yardımcı yazıldı ama bağlanmadı" olmasın.
    /// </summary>
    /// <remarks>
    /// Bu depoda "koruma yazılmış ama o yolda uygulanmıyor" tekrar eden bir hata sınıfıdır. Yasak,
    /// kelimeye değil ŞEKLE konur: iki dosyada da <c>File.ReadAllText</c> ile alınıp sondan
    /// kırpılan bir günlük KALMAMALI.
    /// </remarks>
    [Theory]
    [InlineData("src", "AdaPanel.Infrastructure", "Nginx", "NginxInstallationSupport.cs")]
    [InlineData("src", "AdaPanel.Infrastructure", "Mail", "WebmailService.cs")]
    public void Cagri_yerleri_dosyanin_tamamini_okumuyor(params string[] parcalar)
    {
        var kaynak = File.ReadAllText(Path.Combine(KaynakKoku(), Path.Combine(parcalar)));

        Assert.Contains("FileTail.ReadLastBytes(", kaynak, StringComparison.Ordinal);

        // Sondan kırpma yazımı (`[^1500..]`) yalnız tamamı okunmuşsa gerekir; kalmışsa eski yol duruyor.
        Assert.DoesNotContain("[^4000..]", kaynak, StringComparison.Ordinal);
        Assert.DoesNotContain("[^1500..]", kaynak, StringComparison.Ordinal);
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
