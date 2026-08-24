using System.Security.Cryptography;
using AdaPanel.Infrastructure.Htaccess;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// <see cref="HtaccessPortableApi"/>'nin her üyesi, YERİNE GEÇTİĞİ BCL üyesiyle aynı cevabı
/// vermek zorunda.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden bu testler var:</b> motorun kaynakları iki hedefte derleniyor (panel <c>net8.0-windows</c>,
/// IIS modülü <c>net48</c>) ve bu sınıf, .NET (Core) tarafında var olup net48'de olmayan üyelerin
/// karşılığıdır. "Aynı çalışır" cümlesi ölçülmeden yazılırsa, IIS'te sessizce farklı davranan bir
/// motor elde edilirdi — <c>.htaccess</c> desteğinde bu, parolanın kabul edilmemesi ya da bir
/// kuralın eşleşmemesi demektir.
/// </para>
/// <para>
/// Bu testler <b>net8'de</b> koşuyor ve tam da bu yüzden anlamlılar: BCL aslı burada MEVCUT,
/// dolayısıyla karşılaştırma YAPILABİLİYOR. net48'de asıl yok, karşılaştırma da olamazdı.
/// </para>
/// </remarks>
public sealed class HtaccessPortableApiTests
{
    /// <summary>
    /// ASCII rakam kararı, BCL ile bütün <c>char</c> uzayında AYNI.
    /// </summary>
    /// <remarks>
    /// Örnekleme YAPILMIYOR, 65.536 kod noktasının hepsi geziliyor: asıl tehlike düşünülen
    /// karakterlerde değil, düşünülmeyenlerde. Özellikle <c>char.IsDigit</c>'in kabul ettiği
    /// ASCII DIŞI ondalık rakamlar (Arap-Hint ٣, Devanagari ३, tam genişlikli ３) burada
    /// reddedilmeli — kabul edilseydi <c>- '0'</c> aritmetiği çöp değer üretirdi.
    /// </remarks>
    [Fact]
    public void ASCII_RAKAM_karari_BUTUN_KARAKTERLERDE_BCL_ile_AYNI()
    {
        for (var kod = 0; kod <= char.MaxValue; kod++)
        {
            var karakter = (char)kod;
            Assert.Equal(char.IsAsciiDigit(karakter), HtaccessPortableApi.AsciiRakamMi(karakter));
        }
    }

    [Fact]
    public void ASCII_HARF_VEYA_RAKAM_karari_BUTUN_KARAKTERLERDE_BCL_ile_AYNI()
    {
        for (var kod = 0; kod <= char.MaxValue; kod++)
        {
            var karakter = (char)kod;
            Assert.Equal(
                char.IsAsciiLetterOrDigit(karakter),
                HtaccessPortableApi.AsciiHarfVeyaRakamMi(karakter));
        }
    }

    /// <summary>
    /// Kendi karşılığımızın <c>char.IsDigit</c>'ten AYRILDIĞI de ölçülüyor.
    /// </summary>
    /// <remarks>
    /// Üstteki test, karşılığın BCL ile aynı olduğunu söyler ama "hangi BCL üyesi" sorusunu
    /// gözden kaçırmayı mümkün kılar. Biri bir gün bunu <c>char.IsDigit</c>'e çevirirse üstteki
    /// test kırılmayabilir gibi görünür; bu test o kapıyı kapatır ve NEDEN'i kayda geçirir.
    /// </remarks>
    [Theory]
    [InlineData('٣')] // Arap-Hint rakam uc
    [InlineData('३')] // Devanagari rakam uc
    [InlineData('３')] // Tam genislikli 3
    public void ASCII_DISI_ONDALIK_RAKAMLAR_RAKAM_SAYILMAZ(char karakter)
    {
        Assert.True(char.IsDigit(karakter), "Test verisi hatali: bu karakter Unicode ondalik rakam olmali.");
        Assert.False(HtaccessPortableApi.AsciiRakamMi(karakter));
    }

