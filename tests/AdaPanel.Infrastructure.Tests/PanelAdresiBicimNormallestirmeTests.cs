using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Panel adresi BİÇİMSEL olarak doğrulanıyor mu — yöneticinin makul girdisi geçerli bir URL
/// üretiyor mu?
/// </summary>
/// <remarks>
/// <para>
/// <b>ÖLÇÜLEN ARIZA (düzeltmeden önce alınan gerçek çıktılar, tahmin değil):</b>
/// <c>PanelAddress.BuildBaseUrl</c> ayar değerini çiğ olarak <c>https://</c> ile port arasına
/// yapıştırıyordu. Sonuçlar:
/// </para>
/// <list type="bullet">
///   <item><c>"panel.ornek.com:8890"</c> → <c>https://panel.ornek.com:8890:8888</c> (İKİ PORT)</item>
///   <item><c>"https://panel.ornek.com"</c> → <c>https://https://panel.ornek.com:8888</c> (İKİ ŞEMA)</item>
///   <item><c>"http://panel.ornek.com:8890"</c> → <c>https://http://panel.ornek.com:8890:8888</c></item>
///   <item><c>"fd00::1"</c> → <c>https://fd00::1:8888</c> (IPv6 KÖŞELİ PARANTEZSİZ — geçersiz)</item>
///   <item><c>"panel.ornek.com/"</c> → <c>https://panel.ornek.com/:8888</c> (port YOLUN ardında)</item>
/// </list>
/// <para>
/// Bu URL'ler müşteriye giden postalarda taşınıyor (parola sıfırlama, hoş geldin, abonelikten
/// çıkma onayı, tek kullanımlık giriş). Hiçbiri açılmaz; panel "gönderildi" der. Yönetici hiçbir
/// uyarı almaz — çünkü girdisi "makul"dür: panele zaten o adresten girer.
/// </para>
/// <para>
/// <b>Düzeltme İKİ KATMANDA:</b> kaydetme yolu (<c>PanelPreferencesService.UpdateAsync</c>) yeni
/// girdiyi kanonikleştirir ve yöneticiye anında geri bildirim verir; üretme yolu
/// (<c>BuildBaseUrl</c>) diskte ZATEN duran bozuk değeri de kurtarır. Tek katman yetmez: kaydetme
/// yalnız yeni girdileri, üretme yalnız bağlantıları düzeltir — ama bu alanı DNS, FTP ve posta
/// da okuyor.
/// </para>
/// </remarks>
public sealed class PanelAdresiBicimNormallestirmeTests
{
    // =================================================================================
    // 1) ÜRETME KATMANI — bozuk girdi artık geçerli URL veriyor mu?
    // =================================================================================

    /// <summary>
    /// Adrese port yazılmışsa URL'de İKİ port olmaz. Karar: adreste AÇIKÇA yazılmış port kazanır
    /// (gerekçe <c>PanelAddress.BuildBaseUrl</c> içinde).
    /// </summary>
    [Fact]
    public void Adreste_port_varsa_URL_de_tek_port_kalir()
    {
        Assert.Equal("https://panel.ornek.com:8890", PanelAddress.BuildBaseUrl("panel.ornek.com:8890", 8888));
        Assert.Equal(
            "https://panel.ornek.com:8890/gizli",
            PanelAddress.BuildEntryUrl("panel.ornek.com:8890", 8888, "gizli"));
    }

    /// <summary>Şema yazılmışsa URL'de iki şema olmaz; şema SÖKÜLÜR ve yok sayılır.</summary>
    [Theory]
    [InlineData("https://panel.ornek.com")]
    [InlineData("HTTPS://panel.ornek.com")]
    [InlineData("http://panel.ornek.com")]
    [InlineData("https://panel.ornek.com/")]
    [InlineData("https://panel.ornek.com/yonetim")]
    [InlineData("panel.ornek.com/")]
    [InlineData("panel.ornek.com/yonetim?a=1#b")]
    public void Sema_ve_yol_sokulur(string girdi)
    {
        Assert.Equal("https://panel.ornek.com:8888", PanelAddress.BuildBaseUrl(girdi, 8888));
    }

