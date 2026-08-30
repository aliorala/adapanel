using System.Text.Json;
using System.Text.RegularExpressions;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Infrastructure.Bootstrap;
using AdaPanel.Infrastructure.Mail;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Posta tarafında ÖLÇÜLMEDEN söylenen üç cümlenin düzeltmesi: (1) autodiscover'ın ölü port
/// dağıtması, (2) her posta kutusuna sabit "Etkin" rozeti, (3) ayar dosyasının ezilmesi.
/// </summary>
public sealed class MailHonestMeasurementTests
{
    private static string MailServiceKaynagi()
        => File.ReadAllText(Path.Combine(
            ScriptTestHost.RepoRoot, "src", "AdaPanel.Infrastructure", "Mail", "MailService.cs"));

    // ------------------------------------------------------------------
    // 1) Autodiscover, DIŞARIDAN ERİŞİLEMEYEN portu istemciye vermez
    // ------------------------------------------------------------------

    /// <summary>
    /// Sertleştirmenin loopback'e indirdiği port, istemci ayarlarına GİRMEMELİ.
    /// </summary>
    /// <remarks>
    /// İki modül birbirine BAĞLANARAK ölçülüyor: bind kümesini gerçekten üreten
    /// <see cref="ComponentBootstrapper.ResolveStalwartListenerBinds"/> çağrılıyor, çıktısı
    /// olduğu gibi autodiscover'ın port ayıklayıcısına veriliyor. Testin iki tarafı da
    /// üretimdeki kod; arada elle yazılmış bir "beklenen bind listesi" yok, yoksa sertleştirme
    /// değişince test yanlış yerde yeşil kalırdı.
    /// </remarks>
    [Fact]
    public void LoopbackeIndirilenPop3_110_IstemciyeYayinlanmaz()
    {
        var sertlesmis = ComponentBootstrapper.ResolveStalwartListenerBinds(["[::]:110"]);

        Assert.Contains("127.0.0.1:110", sertlesmis);
        Assert.DoesNotContain(110, MailService.IstemciyeUlasilabilirPortlar(sertlesmis));
    }

    [Fact]
    public void KamuyaAcikPop3s_995_IstemciyeYayinlanir()
    {
        var sertlesmis = ComponentBootstrapper.ResolveStalwartListenerBinds(["[::]:995"]);

        Assert.Contains(995, MailService.IstemciyeUlasilabilirPortlar(sertlesmis));
    }

    [Theory]
    [InlineData("0.0.0.0:143", true)]
    [InlineData("[::]:993", true)]
    [InlineData("*:587", true)]
    [InlineData(":25", true)]
    [InlineData("192.0.2.10:25", true)]
    [InlineData("127.0.0.1:110", false)]
    [InlineData("[::1]:110", false)]
    [InlineData("127.0.0.5:4190", false)]
    [InlineData("localhost:8080", false)]
    public void BindAdresi_DisaridanErisilebilirMi(string bind, bool beklenen)
    {
        Assert.Equal(beklenen, MailService.DisaridanUlasilabilirBind(bind));
    }

    /// <summary>
    /// Ayrıştırılamayan adres GİZLENMEZ: bilinmeyeni saklamak, çalışan bir portu istemciden
    /// esirgemek olurdu. Yanılmanın iki yönü eşit değil.
    /// </summary>
    [Fact]
    public void AnlasilmayanAdres_ErisilebilirSayilir()
    {
        Assert.Contains(143, MailService.IstemciyeUlasilabilirPortlar(["bilinmeyen-arabirim:143"]));
    }

    [Fact]
    public void AyniDinleyicide_HemLoopbackHemJoker_VarsaPortYayinlanir()
    {
        Assert.Contains(25, MailService.IstemciyeUlasilabilirPortlar(["127.0.0.1:25", "0.0.0.0:25"]));
    }

    [Fact]
    public void TumBaglamalariLoopbackOlanDinleyici_HicPortVermez()
    {
        Assert.Empty(MailService.IstemciyeUlasilabilirPortlar(["127.0.0.1:4190", "[::1]:4190"]));
    }

    // ------------------------------------------------------------------
    // 2) Posta kutusu durumu: SORULUR, uydurulmaz
    // ------------------------------------------------------------------

