using System.Net;
using System.Net.Http;
using System.Text.Json;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.SelfUpdate;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// BAYAT <c>updates/pending-update.json</c> HAYALETİ.
/// </summary>
/// <remarks>
/// <para>
/// <b>Kullanıcının makinesinde ölçülen olay:</b> kurulum 22:29'da BAŞARIYLA bitti, ama
/// <c>pending-update.json</c> 0.5.6'yı göstermeye devam etti (22:28'den kalma) ve saatler sonra
/// hâlâ oradaydı. Panel her açılışta o bayat kaydı okuyup kullanıcıya yanlış durum gösterdi
/// ("paket hazır", "kurulum planlandı").
/// </para>
/// <para>
/// <b>İki kök sebep vardı ve ikisi de burada sabitleniyor:</b>
/// </para>
/// <list type="number">
/// <item><b>İki farklı ölçüt.</b> Temizlik <c>pendingVersion == currentVersion</c> EŞİTLİĞİNE,
/// hüküm ise "hedefe ULAŞILDI MI" karşılaştırmasına bakıyordu — aynı gövdede, on beş satır arayla.
/// Çalışan sürüm bekleyen sürümden daha yeniyse (kullanıcı arada 0.5.8'i kurdu) eşitlik tutmaz ve
/// kayıt HİÇ silinmezdi.</item>
/// <item><b>Tek try bloğu.</b> Paket dosyası ile kayıt dosyası aynı <c>try</c> içinde siliniyordu;
/// kilitli bir ~50 MB exe (antivirüs/Explorer) ilk satırda patlayınca KAYIT DOSYASINA HİÇ SIRA
/// GELMİYORDU.</item>
/// </list>
/// <para>
/// Karşı taraf da aynı derecede önemli: GERÇEKTEN bekleyen bir kurulum ASLA silinmemeli. Ayrım
/// tahminle değil, çalışan sürümün ölçülmesiyle yapılır.
/// </para>
/// </remarks>
public sealed class PendingUpdateCleanupTests
{
    [Theory]
    [InlineData("", "0.5.8", false)]        // kayit yok -> temizlenecek bir sey de yok
    [InlineData("0.5.8", "0.5.8", true)]    // kuruldu (esit)
    [InlineData("0.5.6", "0.5.8", true)]    // OLCULEN HATA: calisan surum DAHA YENI -> kayit gecmis
    [InlineData("0.5.9", "0.5.8", false)]   // gercekten bekliyor -> DOKUNMA
    [InlineData("99.0.0", "0.5.8", false)]  // gercekten bekliyor -> DOKUNMA
    public void Bekleyen_kaydin_ANLAMSIZ_olup_olmadigi(string bekleyen, string calisan, bool beklenen)
    {
        // Kurulum PLANLANMAMIS kayit: karar yalniz surume bakar.
        Assert.Equal(
            beklenen,
            SelfUpdateService.IsPendingUpdateObsolete(bekleyen, calisan, null, DateTime.UtcNow));
    }

    [Fact]
    public void YENI_planlanmis_kurulum_SILINMEZ_surum_esit_olsa_bile()
    {
        // AYNI (ya da daha eski) surumu yeniden kurma: surum olcutu daha kurulum baslamadan
        // "hedefe ulasildi" der. Ikinci olcum sart -- panel, kurulum planlandigindan BU YANA yeniden
        // baslamadiysa kurulum tamamlanmis olamaz. Bu koruma olmasaydi kayit, kurulum kuyrukta
        // beklerken silinir ve kullanici "kurulum planlandi" durumunu hic goremezdi.
        var surecBaslangici = DateTime.UtcNow.AddHours(-3);

        Assert.False(SelfUpdateService.IsPendingUpdateObsolete(
            "0.5.8", "0.5.8", installScheduledAtUtc: DateTime.UtcNow, processStartUtc: surecBaslangici));

        // Ayni kayit, panel YENIDEN BASLADIKTAN sonra: artik hayalettir.
        Assert.True(SelfUpdateService.IsPendingUpdateObsolete(
            "0.5.8", "0.5.8", installScheduledAtUtc: surecBaslangici.AddMinutes(-1), processStartUtc: surecBaslangici));
    }

