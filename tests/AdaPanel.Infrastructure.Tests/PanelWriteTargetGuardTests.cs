using AdaPanel.Infrastructure.Security;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// "Panel, HTTP gövdesinden gelen BU mutlak yola yazabilir mi?" kararının davranışı.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden bu kural var:</b> sunucu sahipliği koruması TEK bir varsayıma dayanır —
/// <c>config/server-owner.json</c> panelin HTTP yüzeyinden YAZILAMAZ. Üç uç
/// (<c>/backup/restore</c>, <c>/store/install</c>, <c>/sites</c>) o varsayımı çürütüyordu: gövdeden
/// gelen mutlak bir yol hiç denetlenmeden hedef kabul ediliyor, panel de LocalSystem koştuğu için
/// yazma gerçekleşiyordu.
/// </para>
/// <para>
/// <b>Yollar SABİT DİZGE OLARAK yazılmaz.</b> Bu depoda <c>GetFolderPath</c>'in küçük harf
/// döndürmesi yüzünden sessizce ölmüş üç özellik yaşandı; beklenen değerler bu yüzden testin
/// içinde de ORTAMDAN okunur.
/// </para>
/// </remarks>
public sealed class PanelWriteTargetGuardTests
{
    private static readonly string VeriKoku =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AdaPanel");

    // =====================================================================================
    // A — REDDEDİLMESİ GEREKENLER
    // =====================================================================================

    /// <summary>
    /// SAHİPLİK KAYDININ DURDUĞU KLASÖR: bu tek satır, üç uçtaki yetki yükseltmesinin tamamının
    /// özüdür. Buraya yazabilen bir panel yöneticisi kendini sunucu sahibi ilan eder.
    /// </summary>
    [Fact]
    public void Panel_veri_koku_ve_ALTI_yazma_hedefi_OLAMAZ()
    {
        Assert.Equal(PanelWriteTargetVerdict.SystemPath, PanelWriteTargetGuard.Evaluate(VeriKoku, VeriKoku));
        Assert.Equal(
            PanelWriteTargetVerdict.SystemPath,
            PanelWriteTargetGuard.Evaluate(Path.Combine(VeriKoku, "config"), VeriKoku));
        Assert.Equal(
            PanelWriteTargetVerdict.SystemPath,
            PanelWriteTargetGuard.Evaluate(Path.Combine(VeriKoku, "config", "alt", "daha-alt"), VeriKoku));
    }

    /// <summary>
    /// Veri kökü TAŞINMIŞ olabilir. Koruma kendi kurulum varsayılanını değil, ÇALIŞAN panelin
    /// kökünü korumalı — aksi hâlde taşınmış her kurulumda koruma boş bir klasörü bekler.
    /// </summary>
    [Fact]
    public void Tasinmis_veri_koku_da_korunur()
    {
        const string tasinmis = @"D:\PanelVerisi";

        Assert.Equal(
            PanelWriteTargetVerdict.SystemPath,
            PanelWriteTargetGuard.Evaluate(@"D:\PanelVerisi\config", tasinmis));

        // ...ve KOMŞU klasör korunmaz (ön ek eşleşmesi değil, gerçek kapsama).
        Assert.Equal(
            PanelWriteTargetVerdict.Allowed,
            PanelWriteTargetGuard.Evaluate(@"D:\PanelVerisi-eski\site", tasinmis));
    }

