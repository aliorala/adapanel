using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Yedek geri yüklemesinde ZAMANLANMIŞ GÖREV içe aktarımının iki koruması.
/// </summary>
/// <remarks>
/// <para>
/// Geri yüklenen arşiv GÜVENİLMEZ GİRDİDİR: içindeki <c>tasks/index.json</c>'u paketi hazırlayan
/// kim ise o yazmıştır. Panel o dosyadaki her girdi için bir yol kurup okuyordu.
/// </para>
/// <para>
/// İki açık ölçüldü. <b>Birincisi kapsam:</b> <c>item.Entry</c> denetimsiz
/// <c>Path.Combine</c>'a giriyordu, yani <c>..\..\Windows\...</c> yazmak çıkarma klasörünün
/// DIŞINA çıkardı. <b>İkincisi büyüme:</b> okunan XML <c>Unicode.GetBytes</c> (karakter başına
/// 2 bayt) + <c>ToBase64String</c> (4/3) ile ~2,67 katına çıkıp bir de betik dizesine gömülüyordu,
/// üstelik bu bir DÖNGÜ ve girdi sayısını da arşiv belirliyordu.
/// </para>
/// <para>
/// Testler üretim kodunu çağırmıyor; korunan KURALI sabitliyor. Sebebi ölçüldü: içe aktarım
/// <c>Register-ScheduledTask</c> çalıştırır, yani gerçek çağrı bu makinede zamanlanmış görev
/// oluşturmayı dener — bir birim testinin yapmaması gereken şey. Bu yüzden burada kuralın
/// KENDİSİ (kapsama ve tavan) ölçülür, ve ayrıca kaynakta o kuralın çağrıldığı sabitlenir.
/// </para>
/// </remarks>
public sealed class BackupTaskImportGuardTests : IDisposable
{
    private readonly string kok = Path.Combine(Path.GetTempPath(), "adapanel-tasksec-" + Guid.NewGuid().ToString("N"));

    public BackupTaskImportGuardTests() => Directory.CreateDirectory(Path.Combine(kok, "tasks"));

    public void Dispose()
    {
        try { Directory.Delete(kok, recursive: true); } catch { /* temizlik zorunlu değil */ }
    }

    [Theory]
    [InlineData("../../../Windows/win.ini")]
    [InlineData("..\\..\\gizli.xml")]
    [InlineData("tasks/../../disari.xml")]
    public void Cikarma_klasorunun_disina_cikan_girdi_reddedilir(string girdi)
    {
        var hedef = Path.GetFullPath(Path.Combine(kok, girdi.Replace('/', Path.DirectorySeparatorChar)));

        Assert.False(AdaPanel.Core.Common.PathContainment.IsInsideRoot(kok, hedef));
    }

    [Theory]
    [InlineData("tasks/gorev.xml")]
    [InlineData("tasks/alt/gorev.xml")]
    public void Mesru_girdi_kabul_edilir(string girdi)
    {
        // FAZLA KAPATMA NÖBETÇİSİ: yalnız "kaçış engellendi" ölçmek, HER girdiyi reddeden bir
        // düzeltmeyi de yeşil gösterirdi -- o düzeltme görev geri yüklemesini tümüyle öldürürdü.
        var hedef = Path.GetFullPath(Path.Combine(kok, girdi.Replace('/', Path.DirectorySeparatorChar)));

        Assert.True(AdaPanel.Core.Common.PathContainment.IsInsideRoot(kok, hedef));
    }

    /// <summary>
    /// Kaynakta korumaların GERÇEKTEN çağrıldığı sabitlenir.
    /// </summary>
    /// <remarks>
    /// Bu depoda "koruma yazılmış ama o yolda uygulanmıyor" tekrar eden hata sınıfıdır; bu
    /// oturumda PHP izolasyonu tam bu şekilde ölü bulundu (kural vardı, varsayılan yol onu hiç
    /// çağırmıyordu). Yasak kelimeye değil, çağrının VARLIĞINA konur.
    /// </remarks>
    [Fact]
    public void Gorev_ice_aktarimi_kapsam_ve_tavan_denetimini_cagiriyor()
    {
        var kaynak = File.ReadAllText(Path.Combine(KaynakKoku(), "src", "AdaPanel.Infrastructure", "Backup", "BackupService.cs"));

        var bolum = kaynak[kaynak.IndexOf("tasks\", \"index.json\"", StringComparison.Ordinal)..];
        bolum = bolum[..bolum.IndexOf("RunPowerShellStrictAsync", StringComparison.Ordinal)];

        Assert.Contains("PathContainment.IsInsideRoot(extractedRoot", bolum, StringComparison.Ordinal);
        Assert.Contains("EnBuyukGorevXmlBaytI", bolum, StringComparison.Ordinal);
        Assert.Contains("backup.restore_task_too_large", bolum, StringComparison.Ordinal);
    }

    /// <summary>
    /// Tavan, gerçek bir görev XML'ini engellemeyecek kadar cömert olmalı.
    /// </summary>
    /// <remarks>
    /// Gerçek bir <c>Export-ScheduledTask</c> çıktısı birkaç KB'dır. Tavanı bir gün "sıkılaştırmak"
    /// isteyen biri onu KB'lara indirirse meşru geri yüklemeler sessizce yarım kalır; bu test o
    /// yönü tutar.
    /// </remarks>
    [Fact]
    public void Tavan_gercek_bir_gorev_xmlini_engellemeyecek_kadar_genis()
    {
        var kaynak = File.ReadAllText(Path.Combine(KaynakKoku(), "src", "AdaPanel.Infrastructure", "Backup", "BackupService.cs"));

        Assert.Contains("const long EnBuyukGorevXmlBaytI = 1024L * 1024L;", kaynak, StringComparison.Ordinal);
    }

    private static string KaynakKoku()
    {
        var yol = AppContext.BaseDirectory;
        while (yol is not null && !File.Exists(Path.Combine(yol, "AdaPanel.sln")))
        {
            yol = Path.GetDirectoryName(yol);
        }

        Assert.NotNull(yol);
        return yol!;
    }
}