    /// <summary>
    /// Şemadan port ÇIKARIMI yapılmaz: <c>https://</c> yazmak masum bir alışkanlıktır, ondan 443
    /// çıkarmak 8888'de dinleyen çalışan bir kurulumu ölü bağlantıya çevirirdi.
    /// </summary>
    [Fact]
    public void Semadan_port_cikarimi_yapilmaz()
    {
        Assert.Equal("https://panel.ornek.com:8888", PanelAddress.BuildBaseUrl("https://panel.ornek.com", 8888));
        Assert.Equal("https://panel.ornek.com:8890", PanelAddress.BuildBaseUrl("http://panel.ornek.com:8890", 8888));
    }

    /// <summary>IPv6 URL'de KÖŞELİ PARANTEZ ister; parantezsiz yazılan adres geçersiz URL'dir.</summary>
    [Theory]
    [InlineData("fd00::1", "https://[fd00::1]:8888")]
    [InlineData("[fd00::1]", "https://[fd00::1]:8888")]
    [InlineData("2001:db8::1", "https://[2001:db8::1]:8888")]
    [InlineData("[2001:DB8::1]", "https://[2001:db8::1]:8888")]
    [InlineData("[2001:db8::1]:8890", "https://[2001:db8::1]:8890")]
    public void IPv6_kose_paranteze_alinir(string girdi, string beklenen)
    {
        Assert.Equal(beklenen, PanelAddress.BuildBaseUrl(girdi, 8888));
    }

    /// <summary>Türkçe harfli alan adı REDDEDİLMEZ, punycode'a çevrilir (posta istemcisi kodlamasın diye).</summary>
    [Fact]
    public void Turkce_harfli_alan_adi_punycode_olur()
    {
        Assert.Equal("https://xn--rnek-4qa.com.tr:8888", PanelAddress.BuildBaseUrl("örnek.com.tr", 8888));
    }

    /// <summary>Kanonik host küçük harftir — ve küçültme Türkçe kültürde de doğrudur.</summary>
    /// <remarks>
    /// <c>ToLower</c> Türkçe kültürde <c>"I"</c> → <c>"ı"</c> yapar; <c>IZMIR.COM</c> için üretilen
    /// <c>ızmır.com</c> var olmayan bir alan adıdır. Bu test <c>ToLowerInvariant</c> nöbetçisidir.
    /// </remarks>
    [Fact]
    public void Kucultme_Turkce_kulturde_de_dogru()
    {
        Assert.Equal("https://izmir.com:8888", PanelAddress.BuildBaseUrl("IZMIR.COM", 8888));
        Assert.Equal("https://panel.ornek.com:8888", PanelAddress.BuildBaseUrl("PANEL.ORNEK.COM", 8888));
    }

    /// <summary>Hiç host çıkarılamayan değer için host UYDURULMAZ: yer tutucu döner (ölü ama görünür).</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("https://")]
    [InlineData("/yol")]
    [InlineData("panel ornek com")]
    [InlineData("panel.ornek.com:0")]
    [InlineData("panel.ornek.com:70000")]
    [InlineData("panel.ornek.com:abc")]
    [InlineData("*.ornek.com")]
    [InlineData("kotu.com@panel.ornek.com")]
    [InlineData("panel..ornek.com")]
    public void Ayristirilamayan_deger_yer_tutucuya_duser(string? girdi)
    {
        Assert.Equal($"https://{PanelAddress.UnknownHostPlaceholder}", PanelAddress.BuildBaseUrl(girdi, 8888));
    }

    // =================================================================================
    // 2) FAZLA KAPATMA NÖBETÇİLERİ — meşru adresler AYNEN çalışmaya devam ediyor mu?
    // =================================================================================