    [Fact]
    public async Task Kurulmus_surumun_bekleyen_kaydi_SILINIR()
    {
        var veriKoku = VeriKokuOlustur();
        try
        {
            // Bekleyen surum = panelin SU AN calisan surumu -> kurulum gerceklesmis.
            var paket = await BekleyenYazAsync(veriKoku, VersionInfo.Current, planlandi: true);

            var durum = await Servis(veriKoku).CheckForUpdateAsync();

            Assert.False(File.Exists(Path.Combine(veriKoku, "updates", "pending-update.json")));
            Assert.False(File.Exists(paket));
            Assert.False(durum.IsPackageReady);
            Assert.False(durum.IsInstallScheduled);
        }
        finally
        {
            Directory.Delete(veriKoku, recursive: true);
        }
    }

    [Fact]
    public async Task GECILMIS_bekleyen_kayit_da_SILINIR()
    {
        // ASIL ARIZA: kayit 0.0.1'i gosteriyor ama panel DAHA YENI bir surumle calisiyor. Eski
        // esitlik olcutu burada tutmuyordu ve kayit sonsuza kadar kaliyordu.
        var veriKoku = VeriKokuOlustur();
        try
        {
            await BekleyenYazAsync(veriKoku, "0.0.1", planlandi: false);

            var durum = await Servis(veriKoku).CheckForUpdateAsync();

            Assert.False(File.Exists(Path.Combine(veriKoku, "updates", "pending-update.json")));
            Assert.False(durum.IsPackageReady);
            Assert.Empty(durum.PackagePath);
        }
        finally
        {
            Directory.Delete(veriKoku, recursive: true);
        }
    }

    [Fact]
    public async Task GERCEKTEN_bekleyen_kurulum_SILINMEZ()
    {
        // Paket indirilmis ama kurulum HENUZ BASLAMAMIS: bu kaydi silmek, kullanicinin indirdigi
        // guncellemeyi sessizce cope atmak olurdu.
        var veriKoku = VeriKokuOlustur();
        try
        {
            var paket = await BekleyenYazAsync(veriKoku, "99.0.0", planlandi: false);

            var durum = await Servis(veriKoku).CheckForUpdateAsync();

            Assert.True(File.Exists(Path.Combine(veriKoku, "updates", "pending-update.json")));
            Assert.True(File.Exists(paket));
            Assert.True(durum.IsPackageReady);
        }
        finally
        {
            Directory.Delete(veriKoku, recursive: true);
        }
    }

    [Fact]
    public async Task SUREN_kurulumun_kaydi_SILINMEZ()
    {
        // Kurulum planlanmis ve daha yeni bir surum hedefleniyor: is bitmedi, kayit duracak.
        var veriKoku = VeriKokuOlustur();
        try
        {
            await BekleyenYazAsync(veriKoku, "99.0.0", planlandi: true);

            var durum = await Servis(veriKoku).CheckForUpdateAsync();

            Assert.True(File.Exists(Path.Combine(veriKoku, "updates", "pending-update.json")));
            Assert.True(durum.IsInstallScheduled);
        }
        finally
        {
            Directory.Delete(veriKoku, recursive: true);
        }
    }

    [Fact]
    public async Task AZ_ONCE_planlanan_AYNI_surum_kurulumu_SILINMEZ()
    {
        // Yeniden kurulum / geri alma: bekleyen surum calisan surume esit, ama kurulum SU AN
        // planlandi ve panel henuz yeniden baslamadi. Kayit silinseydi kullanici, baslatilmis
        // kurulumun izini kaybederdi.
        var veriKoku = VeriKokuOlustur();
        try
        {
            await BekleyenYazAsync(veriKoku, VersionInfo.Current, planlanmaAni: DateTime.UtcNow);

            var durum = await Servis(veriKoku).CheckForUpdateAsync();

            Assert.True(File.Exists(Path.Combine(veriKoku, "updates", "pending-update.json")));
            Assert.True(durum.IsInstallScheduled);
        }
        finally
        {
            Directory.Delete(veriKoku, recursive: true);
        }
    }

