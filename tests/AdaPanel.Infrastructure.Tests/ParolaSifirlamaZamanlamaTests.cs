using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// <c>password/forgot</c> YANIT YOLUNUN DALSIZLIĞI.
/// </summary>
/// <remarks>
/// <para>
/// <b>ÖLÇÜLEN AÇIK.</b> Uç, yanıt gövdesi olarak her zaman aynı şeyi döndürüyordu ama YAPTIĞI İŞ
/// adresin panelde kayıtlı olup olmamasına göre değişiyordu: kayıtlıysa SMTP ayarı okunuyor,
/// belirteç üretiliyor, GERÇEK bir SMTP bağlantısı açılıyor ve denetim kaydı yazılıyordu; değilse
/// hiçbiri. Bu makinede ölçülen SMTP maliyeti kapalı bir porta bile 2066 ms, yanıt vermeyen bir
/// adrese 10 sn'de hâlâ bekliyordu (MailKit varsayılan zaman aşımı 120000 ms); aynı yoldaki dosya
/// işleri ise ortanca 0,72 ms ve 0,17 ms. Yani fark saniyeler mertebesindeydi ve ağ üzerinden
/// ölçülebiliyordu — panel, hiçbir parola bilmeyen birine "bu adres kayıtlı mı" sorusunu YANIT
/// SÜRESİYLE cevaplıyordu.
/// </para>
/// <para>
/// <b>Neden kronometreli test YOK.</b> Duvar saatine bakan bir test CI'da kararsız olur ve yarın
/// sebepsiz kırmızıya döner. Ölçülen şey DAVRANIŞTIR: yanıt yolu iki dalda da BİREBİR aynı işi mi
/// yapıyor. Kuyruk sahtesi işi çalıştırmadığı için, yanıt yolunun kendi başına bir şey yapıp
/// yapmadığı da görünür (denetim kaydı boş kalmalı).
/// </para>
/// </remarks>
public sealed class ParolaSifirlamaZamanlamaTests
{
    /// <summary>KAYITLI adres: iş kuyruğa verilir, yanıt yolunda YAPILMAZ.</summary>
    [Fact]
    public async Task Kayitli_adres_isi_KUYRUKLAR_yanit_yolunda_YAPMAZ()
    {
        await using var duzenek = await GirisDuzenegi.OlusturAsync();

        await duzenek.Servis.RequestPasswordResetAsync(duzenek.Kullanici.Email);

        Assert.Equal(["auth.password_reset"], duzenek.Kuyruk.Isler);
        // Denetim kaydı, posta gönderimi ve belirteç üretimi ARKA PLAN işinin içinde; yanıt yolu
        // hiçbirini yapmamalı. Yaparsa buradaki liste dolar.
        Assert.Empty(duzenek.Denetim.Actions);
    }

    /// <summary>
    /// KAYITSIZ adres: TAM OLARAK AYNI iş kuyruğa verilir. Bu testin kırmızıya dönmesi, yanıt
    /// yoluna "kullanıcı var mı" dalının geri geldiği anlamına gelir — yani zamanlama sızıntısının
    /// geri gelmesi.
    /// </summary>
    [Fact]
    public async Task Kayitsiz_adres_de_AYNI_isi_kuyruklar()
    {
        await using var duzenek = await GirisDuzenegi.OlusturAsync();

        await duzenek.Servis.RequestPasswordResetAsync("hicbir-zaman-kayitli-olmayan@ornek.test");

        Assert.Equal(["auth.password_reset"], duzenek.Kuyruk.Isler);
        Assert.Empty(duzenek.Denetim.Actions);
    }

    /// <summary>
    /// İki dalın işi AYNI OLMALI — testin asıl iddiası bu. Ayrı ayrı sayıları doğrulamak, birinin
    /// sessizce değişmesine izin verirdi.
    /// </summary>
    [Fact]
    public async Task Iki_dal_da_ayni_is_kumesini_calistirir()
    {
        await using var kayitli = await GirisDuzenegi.OlusturAsync();
        await kayitli.Servis.RequestPasswordResetAsync(kayitli.Kullanici.Email);

        await using var kayitsiz = await GirisDuzenegi.OlusturAsync();
        await kayitsiz.Servis.RequestPasswordResetAsync("yok@ornek.test");

        Assert.Equal(kayitli.Kuyruk.Isler, kayitsiz.Kuyruk.Isler);
        Assert.Equal(kayitli.Denetim.Actions, kayitsiz.Denetim.Actions);
        Assert.Equal(kayitli.Karmalayici.DogrulamaSayisi, kayitsiz.Karmalayici.DogrulamaSayisi);
    }

    /// <summary>
    /// PASİF hesabın adresi de kayıtsız adresle aynı yolu izler: pasiflik kararı arka planda
    /// verilir, yanıt süresine yansımaz.
    /// </summary>
    [Fact]
    public async Task Pasif_hesabin_adresi_de_AYNI_isi_kuyruklar()
    {
        await using var duzenek = await GirisDuzenegi.OlusturAsync(aktif: false);

        await duzenek.Servis.RequestPasswordResetAsync(duzenek.Kullanici.Email);

        Assert.Equal(["auth.password_reset"], duzenek.Kuyruk.Isler);
        Assert.Empty(duzenek.Denetim.Actions);
    }

    /// <summary>
    /// Adres HİÇ verilmemişse iş üretilmez. Sızacak bilgi yok (karşılaştırılacak ikinci bir dal da
    /// yok) ve boş istekle kuyruk doldurmanın anlamı olmazdı.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Bos_adres_is_uretmez(string adres)
    {
        await using var duzenek = await GirisDuzenegi.OlusturAsync();

        await duzenek.Servis.RequestPasswordResetAsync(adres);

        Assert.Empty(duzenek.Kuyruk.Isler);
    }

    /// <summary>
    /// Büyük harfli / boşluklu yazım da AYNI tek işi üretir: normalleştirme yanıt yolunda ve
    /// adresten bağımsız sabit maliyetli.
    /// </summary>
    [Fact]
    public async Task Adres_yazimi_farkli_olsa_da_tek_is_uretilir()
    {
        await using var duzenek = await GirisDuzenegi.OlusturAsync();

        await duzenek.Servis.RequestPasswordResetAsync($"  {duzenek.Kullanici.Email.ToUpperInvariant()}  ");

        Assert.Equal(["auth.password_reset"], duzenek.Kuyruk.Isler);
    }
}
