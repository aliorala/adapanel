using System.Text.Json;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Users;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// WHMCS modülü, silme yanıtındaki KALINTILARI okuyup WHMCS'e not olarak yazar.
/// </summary>
/// <remarks>
/// <para>
/// <b>Düzeltilen arıza:</b> kullanıcı silme yarım kaldığında panel <c>409</c> + yapısal kalıntı
/// listesi döndürüyordu ve modül bunu hata olarak görüyordu. Ama modülün VARSAYILANI
/// <c>removeSites=false</c>'tur; o yolda panel <c>200</c> + <c>success:true</c> döner (siteler
/// BİLEREK bırakılmıştır) ve gövdede yine <c>data.leftovers</c> ile sunucuda ne kaldığını adıyla
/// sayar. Modül gövdeyi okumadığı için o liste WHMCS tarafında HİÇ görünmüyordu: yönetici hizmeti
/// iptal ediyor, sunucuda kalan siteleri ve veritabanlarını hiç öğrenmiyordu.
/// </para>
/// <para>
/// <b>Neden C# testi PHP'yi çalıştırmıyor:</b> bu depoda WHMCS+PHP yığını yok ve <c>php.exe</c>'nin
/// varlığı makineye göre değişir. "Bulursa çalıştırıp bulamazsa geçen" bir test, ölçemediği hâlde
/// yeşil kalır — bu depoda kapatılan hata sınıfının ta kendisi. Bu yüzden ayrım net: PHP'nin
/// DAVRANIŞI <c>integrations/whmcs/tests/leftover-note.test.php</c> ile ölçülür (elle çalıştırılır,
/// mutasyonla doğrulandı); buradaki testler ise iki şeyi sabitler — (1) modülün silme yolu gövdeyi
/// gerçekten okuyor, (2) panelin ürettiği alan adları modülün beklediği adlarla BİREBİR aynı.
/// İkincisi en sinsi kırılma noktasıdır: C# tarafında bir alan yeniden adlandırılırsa PHP sessizce
/// boş liste görür ve "kalıntı yok" der.
/// </para>
/// </remarks>
public sealed class WhmcsLeftoverReportingTests
{
    [Fact]
    public void Modul_silme_yanitini_HER_IKI_yolda_da_okur()
    {
        var iptal = TerminateAccountGovdesi();

        // Başarı yolu: panel 200 + success:true döner ve siteler bilerek bırakılmış olabilir.
        Assert.Contains("adapanel_recordLeftovers($params, $silme, $decision);", iptal, StringComparison.Ordinal);

        // 409 yolu: yapısal liste HATA zarfında gelir; istisnadan okunuyor.
        Assert.Contains("catch (AdaPanelApiException $exception)", iptal, StringComparison.Ordinal);
        Assert.Contains("adapanel_recordLeftovers($params, $exception->response, $decision);", iptal, StringComparison.Ordinal);
    }

