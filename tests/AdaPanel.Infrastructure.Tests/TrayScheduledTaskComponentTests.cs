using System.Text.Json;
using AdaPanel.Api.Tray;
using AdaPanel.Infrastructure.Bootstrap;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Tepsi, ZAMANLANMIŞ GÖREVLE çalışan bileşenleri (posta sunucusu, önbellek) Windows servisi
/// sanmamalı.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bulgunun ölçümü:</b> bu makinede <c>Get-Service</c> yalnız <c>AdaPanel</c> döndü;
/// <c>AdaStalwart</c> ise Task Scheduler'da <c>State=3</c> (Ready) olarak duruyordu. Tepsi
/// <c>components.json</c>'daki <c>serviceName</c> alanını gördüğü için onu SCM'de arıyor,
/// bulamıyor ve KALICI olarak "kurulu, servisi kayıtlı değil" + "panelden yeniden kurun"
/// yazıyordu — kurulumu sağlam bir posta sunucusunu bozmaya davet eden bir tavsiye. İzleme
/// çıkış kodu da aynı sebeple kalıcı 1'di.
/// </para>
/// <para>
/// <b>Yetki gerçeği (aynı makinede ölçüldü):</b> yetkisiz oturumda var olan görev için
/// <c>GetTask</c> → <c>0x80070005</c> (erişim yok), OLMAYAN görev için <c>0x80070002</c>
/// (yok). <c>schtasks.exe</c> ikisine de <c>exit 1</c> veriyor, yani bu ayrımı YAPAMIYOR.
/// Testler bu ayrımı bekçiler: "soramadık" ile "yok" birleşirse hata geri gelir.
/// </para>
/// </remarks>
public sealed class TrayScheduledTaskComponentTests
{
    private static ServisDurumu Gorev(
        GorevDurumu gorev,
        bool dosyalarKurulu = true,
        bool? surecGorundu = null,
        IReadOnlyList<int>? portlar = null)
        => new(
            "stalwart-mail",
            "Stalwart Mail Server",
            ServisKayitli: gorev is GorevDurumu.Calisiyor or GorevDurumu.Durdu or GorevDurumu.DevreDisi,
            DosyalarKurulu: dosyalarKurulu,
            Durum: null,
            Portlar: portlar ?? [],
            ServisiVar: true,
            Bicim: CalismaBicimi.ZamanlanmisGorev,
            Gorev: gorev,
            SurecGorundu: surecGorundu);

    private static ServisDurumu Servis(
        bool servisKayitli,
        System.ServiceProcess.ServiceControllerStatus? durum,
        bool dosyalarKurulu = true)
        => new("mariadb", "MariaDB", servisKayitli, dosyalarKurulu, durum, [], true, CalismaBicimi.Servis);

    // --- 1) Yanlış cümle bir daha kurulamaz ---

    /// <summary>
    /// Zamanlanmış görevle çalışan bileşen için "servisi kayıtlı değil" cümlesi HİÇBİR durumda
    /// yazılamaz: öyle bir servis zaten olmamalı.
    /// </summary>
    [Theory]
    [InlineData(GorevDurumu.Olculemedi)]
    [InlineData(GorevDurumu.Kayitsiz)]
    [InlineData(GorevDurumu.Durdu)]
    [InlineData(GorevDurumu.DevreDisi)]
    [InlineData(GorevDurumu.Calisiyor)]
    public void GorevBileseni_ServisCumlesiniAsla_Yazmaz(GorevDurumu gorev)
    {
        Assert.DoesNotContain("servisi kayıtlı değil", Gorev(gorev).DurumMetni, StringComparison.Ordinal);
    }

    // --- 2) "Ölçülemedi" ile "yok" karışmaz ---

    [Fact]
    public void DurumuOkunamayanGorev_OlculemediSayilir_ArizaDegildir()
    {
        var durum = Gorev(GorevDurumu.Olculemedi);

        Assert.True(durum.Olculemedi);
        Assert.False(durum.Calisiyor);
        Assert.False(ServisYoneticisi.ArizaVar([durum]));
        Assert.Contains("okunamadı", durum.DurumMetni, StringComparison.Ordinal);
    }

    [Fact]
    public void GorevGercektenKayitliDegilse_ArizaOlarakBildirilir()
    {
        var durum = Gorev(GorevDurumu.Kayitsiz);

        Assert.False(durum.Olculemedi);
        Assert.True(ServisYoneticisi.ArizaVar([durum]));
        Assert.Contains("görevi kayıtlı değil", durum.DurumMetni, StringComparison.Ordinal);
    }

    // --- 3) Süreç ölçümü: yetkisiz oturumda cevaplanabilen tek soru ---

