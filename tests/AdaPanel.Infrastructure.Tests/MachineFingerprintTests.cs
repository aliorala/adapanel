using AdaPanel.Infrastructure.Licensing;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Makine parmak izinin AYNI MAKİNEDE KARARLI olduğunu doğrular.
/// </summary>
/// <remarks>
/// Kararsız bir parmak izi, lisanslamanın en pahalı hatasıdır: müşteri hiçbir şey yapmadığı hâlde
/// lisansı "başka makineye taşınmış" görünür, panel kısıtlanır ve her seferinde destek çağrısı
/// doğar. Bu yüzden kararlılık teste bağlanır, "herhalde kararlıdır"a bırakılmaz.
/// </remarks>
public sealed class MachineFingerprintTests : IDisposable
{
    private readonly string dataRoot = Path.Combine(
        Path.GetTempPath(),
        "adapanel-fingerprint-tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
        catch
        {
            // Temizlik en iyi çaba.
        }
    }

    [Fact]
    public void Ayni_makinede_YENIDEN_baslatilsa_bile_ayni_parmak_izi_uretilir()
    {
        // İki AYRI sağlayıcı örneği = panelin yeniden başlatılması. Her ikisi de kayıt defterini
        // ve diskteki kurulum kimliğini SIFIRDAN okur; sonuç değişmemeli.
        var birinci = Saglayici().Get();
        var ikinci = Saglayici().Get();

        Assert.Equal(birinci, ikinci);
        Assert.Equal(64, birinci.Length);
        Assert.Matches("^[0-9a-f]{64}$", birinci);
    }

    [Fact]
    public void Kurulum_kimligi_BIR_KEZ_uretilir_ve_diskte_kalir()
    {
        var saglayici = Saglayici();
        var parmakIzi = saglayici.Get();

        var yol = Path.Combine(dataRoot, "config", "license", "install-id");
        Assert.True(File.Exists(yol));
        var kimlik = File.ReadAllText(yol).Trim();
        Assert.False(string.IsNullOrWhiteSpace(kimlik));

        // Dosya YERİNDE dururken yeni sağlayıcı aynı kimliği okumalı (yenisini üretmemeli).
        Assert.Equal(parmakIzi, Saglayici().Get());
        Assert.Equal(kimlik, File.ReadAllText(yol).Trim());
    }

    [Fact]
    public void Bu_makinede_parmak_izi_KAYIT_DEFTERINDEN_turetilir()
    {
        // Ölçüm: bu makinede HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid okunabiliyor.
        // Kaynak "installid-only"ye düşerse parmak izi makineye değil yalnız veri klasörüne
        // bağlanmış demektir — bunu sessizce kabul etmek istemiyoruz.
        Assert.Equal("machineguid+installid", Saglayici().GetSource());
    }

    [Fact]
    public void Farkli_kurulum_kimlikleri_farkli_parmak_izi_uretir()
    {
        // Sysprep'lenmemiş görüntüden çoğaltılan iki sunucuda MachineGuid AYNI olabilir;
        // kurulum kimliği onları ayırır.
        var a = MachineFingerprint.Compute("997b466f-22ec-4ab8-8652-5399038c1c7a", "kurulum-a");
        var b = MachineFingerprint.Compute("997b466f-22ec-4ab8-8652-5399038c1c7a", "kurulum-b");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Buyuk_kucuk_harf_ve_suslu_parantez_farki_parmak_izini_DEGISTIRMEZ()
    {
        // Kayıt defteri ve WMI aynı GUID'i farklı biçimlerde döner. Normalize edilmezse parmak izi
        // yalnızca OKUMA YOLU değiştiği için kayardı.
        var duz = MachineFingerprint.Compute("997b466f-22ec-4ab8-8652-5399038c1c7a", "k1");
        var suslu = MachineFingerprint.Compute("{997B466F-22EC-4AB8-8652-5399038C1C7A}", "k1");

        Assert.Equal(duz, suslu);
    }

    [Fact]
    public void Doldurulmamis_uretici_degerleri_KIMLIK_SAYILMAZ()
    {
        // Bu makinede gerçekten ölçülen değerler. Kimlik sayılsalardı aynı anakart modelini
        // kullanan BÜTÜN sunucular tek parmak izine düşerdi.
        Assert.False(MachineFingerprint.IsUsableIdentity("To Be Filled By O.E.M"));
        Assert.False(MachineFingerprint.IsUsableIdentity("00000000-0000-0000-0000-000000000000"));
        Assert.False(MachineFingerprint.IsUsableIdentity("ffffffff-ffff-ffff-ffff-ffffffffffff"));
        Assert.False(MachineFingerprint.IsUsableIdentity("Default string"));
        Assert.False(MachineFingerprint.IsUsableIdentity(null));
        Assert.False(MachineFingerprint.IsUsableIdentity("   "));

        Assert.True(MachineFingerprint.IsUsableIdentity("997b466f-22ec-4ab8-8652-5399038c1c7a"));
    }

    [Fact]
    public void Parmak_izi_ham_makine_kimligini_ICERMEZ()
    {
        // Sunucuya gönderilen değer geri döndürülemez olmalı (gizlilik sözü).
        var makine = "997b466f-22ec-4ab8-8652-5399038c1c7a";
        var izi = MachineFingerprint.Compute(makine, "kurulum-1");

        Assert.DoesNotContain("997b466f", izi, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("kurulum-1", izi, StringComparison.OrdinalIgnoreCase);
    }

    private MachineFingerprintProvider Saglayici()
    {
        var runtime = Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = dataRoot });
        return new MachineFingerprintProvider(new JsonFileStore(runtime));
    }
}
