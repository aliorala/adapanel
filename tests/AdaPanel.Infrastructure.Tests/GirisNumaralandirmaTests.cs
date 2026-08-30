using AdaPanel.Core.Dtos.Auth;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// KULLANICI NUMARALANDIRMA: giriş ekranı "bu hesap var mı" sorusunu CEVAPLAMAZ.
/// </summary>
/// <remarks>
/// <para>
/// <b>Kapatılan iki kapı:</b>
/// </para>
/// <list type="number">
/// <item><b>Pasif hesap mesajı paroladan ÖNCE dönüyordu.</b> Herhangi biri yalnız bir kullanıcı adı
/// göndererek <c>auth.user_inactive</c> alıyor ve hesabın VAR OLDUĞUNU öğreniyordu — hiçbir parola
/// bilmeden. Denetim artık parola doğrulandıktan SONRA yapılıyor: mesajı yalnız hesabın sahibi
/// görür.</item>
/// <item><b>Kilit mesajı.</b> <c>auth.user_locked</c> yalnız var olan bir hesap için üretilebilirdi;
/// beş kez yanlış deneyip altıncıda mesajın değişmesine bakan biri hesabın varlığını
/// doğrulayabiliyordu. Artık yanlış parolada her zaman aynı yanıt dönüyor.</item>
/// </list>
/// <para>
/// <b>Üçüncüsü ZAMAN farkıydı:</b> kullanıcı yokken hiç karma hesaplanmıyor, varken PBKDF2
/// çalışıyordu (on binlerce yineleme). Aradaki fark ağ üzerinden ölçülebilir, yani panel soruyu
/// yanıt SÜRESİYLE cevaplıyordu. Aşağıda kronometre değil, YAPILAN İŞ ölçülüyor: iki yol da bir
/// karma doğrular. Kronometre makineye bağlı ve kırılgan olurdu; sayaç belirleyicidir.
/// </para>
/// </remarks>
public sealed class GirisNumaralandirmaTests
{
    [Fact]
    public async Task Olmayan_kullanici_ile_yanlis_parola_AYNI_yaniti_verir()
    {
        await using var duzenek = await GirisDuzenegi.OlusturAsync();

        var olmayan = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            duzenek.Servis.LoginAsync(Istek("boyle-bir-hesap-yok", "herhangi-bir-parola"), "203.0.113.10"));
        var yanlisParola = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            duzenek.Servis.LoginAsync(Istek(duzenek.Kullanici.UserName, "yanlis-parola"), "203.0.113.10"));

        Assert.Equal("auth.invalid_credentials", olmayan.Message);
        Assert.Equal("auth.invalid_credentials", yanlisParola.Message);
    }

    /// <summary>
    /// OLMAYAN KULLANICI YOLUNDA DA PAROLA KARMASI DOĞRULANIR — zaman farkının kapatılması.
    /// Sayaç 0'a düşerse, ölçülebilir zaman farkı geri gelmiş demektir.
    /// </summary>
    [Fact]
    public async Task Olmayan_kullanici_yolunda_da_KARMA_dogrulanir()
    {
        await using var duzenek = await GirisDuzenegi.OlusturAsync();
        var basta = duzenek.Karmalayici.DogrulamaSayisi;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            duzenek.Servis.LoginAsync(Istek("boyle-bir-hesap-yok", "herhangi-bir-parola"), "203.0.113.10"));

        Assert.Equal(basta + 1, duzenek.Karmalayici.DogrulamaSayisi);
    }

    /// <summary>Var olan kullanıcı yolu da TEK doğrulama yapar: iki yol aynı işi yapmalı.</summary>
    [Fact]
    public async Task Var_olan_kullanici_yolu_da_TEK_karma_dogrular()
    {
        await using var duzenek = await GirisDuzenegi.OlusturAsync();
        var basta = duzenek.Karmalayici.DogrulamaSayisi;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            duzenek.Servis.LoginAsync(Istek(duzenek.Kullanici.UserName, "yanlis-parola"), "203.0.113.10"));

        Assert.Equal(basta + 1, duzenek.Karmalayici.DogrulamaSayisi);
    }

    /// <summary>
    /// PASİF HESAP, PAROLAYI BİLMEYENE ELE VERİLMEZ: yanıt sıradan "geçersiz kimlik bilgisi"dir.
    /// </summary>
    [Fact]
    public async Task Pasif_hesap_YANLIS_parolayla_ele_verilmez()
    {
        await using var duzenek = await GirisDuzenegi.OlusturAsync(aktif: false);

        var hata = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            duzenek.Servis.LoginAsync(Istek(duzenek.Kullanici.UserName, "yanlis-parola"), "203.0.113.10"));

        Assert.Equal("auth.invalid_credentials", hata.Message);
        Assert.DoesNotContain("auth.login.blocked", duzenek.Denetim.Actions);
    }

    /// <summary>
    /// FAZLA KAPATMA NÖBETÇİSİ: hesabın SAHİBİ (parolayı doğru bilen) hesabının kapalı olduğunu
    /// HÂLÂ görür. Aksi hâlde kapatılan müşteri, sebebini anlamadan parola sıfırlamayla uğraşırdı.
    /// </summary>
    [Fact]
    public async Task Pasif_hesap_DOGRU_parolayla_acikca_soylenir()
    {
        await using var duzenek = await GirisDuzenegi.OlusturAsync(aktif: false);

        var hata = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            duzenek.Servis.LoginAsync(Istek(duzenek.Kullanici.UserName, GirisDuzenegi.DogruParola), "203.0.113.10"));

        Assert.Equal("auth.user_inactive", hata.Message);
        Assert.Contains("auth.login.blocked", duzenek.Denetim.Actions);
    }

    /// <summary>
    /// KİLİTLİ HESAP DA ELE VERİLMEZ: kilitliyken yanlış parola, kilitli olmayan bir hesaptan
    /// AYIRT EDİLEMEYEN bir yanıt döner.
    /// </summary>
    [Fact]
    public async Task Kilitli_hesap_yanlis_parolada_ELE_VERILMEZ()
    {
        await using var kilitli = await GirisDuzenegi.OlusturAsync(kilitDakika: 15);
        await using var kilitsiz = await GirisDuzenegi.OlusturAsync();

        var kilitliHata = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            kilitli.Servis.LoginAsync(Istek(kilitli.Kullanici.UserName, "yanlis-parola"), "203.0.113.10"));
        var kilitsizHata = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            kilitsiz.Servis.LoginAsync(Istek(kilitsiz.Kullanici.UserName, "yanlis-parola"), "203.0.113.10"));

        Assert.Equal(kilitsizHata.Message, kilitliHata.Message);
    }

    /// <summary>
    /// FAZLA KAPATMA NÖBETÇİSİ: bütün bu sessizliğe rağmen MEŞRU GİRİŞ çalışmaya devam ediyor.
    /// </summary>
    [Fact]
    public async Task Dogru_kimlik_bilgisiyle_giris_HALA_calisir()
    {
        await using var duzenek = await GirisDuzenegi.OlusturAsync();

        var sonuc = await duzenek.Servis.LoginAsync(
            Istek(duzenek.Kullanici.UserName, GirisDuzenegi.DogruParola), "203.0.113.10");

        Assert.True(sonuc.IsAuthenticated);
        Assert.Equal(duzenek.Kullanici.UserName, sonuc.User?.UserName);
    }

    private static LoginRequest Istek(string ad, string parola) => new()
    {
        UserNameOrEmail = ad,
        Password = parola
    };
}
