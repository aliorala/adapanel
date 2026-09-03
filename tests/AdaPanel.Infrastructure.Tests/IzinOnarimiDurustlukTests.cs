using System.Security.AccessControl;
using System.Security.Principal;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Infrastructure.Iis;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// "İzinleri Onar" düğmesi, izin GERÇEKTEN uygulanmadığında BAŞARI DEMEZ.
/// </summary>
/// <remarks>
/// <para>
/// <b>ÖLÇÜLEN AÇIK.</b> <c>SiteProvisioningService.RepairPermissionsAsync</c> koşulsuz site
/// kaydını döndürüyordu. Çağırdığı <c>GrantSiteWritableAsync</c> ise tanımı gereği EN İYİ ÇABA:
/// havuz adı okunamazsa sessizce döner, <c>icacls</c> başarısız olursa yalnız <c>LogWarning</c>
/// yazar, her istisnayı yutar. Yani bu ucun başarısız dönmesi İMKÂNSIZDI —
/// <c>SitesController</c> her çağrıda <c>sites.permissions.repaired</c> denetim kaydı yazıyor,
/// <c>SiteHostingView</c> de dönen değere hiç bakmadan sabit "Yazma izinleri onarıldı"
/// bildirimini gösteriyordu.
/// </para>
/// <para>
/// <b>Bedeli somut:</b> uygulama havuzu hesabı çözülemediğinde <c>icacls</c> hata 1332 ile
/// HİÇBİR ŞEY yapmaz. Müşterinin Laravel <c>storage/</c> ya da WordPress <c>uploads/</c> klasörü
/// yazılamaz kalır, site 500 verir; yönetici onarım düğmesine basıp yeşil onay gördüğü için
/// sebebi BAŞKA YERDE arar. Görünür arıza, panelin gösterdiği yerde değildir.
/// </para>
/// <para>
/// <b>Neden çıkış koduna bakmak yetmez:</b> <c>icacls … /T /C</c> bir alt öğeye erişemese bile
/// <c>/C</c> yüzünden 0 ile çıkar (bu depoda ölçüldü, <c>WebDbClientService</c> içinde yazılı).
/// Üstelik izin verildikten SONRA çalışan kiracı yalıtımı DACL'i baştan yazabilir. Bu yüzden
/// ölçülen şey KOMUT DEĞİL, klasörün SON DURUMUDUR — DACL diskten okunur.
/// </para>
/// <para>
/// Testler gerçek NTFS davranışını ölçer (gerçek klasör, gerçek DACL, gerçek SID çözümlemesi);
/// kaynak metni taramazlar.
/// </para>
/// </remarks>
public sealed class IzinOnarimiDurustlukTests
{
    private static readonly string BuiltinUsersSid =
        new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Value;