    [Fact]
    public void Isletim_sistemi_klasorleri_yazma_hedefi_OLAMAZ()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        Assert.Equal(PanelWriteTargetVerdict.SystemPath, PanelWriteTargetGuard.Evaluate(windows, VeriKoku));
        Assert.Equal(PanelWriteTargetVerdict.SystemPath, PanelWriteTargetGuard.Evaluate(system32, VeriKoku));
        Assert.Equal(
            PanelWriteTargetVerdict.SystemPath,
            PanelWriteTargetGuard.Evaluate(Path.Combine(system32, "drivers", "etc"), VeriKoku));
        Assert.Equal(PanelWriteTargetVerdict.SystemPath, PanelWriteTargetGuard.Evaluate(programFiles, VeriKoku));
        Assert.Equal(
            PanelWriteTargetVerdict.SystemPath,
            PanelWriteTargetGuard.Evaluate(Path.Combine(programFiles, "herhangi"), VeriKoku));
    }

    /// <summary>
    /// <c>PhysicalPath = C:\</c> ÖLÇÜLEN saldırının kendisiydi: dosya yöneticisinin sanal alanı
    /// sitenin köküdür, yani o site SYSTEM olarak TÜM DİSKİ açardı.
    /// </summary>
    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"D:\")]
    [InlineData(@"C:\..")]
    public void Surucu_kokunun_KENDISI_hedef_olamaz(string yol)
    {
        Assert.Equal(PanelWriteTargetVerdict.DriveRoot, PanelWriteTargetGuard.Evaluate(yol, VeriKoku));
    }

    [Theory]
    [InlineData(@"\\sunucu\pay\klasor")]
    [InlineData(@"\\?\C:\herhangi")]
    public void Ag_paylasimi_ve_aygit_yolu_reddedilir(string yol)
    {
        Assert.Equal(PanelWriteTargetVerdict.NetworkPath, PanelWriteTargetGuard.Evaluate(yol, VeriKoku));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("goreli/klasor")]
    [InlineData(@"..\yukari")]
    public void Bos_ve_goreli_yol_reddedilir(string? yol)
    {
        Assert.Equal(PanelWriteTargetVerdict.InvalidPath, PanelWriteTargetGuard.Evaluate(yol, VeriKoku));
    }

    /// <summary>
    /// Ortak KAPSAYICILARIN kendisi hedef olamaz: oraya yazmak bütün sitelerin ortak kabına
    /// yazmaktır (ve <c>wwwroot</c> ayrıca IIS'in varsayılan sitesinin belge köküdür).
    /// </summary>
    [Fact]
    public void Ortak_kapsayicilarin_KENDISI_hedef_olamaz()
    {
        var surucu = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";

        Assert.Equal(
            PanelWriteTargetVerdict.SystemPath,
            PanelWriteTargetGuard.Evaluate(Path.Combine(surucu, "inetpub"), VeriKoku));
        Assert.Equal(
            PanelWriteTargetVerdict.SystemPath,
            PanelWriteTargetGuard.Evaluate(Path.Combine(surucu, "inetpub", "wwwroot"), VeriKoku));
        Assert.Equal(
            PanelWriteTargetVerdict.SystemPath,
            PanelWriteTargetGuard.Evaluate(Path.Combine(surucu, "Users"), VeriKoku));
    }

    // =====================================================================================
    // B — FAZLA KAPATMA NÖBETÇİLERİ
    //
    // "Her şeyi reddet" çözümü yukarıdaki testlerin HEPSİNİ yeşil gösterir ve paneli işlevsiz
    // bırakır. Aşağıdakiler o çözümü KIRMIZI yakar.
    // =====================================================================================

    /// <summary>
    /// PANELİN EN OLAĞAN HEDEFİ. Kapsayıcının kendisi yasak ama ALTI değil; bu ayrım olmadan
    /// varsayılan kurulum yolundaki her site/kurulum/geri yükleme reddedilirdi.
    /// </summary>
    [Fact]
    public void Varsayilan_site_kokunun_ALTI_kabul_edilir()
    {
        var surucu = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";

        Assert.Equal(
            PanelWriteTargetVerdict.Allowed,
            PanelWriteTargetGuard.Evaluate(Path.Combine(surucu, "inetpub", "wwwroot", "musteri.com"), VeriKoku));
    }

    /// <summary>
    /// YÖNETİCİNİN MEŞRU ALANI: ayrı disk, panelin hiç bilmediği klasörler. Bunları yasaklamak
    /// gerçek bir hosting kurulumunu bozardı ve düzeltmeyi geri aldırırdı.
    /// </summary>
    [Theory]
    [InlineData(@"D:\siteler\musteri.com")]
    [InlineData(@"D:\web")]
    [InlineData(@"E:\geri-yukleme\2026-09")]
    [InlineData(@"D:\uygulamalar\crm")]
    [InlineData(@"C:\Sites\shop")]
    public void Yoneticinin_mesru_hedefleri_KABUL_edilir(string yol)
    {
        Assert.Equal(PanelWriteTargetVerdict.Allowed, PanelWriteTargetGuard.Evaluate(yol, VeriKoku));
    }

    /// <summary>
    /// Ön ek eşleşmesi TEK BAŞINA yetmez: "C:\Windows-yedek" bir sistem klasörü DEĞİLDİR.
    /// Kural düz <c>StartsWith</c> ile yazılsaydı bu meşru klasör de reddedilirdi.
    /// </summary>
    [Fact]
    public void Sistem_klasoru_ADIYLA_BASLAYAN_komsu_klasor_reddedilmez()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        Assert.Equal(PanelWriteTargetVerdict.Allowed, PanelWriteTargetGuard.Evaluate(windows + "-yedek", VeriKoku));
    }
}