    [Fact]
    public void SurecGorunuyorsa_GorevDurumuOkunamasaBile_Calisiyor()
    {
        var durum = Gorev(GorevDurumu.Olculemedi, surecGorundu: true, portlar: [143, 993]);

        Assert.True(durum.Calisiyor);
        Assert.False(durum.Olculemedi);
        Assert.False(ServisYoneticisi.ArizaVar([durum]));
        Assert.Contains("çalışıyor", durum.DurumMetni, StringComparison.Ordinal);
        Assert.Contains("143", durum.DurumMetni, StringComparison.Ordinal);
    }

    [Fact]
    public void SurecYoksa_VeDosyalarKuruluysa_Durdu()
    {
        var durum = Gorev(GorevDurumu.Olculemedi, surecGorundu: false);

        Assert.False(durum.Calisiyor);
        Assert.False(durum.Olculemedi);
        Assert.Equal("durdu", durum.DurumMetni);
        Assert.True(ServisYoneticisi.ArizaVar([durum]));
    }

    [Fact]
    public void HicKurulmamisBilesen_ArizaDegildir()
    {
        var durum = Gorev(GorevDurumu.Kayitsiz, dosyalarKurulu: false, surecGorundu: false);

        Assert.Equal("kurulu değil", durum.DurumMetni);
        Assert.False(ServisYoneticisi.ArizaVar([durum]));
    }

    /// <summary>
    /// Panel MailEnable'a geçince <c>AdaStalwart</c> görevini KENDİ devre dışı bırakıyor; bunu
    /// arıza saymak, yöneticinin kararını alarm diye bildirmek olurdu.
    /// </summary>
    [Fact]
    public void DevreDisiGorev_AlarmUretmez_AmaCalisiyorDaDemez()
    {
        var durum = Gorev(GorevDurumu.DevreDisi, surecGorundu: false);

        Assert.False(durum.Calisiyor);
        Assert.False(ServisYoneticisi.ArizaVar([durum]));
        Assert.Contains("devre dışı", durum.DurumMetni, StringComparison.Ordinal);
    }

    // --- 4) Servis tarafı BOZULMADI ---

    [Fact]
    public void Servis_DurmussaHalaAriza()
    {
        var durum = Servis(true, System.ServiceProcess.ServiceControllerStatus.Stopped);

        Assert.Equal("durdu", durum.DurumMetni);
        Assert.True(ServisYoneticisi.ArizaVar([durum]));
    }

    [Fact]
    public void Servis_KayitliDegilAmaDosyalarVarsa_HalaAriza()
    {
        var durum = Servis(false, null);

        Assert.Equal("kurulu, servisi kayıtlı değil", durum.DurumMetni);
        Assert.True(ServisYoneticisi.ArizaVar([durum]));
    }

    [Fact]
    public void Servis_CalisiyorsaAlarmYok()
    {
        var durum = Servis(true, System.ServiceProcess.ServiceControllerStatus.Running);

        Assert.False(ServisYoneticisi.ArizaVar([durum]));
    }

    // --- 5) HRESULT ayrımı: hatanın kök nedeni buydu ---

    [Fact]
    public void ErisimReddi_YokIleAyniCevabaInmez()
    {
        Assert.Equal(GorevDurumu.Olculemedi, ZamanlanmisGorevler.HResultuCevir(unchecked((int)0x80070005)));
        Assert.Equal(GorevDurumu.Kayitsiz, ZamanlanmisGorevler.HResultuCevir(unchecked((int)0x80070002)));
    }

    [Theory]
    [InlineData(4, GorevDurumu.Calisiyor)]
    [InlineData(3, GorevDurumu.Durdu)]
    [InlineData(2, GorevDurumu.Durdu)]
    [InlineData(1, GorevDurumu.DevreDisi)]
    [InlineData(0, GorevDurumu.Olculemedi)]
    [InlineData(null, GorevDurumu.Olculemedi)]
    public void TaskStateSayisi_AnlaminaCevrilir(int? state, GorevDurumu beklenen)
    {
        Assert.Equal(beklenen, ZamanlanmisGorevler.DurumuCevir(state));
    }

    /// <summary>
    /// COM zinciri GERÇEKTEN çalışıyor mu — yoksa her çağrı sessizce "ölçülemedi"ye mi düşüyor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Var olmayan bir görev adı Task Scheduler'dan <c>0x80070002</c> döndürür ve bu kod ancak
    /// <c>Connect</c> + <c>GetFolder</c> + <c>GetTask</c> zincirinin TAMAMI çalıştıysa alınabilir.
    /// Ölçüldü: yetkisiz oturumda bile bu koda ulaşılıyor (kök klasör okunabiliyor; erişimi
    /// kısıtlı olan tekil görevler ise <c>0x80070005</c> veriyor).
    /// </para>
    /// <para>
    /// GEVŞEK YAZILMAMASININ SEBEBİ ÖLÇÜLDÜ: "Kayitsiz ya da Olculemedi" biçiminde yazılan ilk
    /// hâl YEŞİL yanıyordu, oysa okuyucu gerçekte HER çağrıda <c>Olculemedi</c> döndürüyordu —
    /// geç bağlama hatayı <c>COMException</c> değil, <c>TargetInvocationException</c> içine
    /// sarılı <c>FileNotFoundException</c> olarak veriyor ve tür süzgeci hiç tutmuyordu. Testi
    /// kesinleştirmek, sessizce ölü olan kodu ortaya çıkardı.
    /// </para>
    /// </remarks>
    [Fact]
    public void ZamanlanmisGorevOkuyucusu_OlmayanGoreviYokDiyeBildirir()
    {
        var sonuc = ZamanlanmisGorevler.Oku("AdaPanelYokBoyleGorev-" + Guid.NewGuid().ToString("N"));

        Assert.Equal(GorevDurumu.Kayitsiz, sonuc);
    }