    [Fact]
    public void Modul_notu_UZERINE_YAZMAYAN_uca_yazar()
    {
        // UpdateClientProduct'ın `notes` alanı hizmetin mevcut yönetici notunun ÜZERİNE yazar —
        // başka birinin yazdığı notu sessizce yok ederdi. AddClientNote yeni kayıt EKLER.
        var modul = ModulKaynagi();

        Assert.Contains("'AddClientNote',", modul, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateClientProduct'", modul, StringComparison.Ordinal);
        Assert.DoesNotContain("'UpdateClientProduct',", modul, StringComparison.Ordinal);
    }

    [Fact]
    public void Not_yazilamazsa_SESSIZCE_yutulmaz()
    {
        // WHMCS Local API'nin üçüncü parametresinin (admin adı) boş bırakılabildiği belgede
        // YAZMIYOR. Bu yüzden çağrının sonucu ÖLÇÜLÜR; yazılamayan not, yazılmamış nottan kötüdür
        // çünkü yazıldığı sanılır.
        var modul = ModulKaynagi();

        Assert.Contains("$sonuc['result'] !== 'success'", modul, StringComparison.Ordinal);
        Assert.Contains("TerminateAccount.note_failed", modul, StringComparison.Ordinal);
    }

    [Fact]
    public void Panelin_URETTIGI_alan_adlari_modulun_BEKLEDIGI_adlarla_ayni()
    {
        // Bu, iki dil arasındaki tek sessiz kırılma noktası: C# tarafında bir alan yeniden
        // adlandırılırsa PHP boş liste görür ve "kalıntı yok" der — yani yönetici yine hiçbir şey
        // öğrenmez. Ad listesi ELLE yazılmıyor, DTO gerçekten serileştirilip okunuyor.
        var sonuc = new UserDeletionResultDto
        {
            UserId = Guid.NewGuid(),
            UserName = "musteri1",
            UserDeleted = true,
            SiteRemovalRequested = false,
            DeletedSites = ["eski.com"],
            Leftovers =
            [
                new UserDeletionLeftoverDto
                {
                    SiteName = "ornek.com",
                    Kind = SiteLeftoverKinds.Other,
                    Name = "mysql:ornek_db",
                    Reason = "sahiplik kanitlanamadi",
                    Deliberate = true
                }
            ]
        };

        using var belge = JsonDocument.Parse(JsonSerializer.Serialize(
            sonuc,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var kok = belge.RootElement;

        // Modülün okuduğu üst düzey alanlar.
        foreach (var alan in new[] { "leftovers", "deletedSites", "siteRemovalRequested", "completed", "userName" })
        {
            Assert.True(kok.TryGetProperty(alan, out _), $"Panel yaniti '{alan}' alanini artik uretmiyor; WHMCS modulu onu okuyor.");
        }

        // Modülün kalıntı satırı için okuduğu alanlar.
        var kalinti = kok.GetProperty("leftovers")[0];
        foreach (var alan in new[] { "siteName", "kind", "name", "reason", "deliberate" })
        {
            Assert.True(kalinti.TryGetProperty(alan, out _), $"Kalinti kaydi '{alan}' alanini artik uretmiyor; WHMCS modulu onu okuyor.");
        }

        // "completed" hesaplanan bir özelliktir; serileştirmeden düşerse modül yarım işi
        // eksiksizmiş gibi gösterirdi.
        Assert.True(kok.GetProperty("completed").GetBoolean());
    }

    [Fact]
    public void PHP_davranis_testi_depoda_DURUYOR()
    {
        // PHP tarafının tek gerçek ölçümü o betik. Silinirse bu düzeltme yalnız gözle
        // doğrulanabilir hâle gelir; testin varlığı da denetlenmek zorunda.
        var yol = Path.Combine(KaynakKoku(), "integrations", "whmcs", "tests", "leftover-note.test.php");

        Assert.True(File.Exists(yol), "WHMCS kalinti notu davranis testi kaybolmus: " + yol);

        var betik = File.ReadAllText(yol);
        Assert.Contains("adapanel_recordLeftovers", betik, StringComparison.Ordinal);
        Assert.Contains("function localAPI", betik, StringComparison.Ordinal);
    }

    // --- yardımcılar ---

    private static string ModulKaynagi() =>
        File.ReadAllText(Path.Combine(KaynakKoku(), "integrations", "whmcs", "adapanel", "adapanel.php"));

    /// <summary>Yalnız <c>adapanel_TerminateAccount</c> gövdesi — kaçak eşleşme olmasın.</summary>
    private static string TerminateAccountGovdesi()
    {
        var modul = ModulKaynagi();
        var bas = modul.IndexOf("function adapanel_TerminateAccount", StringComparison.Ordinal);
        Assert.True(bas > 0, "adapanel_TerminateAccount bulunamadi.");

        var son = modul.IndexOf("function adapanel_recordLeftovers", StringComparison.Ordinal);
        Assert.True(son > bas, "adapanel_recordLeftovers bulunamadi.");

        return modul[bas..son];
    }

    private static string KaynakKoku()
    {
        var dizin = new DirectoryInfo(AppContext.BaseDirectory);
        while (dizin is not null && !File.Exists(Path.Combine(dizin.FullName, "AdaPanel.sln")))
        {
            dizin = dizin.Parent;
        }

        Assert.NotNull(dizin);
        return dizin!.FullName;
    }
}