    /// <summary>
    /// Test klasörü. SİLİNMEZ — bu turda ajanların hiçbir şeyi silmemesi kuralı geçerli;
    /// klasörler işletim sisteminin geçici dizininde kalır.
    /// </summary>
    private static string YeniKlasor()
    {
        var yol = Path.Combine(Path.GetTempPath(), "adapanel-izin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(yol);
        return yol;
    }

    /// <summary>
    /// Mirası KIRAR ve yalnız verilen kuralları bırakır; böylece üst klasörden sızan bir hak
    /// ölçümü yanıltmaz. Yönetici + SYSTEM hep kalır, yoksa klasör okunamaz hâle gelirdi.
    /// </summary>
    private static void DaclKur(string yol, params FileSystemAccessRule[] kurallar)
    {
        var info = new DirectoryInfo(yol);
        var security = info.GetAccessControl(AccessControlSections.Access);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        foreach (FileSystemAccessRule mevcut in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            security.RemoveAccessRuleSpecific(mevcut);
        }

        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        foreach (var kural in kurallar)
        {
            security.AddAccessRule(kural);
        }

        info.SetAccessControl(security);
    }

    private static FileSystemAccessRule Kural(string sid, FileSystemRights haklar, AccessControlType tur) =>
        new(new SecurityIdentifier(sid),
            haklar,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            tur);

    // ------------------------------------------------------------------------------------------
    // FAZLA KAPATMA NÖBETÇİSİ. Her şey yolundayken panel HÂLÂ sade "yapıldı" demeli.
    //
    // Bu nöbetçi olmadan "hiçbir zaman granted deme" çözümü öteki bütün testleri geçerdi ve her
    // başarılı onarımı şüpheli gösterirdi. Sürekli "doğrulanamadı" diyen bir panel, yalan
    // söyleyenden daha kullanışsız olabilir.
    // ------------------------------------------------------------------------------------------
    [Fact]
    public void Modify_hakki_YERINDEYSE_olcum_sade_GRANTED_der()
    {
        var yol = YeniKlasor();
        DaclKur(yol, Kural(BuiltinUsersSid, FileSystemRights.Modify, AccessControlType.Allow));

        var olcum = FolderWriteAccessProbe.Probe(yol, BuiltinUsersSid);

        Assert.Equal(FolderWriteAccessProbe.Access.Granted, olcum.State);
    }

    /// <summary>FullControl da Modify'ı KAPSAR; onu "yok" saymak yanlış alarm olurdu.</summary>
    [Fact]
    public void FullControl_da_yazma_hakki_SAYILIR()
    {
        var yol = YeniKlasor();
        DaclKur(yol, Kural(BuiltinUsersSid, FileSystemRights.FullControl, AccessControlType.Allow));

        Assert.Equal(FolderWriteAccessProbe.Access.Granted, FolderWriteAccessProbe.Probe(yol, BuiltinUsersSid).State);
    }

    /// <summary>
    /// Hak YOKSA "var" denmez. Arızanın kendisi buydu: komut hiçbir şey yapmamışken panel
    /// onarıldığını söylüyordu.
    /// </summary>
    [Fact]
    public void Modify_hakki_YOKSA_olcum_NOT_GRANTED_der()
    {
        var yol = YeniKlasor();
        // Yalnız OKUMA verilir: "bir kural var" ile "yazabiliyor" ayni sey degildir.
        DaclKur(yol, Kural(BuiltinUsersSid, FileSystemRights.ReadAndExecute, AccessControlType.Allow));

        var olcum = FolderWriteAccessProbe.Probe(yol, BuiltinUsersSid);

        Assert.Equal(FolderWriteAccessProbe.Access.NotGranted, olcum.State);
    }

    /// <summary>Windows'ta REDDETME izne baskındır; izin kuralı olması yetmez.</summary>
    [Fact]
    public void REDDETME_kurali_izne_baskindir()
    {
        var yol = YeniKlasor();
        DaclKur(
            yol,
            Kural(BuiltinUsersSid, FileSystemRights.Modify, AccessControlType.Allow),
            Kural(BuiltinUsersSid, FileSystemRights.Modify, AccessControlType.Deny));

        Assert.Equal(FolderWriteAccessProbe.Access.NotGranted, FolderWriteAccessProbe.Probe(yol, BuiltinUsersSid).State);
    }

    /// <summary>
    /// <c>icacls</c> hata 1332'nin TAM kaynağı: hesap adı çözülemez, komut hiçbir şey uygulamaz.
    /// Bu bir ölçüm eksikliği DEĞİL, ölçülmüş bir yokluktur — "ölçülemedi" demek de yanlış olurdu.
    /// </summary>
    [Fact]
    public void HESAP_YOKSA_olcum_NOT_GRANTED_der_ve_sebebi_YAZAR()
    {
        var yol = YeniKlasor();
        var olmayanHavuz = "IIS AppPool\\adapanel-olmayan-" + Guid.NewGuid().ToString("N");

        var olcum = FolderWriteAccessProbe.Probe(yol, olmayanHavuz);

        Assert.Equal(FolderWriteAccessProbe.Access.NotGranted, olcum.State);
        // Yönetici NE YAPACAĞINI bilmeli: sebep hesabın yokluğu, klasörün hakları değil.
        Assert.Contains("Hesap Windows'ta YOK", olcum.Detail, StringComparison.Ordinal);
        Assert.Contains(olmayanHavuz, olcum.Detail, StringComparison.Ordinal);
    }

    /// <summary>Klasör yoksa hak da yoktur; yol denetim kaydında ADIYLA görünür.</summary>
    [Fact]
    public void KLASOR_YOKSA_olcum_NOT_GRANTED_der()
    {
        var yol = Path.Combine(Path.GetTempPath(), "adapanel-hic-olmayan-" + Guid.NewGuid().ToString("N"));

        var olcum = FolderWriteAccessProbe.Probe(yol, BuiltinUsersSid);

        Assert.Equal(FolderWriteAccessProbe.Access.NotGranted, olcum.State);
        Assert.Contains(yol, olcum.Detail, StringComparison.Ordinal);
    }

    /// <summary>Hesap adı hiç bilinmiyorsa cevap "yok" değil, "ÖLÇÜLEMEDİ"dir.</summary>
    [Fact]
    public void HESAP_ADI_BOSSA_olcum_OLCULEMEDI_der()
    {
        var yol = YeniKlasor();

        Assert.Equal(FolderWriteAccessProbe.Access.Unmeasured, FolderWriteAccessProbe.Probe(yol, string.Empty).State);
        Assert.Equal(FolderWriteAccessProbe.Access.Unmeasured, FolderWriteAccessProbe.Probe(string.Empty, BuiltinUsersSid).State);
    }

    // ------------------------------------------------------------------------------------------
    // ÖLÇÜMÜN KULLANICIYA GİDEN CEVABA ÇEVRİLMESİ. Yalanın durduğu yer tam burasıydı: cevabı
    // taşıyacak bir alan bile yoktu, uç koşulsuz "onarıldı" diyordu.
    // ------------------------------------------------------------------------------------------
    [Theory]
    [InlineData(FolderWriteAccessProbe.Access.Granted, SitePermissionRepairResultDto.StateGranted)]
    [InlineData(FolderWriteAccessProbe.Access.NotGranted, SitePermissionRepairResultDto.StateNotGranted)]
    [InlineData(FolderWriteAccessProbe.Access.Unmeasured, SitePermissionRepairResultDto.StateUnmeasured)]
    internal void OLCUM_UC_DEGERLI_cevaba_AYNEN_cevrilir(FolderWriteAccessProbe.Access olculen, string beklenen)
    {
        var sonuc = SiteProvisioningService.BuildRepairResult(
            new SiteDto { Name = "musteri.test", ApplicationPoolName = "AdaPanel.musteri.test" },
            "IIS AppPool\\AdaPanel.musteri.test",
            new FolderWriteAccessProbe.Result(olculen, "ayrinti"));

        Assert.Equal(beklenen, sonuc.State);
        Assert.Equal(olculen == FolderWriteAccessProbe.Access.Granted, sonuc.IsGranted);
        // Sebep KAYBOLMAZ: denetim kaydı ve ekran bildirimi bunu gösterir.
        Assert.Equal("ayrinti", sonuc.Detail);
    }

    /// <summary>
    /// İzni VEREN ad ile ARANAN ad AYNI KAYNAKTAN gelir.
    /// </summary>
    /// <remarks>
    /// İki yerde iki farklı ad kurulsaydı, verilen hak başka bir hesapta aranırdı: ölçüm
    /// sessizce anlamsızlaşır, panel bu kez "onarılamadı" diye yalan söylerdi. Bu depoda havuz
    /// adının iki taraftan farklı üretilmesi zaten bir kez ölçülmüş bir arızadır
    /// (<see cref="SiteAppPoolNameContractTests"/>).
    /// </remarks>
    [Fact]
    public void HAVUZ_HESABI_tek_kaynaktan_kurulur()
    {
        Assert.Equal(
            "IIS AppPool\\AdaPanel.musteri.test",
            SiteProvisioningService.PoolPrincipal(new SiteDto { ApplicationPoolName = "AdaPanel.musteri.test" }));

        // Havuz adı okunamadıysa hesap UYDURULMAZ; boş ad, "ölçülecek hesap yok" demektir.
        Assert.Equal(string.Empty, SiteProvisioningService.PoolPrincipal(new SiteDto { ApplicationPoolName = "  " }));
    }
}