    // --- 6) Bildirim ile kurulum kodu AYNI ŞEYİ söylüyor mu ---

    private static JsonElement Bilesen(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void ZamanlanmisGorevKancasiTasiyanBilesen_GorevOlarakCozulur()
    {
        var bicim = BilesenListesi.BicimiCoz(
            "AdaStalwart",
            Bilesen("""{"postInstall":["installStalwartService"]}"""));

        Assert.Equal(CalismaBicimi.ZamanlanmisGorev, bicim);
    }

    [Fact]
    public void GercekServisKuranBilesen_ServisOlarakCozulur()
    {
        var bicim = BilesenListesi.BicimiCoz(
            "AdaRedis",
            Bilesen("""{"postInstall":["installRedisService"]}"""));

        Assert.Equal(CalismaBicimi.Servis, bicim);
    }

    [Fact]
    public void ServisAdiBosOlanBilesen_ArkaPlanBirimiYok()
    {
        Assert.Equal(CalismaBicimi.Yok, BilesenListesi.BicimiCoz(string.Empty, Bilesen("""{"postInstall":[]}""")));
    }

    /// <summary>
    /// DEPODAKİ GERÇEK <c>components.json</c>: zamanlanmış görev kancası taşıyan her bileşen
    /// tepside de görev olarak çözülmeli ve süreç adı BİLDİRİLMİŞ olmalı.
    /// </summary>
    /// <remarks>
    /// Süreç adı şart, çünkü yetkisiz oturumda ölçülebilen TEK gerçek odur. Alan boş bırakılırsa
    /// tepsi o bileşen için kalıcı "durumu okunamadı" gösterir — sessiz bir körlük.
    /// </remarks>
    [Fact]
    public void Bildirimdeki_GorevBilesenleri_SurecAdiniDaBildirir()
    {
        var yol = Path.Combine(ScriptTestHost.RepoRoot, "components.json");
        using var belge = JsonDocument.Parse(File.ReadAllText(yol));
        var bilesenler = belge.RootElement.GetProperty("components").EnumerateArray().ToList();

        var gorevler = bilesenler
            .Where(b => b.TryGetProperty("postInstall", out var adimlar)
                && adimlar.ValueKind == JsonValueKind.Array
                && adimlar.EnumerateArray().Any(a =>
                    a.ValueKind == JsonValueKind.String
                    && ComponentBootstrapper.ZamanlanmisGorevKancalari.Contains(a.GetString()!)))
            .ToList();

        Assert.NotEmpty(gorevler);
        foreach (var bilesen in gorevler)
        {
            var kimlik = bilesen.GetProperty("id").GetString();
            var servisAdi = bilesen.GetProperty("serviceName").GetString() ?? string.Empty;

            Assert.Equal(CalismaBicimi.ZamanlanmisGorev, BilesenListesi.BicimiCoz(servisAdi, bilesen));
            Assert.True(
                bilesen.TryGetProperty("processName", out var surec)
                    && !string.IsNullOrWhiteSpace(surec.GetString()),
                $"{kimlik}: zamanlanmış görevle çalışıyor ama 'processName' bildirilmemiş.");
        }
    }

    /// <summary>
    /// Bildirimde <c>serviceName</c> dolu olup zamanlanmış görev kancası TAŞIMAYAN bileşenler
    /// gerçekten servis olmalı; aksi hâlde tepsi onları yine SCM'de arar.
    /// </summary>
    [Fact]
    public void Bildirimdeki_ServisBilesenleri_GorevOlarakIsaretlenmez()
    {
        var yol = Path.Combine(ScriptTestHost.RepoRoot, "components.json");
        using var belge = JsonDocument.Parse(File.ReadAllText(yol));

        foreach (var bilesen in belge.RootElement.GetProperty("components").EnumerateArray())
        {
            var servisAdi = bilesen.GetProperty("serviceName").GetString() ?? string.Empty;
            if (servisAdi.Length == 0)
            {
                Assert.Equal(CalismaBicimi.Yok, BilesenListesi.BicimiCoz(servisAdi, bilesen));
                continue;
            }

            var bicim = BilesenListesi.BicimiCoz(servisAdi, bilesen);
            Assert.True(bicim is CalismaBicimi.Servis or CalismaBicimi.ZamanlanmisGorev);
        }
    }
}
