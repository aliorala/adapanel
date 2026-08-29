using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Site klasörüne yazma izni verilirken kullanılan UYGULAMA HAVUZU ADI, IIS'te GERÇEKTEN kurulan
/// havuzun adı olmalı — panelin yeniden TÜRETTİĞİ bir ad değil.
/// </summary>
/// <remarks>
/// <para>
/// <b>ÖLÇÜLMÜŞ ARIZA.</b> <c>SiteProvisioningService.GrantSiteWritableAsync</c> bir
/// <c>applicationPoolName</c> parametresi alıyor ve boş gelirse <c>site.Name</c>'e düşüyordu.
/// Oluşturma yolu ona <c>request.ApplicationPoolName</c> veriyordu; o alan panelden gelen HER
/// istekte boş. Yani havuz adı her zaman <c>site.Name</c> (örn. <c>musteri.com</c>) oluyordu.
/// IIS'teki gerçek havuz ise <c>WebAdministrationSiteService.BuildManagedPoolName</c> ile
/// <c>AdaPanel.</c> önekli kuruluyor (<c>AdaPanel.musteri.com</c>). <c>icacls</c> var olmayan bir
/// hesap için hata 1332 döndürüp HİÇBİR ŞEY yapmaz: panelden açılan hiçbir sitede oluşturma
/// anındaki Modify izni gerçekten verilmiyordu.
/// </para>
/// <para>
/// <b>Bu test neden KAYNAK METNİNE bakıyor:</b> gerçek davranış <c>icacls.exe</c> çağrısında ve o
/// çağrı <c>ProcessRunner</c>'ın STATİK metodundan geçiyor — araya girilemiyor. Alternatif, testin
/// gerçekten <c>icacls</c> çalıştırması olurdu: bu makinede o havuzlar yok, sonuç her hâlükârda
/// başarısız döner ve test doğru ile yanlış uygulamayı AYIRT EDEMEZDİ. Kırılganlığı bilerek kabul
/// ediyoruz; ölçtüğü şey (yasak geri dönüşün geri gelmemesi) kırılganlığından değerli.
/// Aynı gerekçe <c>PackagingPipelineWiringTests</c> sınıf yorumunda da yazılı.
/// </para>
/// </remarks>
public sealed class SiteAppPoolNameContractTests
{
    private static string Kaynak(params string[] parcalar) =>
        File.ReadAllText(ScriptTestHost.ScriptPath(parcalar));

    private static string Saglayici() =>
        Kaynak("src", "AdaPanel.Infrastructure", "Iis", "SiteProvisioningService.cs");

    /// <summary>
    /// Havuz adı <c>site.Name</c>'e DÜŞMEMELİ.
    /// </summary>
    /// <remarks>
    /// Arızanın tam kendisi bu geri dönüştü. Geri gelirse panelden açılan her sitede izin sessizce
    /// uygulanmaz ve belirtisi ancak Laravel/WordPress 500 hatası olarak görünür.
    /// </remarks>
    [Fact]
    public void HAVUZ_ADI_site_adina_DUSMEZ()
    {
        var kaynak = Saglayici();

        // Geri dönüşün KENDİSİ.
        Assert.DoesNotContain("? site.Name : applicationPoolName", kaynak);

        // Parametrenin KENDİSİ de kalkmalı: dursaydı çağıran taraf aynı boş değeri yeniden
        // geçirebilirdi. DİKKAT — sade "applicationPoolName" araması BU DOSYADAKİ AÇIKLAMA
        // METİNLERİNE de takılır (ölçüldü: ilk hâli tam bu yüzden kırmızı yandı); bu yüzden
        // aranan şey kod içindeki PARAMETRE BİLDİRİMİDİR.
        Assert.DoesNotContain("string applicationPoolName", kaynak);
    }

    /// <summary>
    /// Havuz adı IIS'ten OKUNAN değerden gelmeli.
    /// </summary>
    /// <remarks>
    /// Bir önceki test tek başına yetmez: yasak ifadeyi silip yerine BAŞKA bir türetme
    /// (ör. <c>"AdaPanel." + site.Name</c>) yazmak da onu geçerdi ve aynı sınıftan bir arıza olurdu.
    /// Ölçülen şey "ad NEREDEN geliyor": <c>site.ApplicationPoolName</c>, yani IIS'ten taze okunan
    /// <c>SiteDto</c>'nun alanı.
    /// </remarks>
    [Fact]
    public void HAVUZ_ADI_IIS_ten_OKUNAN_alandan_gelir()
    {
        var kaynak = Saglayici();

        Assert.Contains("var pool = site.ApplicationPoolName;", kaynak);

        // Havuz adını ELDE kurmak (önek birleştirme) bu dosyada YASAK: önekin tek kaynağı
        // WebAdministrationSiteService.BuildManagedPoolName'dir.
        Assert.DoesNotContain("\"AdaPanel.\" + site.Name", kaynak);
    }

    /// <summary>
    /// Havuz adı okunamıyorsa izin UYDURULMAZ, atlanır ve söylenir.
    /// </summary>
    /// <remarks>
    /// "Bilmiyorum" ile "şu ad" arasındaki farkı silmek, düzeltilen arızanın ta kendisiydi.
    /// </remarks>
    [Fact]
    public void HAVUZ_ADI_OKUNAMAZSA_izin_UYDURULMAZ()
    {
        var kaynak = Saglayici();

        Assert.Contains("Site yazma izni ATLANDI", kaynak);
    }

    /// <summary>
    /// Havuzu KURAN taraf hâlâ tek kaynaktan önek veriyor.
    /// </summary>
    /// <remarks>
    /// Sözleşmenin öbür ucu: yazma izni IIS'ten okunan adı kullanıyor olsa bile, havuzu kuran
    /// tarafın adı tek bir yerden üretmesi gerekir. İki uç da ölçülmezse "sözleşme" tek taraflı
    /// bir temenniye dönüşür.
    /// </remarks>
    [Fact]
    public void HAVUZ_ADINI_URETEN_TEK_YER_vardir()
    {
        var kaynak = Kaynak("src", "AdaPanel.Infrastructure", "Iis", "WebAdministrationSiteService.cs");

        Assert.Contains("ManagedPoolPrefix + AppPoolNamePattern().Replace(siteName, \"-\")", kaynak);
    }
}
