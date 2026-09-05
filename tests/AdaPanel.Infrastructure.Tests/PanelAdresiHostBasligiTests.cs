using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Panelin DIŞARIYA verdiği adresi kim belirliyor: sunucunun ayarı mı, isteğin <c>Host</c> başlığı mı?
/// </summary>
/// <remarks>
/// <para>
/// <b>Ölçülen bulgu:</b> <c>appsettings.json</c>'da <c>"AllowedHosts": "*"</c> ve boru hattında
/// <c>UseHostFiltering</c> yok — panel gelen HER <c>Host</c> başlığını kabul eder, yani başlık
/// istemcinin serbest metnidir. Bu tek başına bir açık DEĞİLDİR; açık, o başlığın
/// güvenlik-etkili bir karara girmesiyle oluşur. <c>PanelAddress</c> eskiden tam olarak bunu
/// yapıyordu: ayar boşsa isteğin <c>Host</c>'una düşen bir <c>fallbackHost</c> yedeği vardı ve bu
/// yedek hem KİMLİKSİZ uçtan giden abonelikten çıkma postasını hem de TEK KULLANIMLIK GİRİŞ
/// bağlantısını besliyordu.
/// </para>
/// <para>
/// <b>Neden <c>AllowedHosts</c> kısılmadı:</b> panele meşru olarak <c>localhost</c>,
/// <c>127.0.0.1</c>, makinenin IP'si, makine adı, kurulumda verilen panel alan adı ve müşterinin
/// sonradan eklediği alan adı üzerinden erişilir. Bu küme çalışma zamanında değişir; statik bir
/// beyaz listeye çevrilseydi listede olmayan her adres 400 alırdı — müşteri kendi panelinden
/// kilitlenirdi. Kapatılan şey isteğin kabulü değil, başlığın KARARA GİRMESİDİR.
/// </para>
/// <para>
/// <b>Bu dosya iki şeyi ölçer:</b> (1) yedeğin kaldırılmasının hiçbir meşru adresi bozmadığını
/// (fazla kapatma nöbetçileri) ve (2) yedeği gereksiz kılan asıl güvencenin gerçekten var
/// olduğunu — panel adresi ayar kaynağında ASLA boş kalmaz. İkincisi kritiktir: yedek "adres
/// bilinmiyorsa" diye konmuştu, ama adres hiçbir zaman bilinmiyor olmuyor.
/// </para>
/// </remarks>
public sealed class PanelAdresiHostBasligiTests
{
    // ---------------------------------------------------------------------------------
    // 1) KALDIRILAN YEDEĞİN YERİNE GEÇEN GÜVENCE
    //    Yedek, "ServerAddress boş kalabilir" varsayımına dayanıyordu. Varsayım ölçülüyor.
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// HİÇ ayar dosyası yokken bile panel adresi boş dönmez (ilk açılış / temiz kurulum).
    /// </summary>
    /// <remarks>
    /// MUTASYON NÖBETÇİSİ: <c>PanelPreferencesService.ResolveServerAddress()</c> bir gün boş
    /// döndürmeye başlarsa bu test kırmızıya düşer ve haber verir — çünkü o an
    /// <c>PanelAddress.UnknownHostPlaceholder</c> gerçek e-postalara sızmaya başlar.
    /// </remarks>
    [Fact]
    public async Task Ayar_dosyasi_HIC_yokken_bile_panel_adresi_BOS_donmez()
    {
        using var duzenek = new GeciciKok();

        var tercihler = await duzenek.Servis.GetAsync(CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(tercihler.ServerAddress));
    }

    /// <summary>
    /// Ayar dosyasında adres BOŞ STRING olarak dursa bile okuma katmanı onu doldurur.
    /// </summary>
    /// <remarks>
    /// Yedeğin devreye girebileceği tek senaryo buydu: kayıtta boş bir adres. Bu testin yeşil
    /// olması, o senaryonun okuma yolunda kapalı olduğu anlamına gelir — yani kaldırılan yedek
    /// üretimde zaten hiç çalışmıyordu.
    /// </remarks>
    [Fact]
    public async Task Kayitta_adres_BOS_yazsa_bile_okuma_katmani_DOLDURUR()
    {
        using var duzenek = new GeciciKok();
        var yol = Path.Combine(duzenek.Kok, "config");
        Directory.CreateDirectory(yol);
        await File.WriteAllTextAsync(
            Path.Combine(yol, "panel-preferences.json"),
            """{"serverAddress":"","preferredHttpsPort":8890}""");

        var tercihler = await duzenek.Servis.GetAsync(CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(tercihler.ServerAddress));
    }

    /// <summary>Ayarlanmış adres OLDUĞU GİBİ korunur — okuma katmanı onu ezmez.</summary>
    [Fact]
    public async Task Ayarlanmis_panel_adresi_KORUNUR()
    {
        using var duzenek = new GeciciKok();
        var yol = Path.Combine(duzenek.Kok, "config");
        Directory.CreateDirectory(yol);
        await File.WriteAllTextAsync(
            Path.Combine(yol, "panel-preferences.json"),
            """{"serverAddress":"panel.ornek.com","preferredHttpsPort":8890}""");

        var tercihler = await duzenek.Servis.GetAsync(CancellationToken.None);

        Assert.Equal("panel.ornek.com", tercihler.ServerAddress);
    }