    /// <summary>SHA-1 karşılığı, BCL'in static üyesiyle bayt bayt AYNI özet üretir.</summary>
    /// <remarks>
    /// Boş girdi ve blok sınırı (55/56/64 bayt — SHA-1'in dolgu davranışının değiştiği yer) ayrıca
    /// deneniyor; yanlış yazılmış bir sarmalayıcı en çok orada ayrılır.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(55)]
    [InlineData(56)]
    [InlineData(64)]
    [InlineData(1000)]
    public void SHA1_karsiligi_BCL_ile_AYNI_OZETI_URETIR(int uzunluk)
    {
        var veri = new byte[uzunluk];
        for (var i = 0; i < uzunluk; i++)
        {
            veri[i] = (byte)(i * 31 % 251);
        }

        Assert.Equal(SHA1.HashData(veri), HtaccessPortableApi.Sha1(veri));
    }

    /// <summary>
    /// Sabit süreli karşılaştırma, <c>CryptographicOperations.FixedTimeEquals</c> ile AYNI kararı
    /// verir.
    /// </summary>
    /// <remarks>
    /// Ölçülen şey SÜRE değil KARAR — süreyi birim testinde güvenilir ölçmek mümkün değil (JIT,
    /// zamanlayıcı, güç durumu). Sabit sürelilik kodun ŞEKLİNDEN gelir: erken çıkış yok, döngü
    /// her zaman bütün baytları gezer. Buradaki iş, o şeklin doğru cevabı bozmadığını göstermek.
    /// </remarks>
    [Fact]
    public void SABIT_SUREDE_ESITLIK_BCL_ile_AYNI_KARARI_VERIR()
    {
        byte[][] ornekler =
        [
            [],
            [0],
            [1],
            [1, 2, 3],
            [1, 2, 4],
            [1, 2, 3, 4],
            [255, 0, 255]
        ];

        foreach (var sol in ornekler)
        {
            foreach (var sag in ornekler)
            {
                Assert.Equal(
                    CryptographicOperations.FixedTimeEquals(sol, sag),
                    HtaccessPortableApi.SabitSuredeEsit(sol, sag));
            }
        }
    }

    /// <summary>
    /// <c>null</c> girdi FIRLATMAZ, <c>false</c> döner.
    /// </summary>
    /// <remarks>
    /// BCL aslı <c>ReadOnlySpan</c> aldığı için null diye bir durumu yoktur; bizim karşılığımız
    /// dizi alıyor ve bu farkı kapatmak zorunda. Parola doğrulama yolunda bir
    /// <see cref="NullReferenceException"/>, motorun "istisna fırlatmaz" sözleşmesini kırardı.
    /// </remarks>
    [Fact]
    public void SABIT_SUREDE_ESITLIK_NULL_GIRDIDE_FIRLATMAZ()
    {
        Assert.False(HtaccessPortableApi.SabitSuredeEsit(null, [1]));
        Assert.False(HtaccessPortableApi.SabitSuredeEsit([1], null));
        Assert.False(HtaccessPortableApi.SabitSuredeEsit(null, null));
    }

    /// <summary>Sıfırlama, <c>Array.Clear(dizi)</c> ile aynı: TAMAMI sıfırlanır.</summary>
    /// <remarks>
    /// Yarım sıfırlanan bir tampon, hiç sıfırlanmayandan daha tehlikelidir: kod "temizledim"
    /// sanır, bellek dökümünde parolanın kuyruğu durur.
    /// </remarks>
    [Fact]
    public void SIFIRLAMA_DIZININ_TAMAMINI_temizler()
    {
        var dizi = new byte[] { 1, 2, 3, 4, 5 };
        HtaccessPortableApi.Sifirla(dizi);

        Assert.All(dizi, b => Assert.Equal(0, b));

        // null FIRLATMAMALI: cagrildigi yerlerde "belki hic ayrilmadi" durumu var.
        HtaccessPortableApi.Sifirla(null);
    }
}