    /// <summary>
    /// Doğrulamanın kabul ETMEK ZORUNDA olduğu adresler. Bunlardan birini reddetmek, yöneticiyi
    /// kendi panelinden kilitler — kapatılan sorundan kötüdür.
    /// </summary>
    [Theory]
    [InlineData("ornek.com")]                       // düz alan adı
    [InlineData("panel.ornek.com")]                 // alt alan adı
    [InlineData("a.b.c.panel.ornek.com")]           // derin alt alan adı
    [InlineData("ornek.com.tr")]                    // uzun TLD (iki etiketli)
    [InlineData("ornek.travel")]                    // uzun TLD
    [InlineData("a-b.ornek-panel.travel")]          // tireli etiketler
    [InlineData("xn--nl-lka.com")]                  // punycode (yönetici elle yazmış)
    [InlineData("192.168.1.5")]                     // IPv4 (iç ağ kurulumu — MEŞRU)
    [InlineData("185.7.242.225")]                   // IPv4 (genel)
    [InlineData("fd00::1")]                         // IPv6
    [InlineData("2606:4700:4700::1111")]            // IPv6 (genel)
    [InlineData("WIN-SUNUCU01")]                    // tek etiketli makine adı (otomatik çözülen değer)
    [InlineData("MASAUSTU_A1")]                     // alt çizgili makine adı
    [InlineData("localhost")]
    [InlineData("panel.ornek.com.")]                // sondaki kök noktası
    public void Mesru_adresler_gecerli_URL_uretir(string girdi)
    {
        var url = PanelAddress.BuildBaseUrl(girdi, 8888);

        Assert.DoesNotContain(PanelAddress.UnknownHostPlaceholder, url, StringComparison.Ordinal);
        // Üretilen değer, .NET'in URI ayrıştırıcısının kabul ettiği MUTLAK bir adres olmalı.
        Assert.True(Uri.TryCreate(url, UriKind.Absolute, out var uri), url);
        Assert.Equal(Uri.UriSchemeHttps, uri!.Scheme);
        Assert.Equal(8888, uri.Port);
    }

    /// <summary>Portsuz meşru adres için 443 hâlâ adrese YAZILMAZ (mevcut davranış korunuyor).</summary>
    [Fact]
    public void Dort_yuz_kirk_uc_portu_adrese_yazilmaz()
    {
        Assert.Equal("https://panel.ornek.com", PanelAddress.BuildBaseUrl("panel.ornek.com", 443));
        Assert.Equal("https://[fd00::1]", PanelAddress.BuildBaseUrl("fd00::1", 443));
        Assert.Equal("https://panel.ornek.com", PanelAddress.BuildBaseUrl("panel.ornek.com:443", 8888));
    }

    // =================================================================================
    // 3) KAYDETME KATMANI — yönetici hatayı kaydederken görüyor mu?
    // =================================================================================

    [Fact]
    public async Task Kaydederken_adres_kanonik_hale_getirilir()
    {
        using var kok = new GeciciAyarKoku();

        var sonuc = await kok.Servis.UpdateAsync(kok.Istek(" HTTPS://PANEL.Ornek.COM/yonetim "));

        // Kayda saf HOST yazılır: bu alanı DNS A kaydı, FTP sunucu adresi, posta DNS yayını ve
        // uzak veritabanı host'u da okuyor; içinde şema/yol/port olan bir değer onları da bozar.
        Assert.Equal("panel.ornek.com", sonuc.ServerAddress);

        var tekrarOkunan = await kok.Servis.GetAsync();
        Assert.Equal("panel.ornek.com", tekrarOkunan.ServerAddress);
    }

    [Fact]
    public async Task Adresteki_port_ayardakiyle_ayniysa_sessizce_elenir()
    {
        using var kok = new GeciciAyarKoku();

        var sonuc = await kok.Servis.UpdateAsync(kok.Istek("panel.ornek.com:8888"));

        Assert.Equal("panel.ornek.com", sonuc.ServerAddress);
        Assert.Equal("https://panel.ornek.com:8888", PanelAddress.BuildBaseUrl(sonuc.ServerAddress, sonuc.PreferredHttpsPort));
    }

    [Fact]
    public async Task Adresteki_port_ayardakinden_farkliysa_REDDEDILIR()
    {
        using var kok = new GeciciAyarKoku();

        var hata = await Assert.ThrowsAsync<InvalidOperationException>(
            () => kok.Servis.UpdateAsync(kok.Istek("panel.ornek.com:8890")));

        Assert.Equal("settings.server_address_port_conflict", hata.Message);
    }