    // ---------------------------------------------------------------------------------
    // 2) FAZLA KAPATMA NÖBETÇİLERİ
    //    "Host'u kullanma" kararı, panele meşru olarak erişilen adreslerin hiçbirini bozmamalı.
    //    Aşağıdaki değerler panelin gerçekten kullanıldığı adreslerdir: kurulum sonrası yerel
    //    erişim (localhost / 127.0.0.1), otomatik çözülen sunucu IP'si, makine adı ve kurulumda
    //    verilen panel alan adı.
    // ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("panel.ornek.com")]
    [InlineData("185.7.242.225")]
    [InlineData("WIN-SUNUCU01")]
    public void Mesru_panel_adresleri_ACILABILIR_adres_uretir(string adres)
    {
        var kok = PanelAddress.BuildBaseUrl(adres, 8890);

        Assert.Equal($"https://{adres}:8890", kok);
        Assert.DoesNotContain(PanelAddress.UnknownHostPlaceholder, kok, StringComparison.Ordinal);
        Assert.True(Uri.TryCreate(kok, UriKind.Absolute, out _), $"Üretilen adres ayrıştırılamadı: {kok}");
    }

    /// <summary>
    /// Panel varsayılan dışı bir portta (8890) yayındayken port adrese YAZILIR; 443'te YAZILMAZ.
    /// </summary>
    /// <remarks>
    /// Portu düşürmek, panelin gerçekte 8890'da dinlediği kurulumlarda bağlantıyı ölü yapardı;
    /// 443'e port yazmak ise panelin diğer bağlantılarıyla tutarsız bir adres üretirdi.
    /// </remarks>
    [Fact]
    public void Varsayilan_disi_port_adrese_YAZILIR_443_YAZILMAZ()
    {
        Assert.Equal("https://panel.ornek.com:8890", PanelAddress.BuildBaseUrl("panel.ornek.com", 8890));
        Assert.Equal("https://panel.ornek.com", PanelAddress.BuildBaseUrl("panel.ornek.com", 443));
    }

    /// <summary>Gizli giriş yolu adrese eklenir; kök adres kuralları aynen geçerlidir.</summary>
    [Fact]
    public void Giris_adresi_GIZLI_YOLU_tasir()
    {
        Assert.Equal(
            "https://panel.ornek.com:8890/gizli-giris",
            PanelAddress.BuildEntryUrl("panel.ornek.com", 8890, "gizli-giris"));

        // Yol boşsa fazladan eğik çizgi bırakılmaz.
        Assert.Equal(
            "https://panel.ornek.com:8890",
            PanelAddress.BuildEntryUrl("panel.ornek.com", 8890, string.Empty));
    }

    // ---------------------------------------------------------------------------------
    // 3) SALDIRGAN GİRDİSİ
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Adres kaynağı boşsa üretilen bağlantı YER TUTUCU taşır — istemciden gelen hiçbir şey değil.
    /// </summary>
    /// <remarks>
    /// Bu, kaldırılan yedeğin bıraktığı boşluğun nasıl doldurulduğunu sabitler: bilinmeyen adres
    /// ÖLÜ bir bağlantı üretir. Yanlış yön bilerek buraya seçildi — açılmayan bir bağlantı,
    /// saldırganın alan adına giden çalışan bir bağlantıdan iyidir.
    /// </remarks>
    [Fact]
    public void Adres_bilinmiyorsa_YER_TUTUCU_uretilir()
    {
        Assert.Equal($"https://{PanelAddress.UnknownHostPlaceholder}", PanelAddress.BuildBaseUrl(string.Empty, 8890));
        Assert.Equal($"https://{PanelAddress.UnknownHostPlaceholder}", PanelAddress.BuildBaseUrl(null, 8890));
        Assert.Equal($"https://{PanelAddress.UnknownHostPlaceholder}", PanelAddress.BuildBaseUrl("   ", 8890));
    }

    /// <remarks>
    /// Klasör KASITEN silinmez: bu oturumda hiçbir silme işlemi yapılmıyor. Klasörler
    /// <c>%TEMP%\adapanel-panel-adresi-*</c> altında benzersiz adlarla kalır ve elle temizlenebilir.
    /// </remarks>
    private sealed class GeciciKok : IDisposable
    {
        public GeciciKok()
        {
            Kok = Path.Combine(Path.GetTempPath(), "adapanel-panel-adresi-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Kok);

            // Tam nitelemeli: `AdaPanel.Infrastructure.Options` ad alanı `Options` adını gölgeliyor.
            var secenekler = Microsoft.Extensions.Options.Options.Create(
                new PanelRuntimeOptions { DataRoot = Kok, PreferredHttpsPort = 8890 });
            Servis = new PanelPreferencesService(new JsonFileStore(secenekler), secenekler);
        }

        public string Kok { get; }

        public PanelPreferencesService Servis { get; }

        public void Dispose()
        {
            // Bilerek boş: geçici klasör silinmez (yukarıdaki nota bakınız).
        }
    }
}