    private static JsonElement Nesne(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void HesapDurumu_AlanYoksa_OlculemediDoner()
    {
        Assert.Null(MailService.OkunanEtkinlik(Nesne("""{"name":"ali@ornek.com"}""")));
    }

    [Theory]
    [InlineData("""{"isEnabled":true}""", true)]
    [InlineData("""{"isEnabled":false}""", false)]
    [InlineData("""{"active":false}""", false)]
    [InlineData("""{"enabled":"false"}""", false)]
    [InlineData("""{"enabled":"true"}""", true)]
    [InlineData("""{"isEnabled":1}""", true)]
    [InlineData("""{"isEnabled":0}""", false)]
    public void HesapDurumu_SunucununCevabiOkunur(string json, bool beklenen)
    {
        Assert.Equal(beklenen, MailService.OkunanEtkinlik(Nesne(json)));
    }

    /// <summary>Anlaşılmayan değer "etkin" SAYILMAZ; ölçülemedi denir.</summary>
    [Theory]
    [InlineData("""{"isEnabled":"belirsiz"}""")]
    [InlineData("""{"isEnabled":null}""")]
    [InlineData("""{"isEnabled":{"x":1}}""")]
    public void HesapDurumu_AnlasilmayanDeger_Uydurulmaz(string json)
    {
        Assert.Null(MailService.OkunanEtkinlik(Nesne(json)));
    }

    /// <summary>
    /// Sözleşme ÜÇ DEĞERLİ kalmalı: <c>bool</c>'a döndürülürse "ölçülemedi" cevabı yok olur ve
    /// panel yine her kutuya yeşil rozet takar.
    /// </summary>
    [Fact]
    public void HesapSozlesmesi_UcDegerliKalir()
    {
        var ozellik = typeof(MailAccountDto).GetProperty(nameof(MailAccountDto.Active));

        Assert.NotNull(ozellik);
        Assert.Equal(typeof(bool?), ozellik!.PropertyType);
    }

    /// <summary>
    /// Posta kutusu nesnesi kurulurken SABİT değer atanamaz.
    /// </summary>
    /// <remarks>
    /// Üstteki testler saf okuyucuyu doğruluyor ama okuyucunun ÇAĞRILDIĞINI göstermiyor:
    /// <c>Active = true</c> geri yazılsa hepsi yeşil kalırdı. Bu yüzden kurulum noktası ayrıca
    /// bekçileniyor.
    /// </remarks>
    [Fact]
    public void PostaKutusuKurulurken_SabitEtkinlikAtanmaz()
    {
        var kaynak = MailServiceKaynagi();
        var kalip = new Regex(
            @"new\s+MailAccountDto\s*\{[^}]*Active\s*=\s*(true|false)\s*[,}]",
            RegexOptions.Singleline);

        Assert.False(
            kalip.IsMatch(kaynak),
            "MailAccountDto kurulurken Active alanına sabit değer atanmış; durum sunucuya SORULMALI.");
    }

    // ------------------------------------------------------------------
    // 3) config/mail.json: oku-değiştir-yaz DIŞINDA yazan kalmaz
    // ------------------------------------------------------------------

    /// <summary>
    /// Ayar dosyasına yazan TEK bir yer olmalı.
    /// </summary>
    /// <remarks>
    /// Bulgu şuydu: yönetici parolasını yeniden girince <c>ConfigureAsync</c> sıfırdan bir
    /// <c>MailConfig</c> yazıyor ve <c>PublicBaseUrl</c>'ü siliyordu — yani gerçek abonelere giden
    /// iletilerdeki <c>List-Unsubscribe</c> başlığı sessizce kapanıyordu. Tek çağrıyı düzeltmek
    /// yetmez; alan eklendikçe aynı hata tekrar yazılabilir. Bu yüzden kısıt sayılıyor: yazma
    /// noktası birden fazlaysa test kırmızı yanar.
    /// </remarks>
    [Fact]
    public void AyarDosyasinaYazan_TekNoktaVardir()
    {
        var kaynak = MailServiceKaynagi();
        var yazmaSayisi = Regex.Matches(kaynak, @"fileStore\.WriteAsync\(\s*ConfigPath").Count;

        Assert.Equal(1, yazmaSayisi);
    }

    [Fact]
    public void AyarDosyasi_SifirdanNesneYazilarakEzilmez()
    {
        var kaynak = MailServiceKaynagi();

        Assert.DoesNotContain("WriteAsync(ConfigPath, new MailConfig", kaynak, StringComparison.Ordinal);
    }
}