    [Fact]
    public async Task Paket_dosyasi_KILITLIYKEN_bile_bekleyen_KAYIT_silinir()
    {
        // KOK SEBEP 2'NIN TESTI: iki silme tek try icindeyken kilitli paket ilk satirda patliyor ve
        // yaniltici olan kayit dosyasi yerinde kaliyordu. Windows'ta ~50 MB'lik bir kurulum
        // dosyasinin antivirus/Explorer tarafindan tutulmasi ender degil, olagan.
        var veriKoku = VeriKokuOlustur();
        FileStream? kilit = null;
        try
        {
            var paket = await BekleyenYazAsync(veriKoku, VersionInfo.Current, planlandi: true);
            kilit = new FileStream(paket, FileMode.Open, FileAccess.Read, FileShare.None);

            var durum = await Servis(veriKoku).CheckForUpdateAsync();

            Assert.True(File.Exists(paket)); // kilitli oldugu icin silinemedi (beklenen)
            Assert.False(File.Exists(Path.Combine(veriKoku, "updates", "pending-update.json")));
            Assert.False(durum.IsInstallScheduled);
        }
        finally
        {
            kilit?.Dispose();
            Directory.Delete(veriKoku, recursive: true);
        }
    }

    /// <summary>
    /// Bekleyen kaydi ve yanindaki sahte paketi yazar; paketin yolunu dondurur.
    /// </summary>
    /// <param name="planlandi">
    /// Kurulum planlanmis mi. Planlanma ani BIR GUN GERIYE alinir: temizlik olcutu "panel kurulum
    /// planlandiktan SONRA yeniden basladi mi" diye sorar ve test surecinin baslama ani, testin
    /// kendi calisma suresine gore degisir. Bir gunluk fark, uzun suren bir test kosusunda bile
    /// olcumu belirsiz birakmaz.
    /// </param>
    private static Task<string> BekleyenYazAsync(string veriKoku, string surum, bool planlandi)
        => BekleyenYazAsync(veriKoku, surum, planlandi ? DateTime.UtcNow.AddDays(-1) : null);

    private static async Task<string> BekleyenYazAsync(string veriKoku, string surum, DateTime? planlanmaAni)
    {
        var guncellemeler = Path.Combine(veriKoku, "updates");
        Directory.CreateDirectory(guncellemeler);
        var paket = Path.Combine(guncellemeler, "AdaPanelSetup.exe");
        await File.WriteAllBytesAsync(paket, [(byte)'M', (byte)'Z', 1, 2, 3, 4]);

        await File.WriteAllTextAsync(
            Path.Combine(guncellemeler, "pending-update.json"),
            JsonSerializer.Serialize(new
            {
                version = surum,
                downloadUrl = "https://example.test/AdaPanelSetup.exe",
                packagePath = paket,
                packageSha256 = new string('A', 64),
                stagedAtUtc = (planlanmaAni ?? DateTime.UtcNow).AddMinutes(-5),
                installScheduledAtUtc = planlanmaAni
            }));

        return paket;
    }

    private static SelfUpdateService Servis(string veriKoku)
        => new(
            new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = veriKoku })),
            new StubHttpClientFactory(),
            new NoopLauncher());

    private static string VeriKokuOlustur()
    {
        var yol = Path.Combine(Path.GetTempPath(), $"adapanel-pending-{Guid.NewGuid():N}");
        Directory.CreateDirectory(yol);
        return yol;
    }

    private sealed class NoopLauncher : IUpdateInstallerLauncher
    {
        public Task ScheduleAsync(string packagePath, string packageSha256, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    // Testi hermetik tutar: gercek aga CIKMAZ (GitHub sorgusu 404 doner, surum onbellekten gelir).
    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new NotFoundHandler());
    }

    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
