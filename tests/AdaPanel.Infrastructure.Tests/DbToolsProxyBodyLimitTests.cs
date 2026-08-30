using AdaPanel.Api.Controllers;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// DB araci vekilinin yanit govdesini bellege alma siniri.
/// </summary>
/// <remarks>
/// <para>
/// <b>KAPATILAN ACIK (olculdu):</b> vekil, layout duzeltmesini enjekte edebilmek icin HTML
/// yanitlari bellege aliyordu ve kosul soyleydi: <c>contentLength is null or &lt;= 16 MiB</c>.
/// Yani <c>Content-Length</c> GONDERMEYEN (chunked) bir yanit <c>null</c> dalina duser ve
/// <c>ReadAsByteArrayAsync</c> ile SINIRSIZ bellege alinirdi. Uc <c>ResponseHeadersRead</c> ile
/// okudugu icin govde o ana kadar hic tuketilmemis olur; yani 16 MiB'lik sinir tam da onu asan
/// yanitlarda hic calismiyordu.
/// </para>
/// <para>
/// <b>NEDEN "uzunlugu bilinmeyeni hic tamponlama" DEGIL:</b> phpMyAdmin sayfalarinin cogu chunked
/// gelir; o cozum layout duzeltmesini tumuyle oldururdu. Dogru cevap uzunluga guvenmek degil,
/// OKURKEN SAYMAKTIR.
/// </para>
/// </remarks>
public sealed class DbToolsProxyBodyLimitTests
{
    /// <summary>
    /// UZUNLUGU BILINMEYEN govde de sayilir: tavani asinca tamponlama BIRAKILIR ve govde akitilir.
    /// </summary>
    /// <remarks>
    /// MUTASYON (olculdu): <c>BufferForInjectionAsync</c> govdeyi kosulsuz sonuna kadar okuyacak
    /// sekilde degistirildiginde bu test KIRMIZI yandi (null yerine 5.000 baytlik tampon dondu).
    /// </remarks>
    [Fact]
    public async Task Uzunlugu_bilinmeyen_buyuk_govde_TAMPONLANMAZ_ama_KAYBOLMAZ()
    {
        var govde = new byte[5000];
        Array.Fill(govde, (byte)'A');

        using var kaynak = new UzunluguBilinmeyenAkis(govde);
        using var cikti = new MemoryStream();

        var tampon = await DbToolsProxyController.BufferForInjectionAsync(kaynak, cikti, 1024, CancellationToken.None);

        // Tavan asildi: enjeksiyondan vazgecildi.
        Assert.Null(tampon);

        // GOVDE TAM olarak istemciye gitti: koruma, yanitin bir kismini yutmak DEGILDIR.
        Assert.Equal(govde.Length, cikti.Length);
        Assert.Equal(govde, cikti.ToArray());
    }

    /// <summary>Tavana SIGAN govde tamponlanir (layout enjeksiyonu calismaya devam eder).</summary>
    /// <remarks>
    /// Bu, korumanin asiri engelleme YAPMADIGININ nobetcisi: her govdeyi akitan bir "duzeltme"
    /// phpMyAdmin'in duzen sorununu geri getirirdi.
    /// </remarks>
    [Fact]
    public async Task Tavana_sigan_govde_TAMPONLANIR_ve_ciktiya_yazilmaz()
    {
        var govde = System.Text.Encoding.UTF8.GetBytes("<html><head></head><body>ok</body></html>");

        using var kaynak = new UzunluguBilinmeyenAkis(govde);
        using var cikti = new MemoryStream();

        var tampon = await DbToolsProxyController.BufferForInjectionAsync(kaynak, cikti, 1024, CancellationToken.None);

        Assert.NotNull(tampon);
        Assert.Equal(govde, tampon!);

        // Cagiran enjekte edip KENDI yazacak; yardimci ciktiya hicbir sey yazmamali (yoksa govde
        // iki kez giderdi).
        Assert.Equal(0, cikti.Length);
    }

    /// <summary>Tavanin TAM UZERINDEKI govde de asmis sayilir (sinir kapali araliktir).</summary>
    [Fact]
    public async Task Tavanin_bir_bayt_ustu_asmis_sayilir()
    {
        using var kaynak = new UzunluguBilinmeyenAkis(new byte[1025]);
        using var cikti = new MemoryStream();

        Assert.Null(await DbToolsProxyController.BufferForInjectionAsync(kaynak, cikti, 1024, CancellationToken.None));
        Assert.Equal(1025, cikti.Length);
    }

    /// <summary>
    /// UZUNLUGU BILINMEYEN yanit HALA tamponlama adayidir; degisen sey sinirin gercekten
    /// uygulanmasidir.
    /// </summary>
    [Fact]
    public void Uzunlugu_bilinmeyen_html_hala_enjeksiyon_adayidir()
    {
        Assert.True(DbToolsProxyController.ShouldBufferForInjection(isHtml: true, isHead: false, contentLength: null));
    }

    /// <summary>HTML olmayan, HEAD olan ve BILINEN sekilde cok buyuk yanitlar hic tamponlanmaz.</summary>
    [Theory]
    [InlineData(false, false, null)]                 // HTML degil (ornegin SQL disa aktarim indirmesi)
    [InlineData(true, true, null)]                   // HEAD: govde yok
    [InlineData(true, false, 17L * 1024 * 1024)]     // Bilinen uzunluk tavani asiyor
    public void Tamponlanmamasi_gerekenler(bool isHtml, bool isHead, long? contentLength)
    {
        Assert.False(DbToolsProxyController.ShouldBufferForInjection(isHtml, isHead, contentLength));
    }

    /// <summary>
    /// <c>Content-Length</c> BILDIRMEYEN akis: gercek chunked yanitin davranisi.
    /// </summary>
    /// <remarks>
    /// <c>MemoryStream</c> uzunlugunu bilir; onunla olcmek, tam da kacan durumu (uzunlugun
    /// bilinmedigi durumu) hic ölçmemek olurdu.
    /// </remarks>
    private sealed class UzunluguBilinmeyenAkis(byte[] veri) : Stream
    {
        private int konum;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            // Kucuk parcalar halinde verir: gercek ag akisi da tek seferde bitmez.
            var alinacak = Math.Min(Math.Min(count, 256), veri.Length - konum);
            if (alinacak <= 0)
            {
                return 0;
            }

            Array.Copy(veri, konum, buffer, offset, alinacak);
            konum += alinacak;
            return alinacak;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