    [Fact]
    public async Task Ayristirilamayan_adres_REDDEDILIR()
    {
        using var kok = new GeciciAyarKoku();

        var hata = await Assert.ThrowsAsync<InvalidOperationException>(
            () => kok.Servis.UpdateAsync(kok.Istek("panel ornek com")));

        Assert.Equal("settings.server_address_invalid", hata.Message);
    }

    /// <summary>
    /// FAZLA KAPATMA NÖBETÇİSİ — kaydetme yolu meşru adresleri reddetmemeli.
    /// </summary>
    [Theory]
    [InlineData("ornek.com", "ornek.com")]
    [InlineData("PANEL.ORNEK.COM.TR", "panel.ornek.com.tr")]
    [InlineData("a-b.ornek.travel", "a-b.ornek.travel")]
    [InlineData("xn--nl-lka.com", "xn--nl-lka.com")]
    [InlineData("örnek.com.tr", "xn--rnek-4qa.com.tr")]
    [InlineData("192.168.1.5", "192.168.1.5")]
    [InlineData("[fd00::1]", "fd00::1")]
    [InlineData("fd00::1", "fd00::1")]
    [InlineData("WIN-SUNUCU01", "win-sunucu01")]
    public async Task Mesru_adres_kaydedilebilir(string girdi, string beklenen)
    {
        using var kok = new GeciciAyarKoku();

        var sonuc = await kok.Servis.UpdateAsync(kok.Istek(girdi));

        Assert.Equal(beklenen, sonuc.ServerAddress);
    }

    /// <summary>
    /// FAZLA KAPATMA NÖBETÇİSİ — adres alanı BOŞ bırakılabilmeli. Boşsa panel makinenin kendi
    /// adresini yazar; o değeri biçim kuralına takılıp reddetmek, adresi ayarlamamış bir
    /// yöneticinin Ayarlar formunu HİÇ kaydedememesi demek olurdu.
    /// </summary>
    [Fact]
    public async Task Bos_adres_reddedilmez()
    {
        using var kok = new GeciciAyarKoku();

        var sonuc = await kok.Servis.UpdateAsync(kok.Istek("   "));

        Assert.False(string.IsNullOrWhiteSpace(sonuc.ServerAddress));
    }

    /// <remarks>
    /// Klasör KASITEN silinmez (bu depoda hiçbir silme yapılmıyor); <c>%TEMP%\adapanel-adres-bicim-*</c>
    /// altında kalır ve elle temizlenebilir.
    /// </remarks>
    private sealed class GeciciAyarKoku : IDisposable
    {
        public GeciciAyarKoku()
        {
            Kok = Path.Combine(Path.GetTempPath(), "adapanel-adres-bicim-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Kok);

            var secenekler = Microsoft.Extensions.Options.Options.Create(
                new PanelRuntimeOptions { DataRoot = Kok, PreferredHttpsPort = 8888 });
            Servis = new PanelPreferencesService(new JsonFileStore(secenekler), secenekler);
        }

        public string Kok { get; }

        public PanelPreferencesService Servis { get; }

        public UpdatePanelPreferencesRequest Istek(string adres) => new()
        {
            PanelName = "AdaPanel",
            DefaultLanguage = "tr",
            ThemeMode = "light",
            UiDensity = "compact",
            DefaultSiteRoot = Path.Combine(Kok, "siteler"),
            // Panelin KENDİ yedek klasörü. Bu testin konusu ADRES normalleştirmesi; yedek kökü
            // artık doğrulanıyor (PanelBackupRootGuardTests) ve veri kökü altında yalnız panelin
            // kendi "backups" ağacı kabul ediliyor — burada rastgele bir ad kullanmak, testi
            // ölçmek istediği şeyden koparırdı.
            DefaultBackupRoot = Path.Combine(Kok, "backups"),
            ServerAddress = adres,
            PreferredHttpsPort = 8888,
            LoginLockoutMinutes = 15,
            DefaultRuntimeProfile = "auto",
            InstallRuntimeIfMissing = true,
        };

        public void Dispose()
        {
            // Bilerek boş: geçici klasör silinmez (yukarıdaki nota bakınız).
        }
    }
}
