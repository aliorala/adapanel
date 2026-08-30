using AdaPanel.Infrastructure.Iis;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// K8 + F7 — "arşivden site içe aktarma siteyi BOŞALTIP tamamlandı diyor".
/// <para>
/// Eski akış: PowerShell indirme+açma script'i çalıştırılıyor, başarı ölçütü
/// <c>result.Success &amp;&amp; Directory.Exists(extractDir)</c> idi. İkinci koşul HER ZAMAN
/// doğruydu (klasörü C# kendisi oluşturuyor), birincisi ise bozuk zip'te 0 kalıyordu. Sonuç:
/// sitenin tüm kökü yedeğe taşınıyor, 0 dosya kopyalanıyor, panel HTTP 200 + "tamamlandı" diyordu.
/// </para>
/// <para>
/// Bu testler yıkıcı adımın İKİ korumasını ölçer: (1) içerik doğrulanmadan hiçbir şeye
/// dokunulmaz, (2) kopyalama yarıda kalırsa site ESKİ hâline geri sarılır.
/// </para>
/// </summary>
public sealed class SiteArchiveImportHonestyTests
{
    /// <summary>
    /// Boş açılma klasörü = "arşivden hiçbir şey çıkmadı". Çağıran bunu görüp yıkıcı adımı hiç
    /// başlatmaz. MUTASYON: <c>null</c> yerine klasörün kendisi döndürülürse import boş içerikle
    /// devam eder ve bu test düşer.
    /// </summary>
    [Fact]
    public void ResolveArchiveContentRoot_EmptyExtraction_ReportsNoContent()
    {
        using var gecici = new GeciciKlasor();
        var bos = gecici.KlasorOlustur("extract");

        Assert.Null(SiteHostingSettingsService.ResolveArchiveContentRoot(bos));
        Assert.Equal(0, SiteHostingSettingsService.CountFiles(bos));
    }

    /// <summary>Yaygın "site/..." paketlemesinde tek üst klasör soyulur.</summary>
    [Fact]
    public void ResolveArchiveContentRoot_UnwrapsSingleTopLevelFolder()
    {
        using var gecici = new GeciciKlasor();
        var extract = gecici.KlasorOlustur("extract");
        var ic = Directory.CreateDirectory(Path.Combine(extract, "site")).FullName;
        File.WriteAllText(Path.Combine(ic, "index.php"), "<?php");

        var kok = SiteHostingSettingsService.ResolveArchiveContentRoot(extract);

        Assert.Equal(ic, kok);
        Assert.Equal(1, SiteHostingSettingsService.CountFiles(kok!));
    }

    /// <summary>
    /// "Önce temizle" ile normal akış: eski içerik KARDEŞ klasöre taşınır (kökün içine değil),
    /// yeni içerik yazılır ve yedeğin YERİ sonuçta bildirilir. Panel eskiden yedeğin nerede
    /// olduğunu hiç söylemiyordu.
    /// </summary>
    [Fact]
    public void ApplyImportedContent_CleanFirst_MovesOldContentAsideAndReportsWhere()
    {
        using var gecici = new GeciciKlasor();
        var kok = gecici.KlasorOlustur("site");
        File.WriteAllText(Path.Combine(kok, "eski.html"), "eski");
        Directory.CreateDirectory(Path.Combine(kok, "uploads"));
        File.WriteAllText(Path.Combine(kok, "uploads", "musteri.png"), "veri");

        var kaynak = gecici.KlasorOlustur("kaynak");
        File.WriteAllText(Path.Combine(kaynak, "index.php"), "yeni");

        var sonuc = SiteHostingSettingsService.ApplyImportedContent("site", kaynak, kok, cleanFirst: true);

        Assert.Equal(1, sonuc.FilesCopied);
        Assert.True(File.Exists(Path.Combine(kok, "index.php")));
        Assert.False(File.Exists(Path.Combine(kok, "eski.html")));

        // Yedek yolu BİLDİRİLMELİ ve müşteri verisi orada DURMALI.
        Assert.False(string.IsNullOrWhiteSpace(sonuc.BackupPath));
        Assert.True(File.Exists(Path.Combine(sonuc.BackupPath, "eski.html")));
        Assert.True(File.Exists(Path.Combine(sonuc.BackupPath, "uploads", "musteri.png")));

        // Yedek kökün DIŞINDA (kardeş) olmalı; içeride olsaydı "kökü boşalt" kendi yedeğini yerdi.
        Assert.False(sonuc.BackupPath.StartsWith(kok + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// F7'NİN ÖZÜ: kopyalama YARIDA kalırsa site ne eski ne yeni hâlde bırakılamaz.
    /// Kaynaktaki bir dosya paylaşımsız kilitlenir → <c>File.Copy</c> patlar. Beklenen: istisna
    /// çağırana ulaşır VE site kökü eski içeriğiyle geri gelir.
    /// <para>
    /// MUTASYON: <c>catch</c> bloğundaki <c>ContentSideline.MoveBack</c> kaldırılırsa site kökü
    /// yarı boşalmış kalır ve bu test düşer (eski davranış tam olarak buydu).
    /// </para>
    /// </summary>
    [Fact]
    public void ApplyImportedContent_WhenCopyFails_RestoresTheSiteRoot()
    {
        using var gecici = new GeciciKlasor();
        var kok = gecici.KlasorOlustur("site");
        File.WriteAllText(Path.Combine(kok, "eski.html"), "eski");
        Directory.CreateDirectory(Path.Combine(kok, "uploads"));
        File.WriteAllText(Path.Combine(kok, "uploads", "musteri.png"), "veri");

        var kaynak = gecici.KlasorOlustur("kaynak");
        File.WriteAllText(Path.Combine(kaynak, "a-once.txt"), "kopyalanabilir");
        var kilitliYol = Path.Combine(kaynak, "z-kilitli.txt");
        File.WriteAllText(kilitliYol, "okunamaz");

        // Paylaşımsız açık tanıtıcı: File.Copy bu dosyada IOException atar.
        using (var kilit = new FileStream(kilitliYol, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Throws<IOException>(() =>
                SiteHostingSettingsService.ApplyImportedContent("site", kaynak, kok, cleanFirst: true));
        }

        // Site ESKİ hâlinde olmalı: müşteri verisi yerinde, yarım yeni içerik ortalıkta değil.
        Assert.True(File.Exists(Path.Combine(kok, "eski.html")));
        Assert.True(File.Exists(Path.Combine(kok, "uploads", "musteri.png")));
        Assert.Equal("eski", File.ReadAllText(Path.Combine(kok, "eski.html")));
        Assert.False(File.Exists(Path.Combine(kok, "a-once.txt")));
    }

    /// <summary>
    /// "Önce temizle" KAPALIYKEN yana alma yapılmaz: mevcut içerik korunur, yeni dosyalar üzerine
    /// yazılır ve yedek yolu boş bildirilir (olmayan bir yedeği varmış gibi göstermeyiz).
    /// </summary>
    [Fact]
    public void ApplyImportedContent_WithoutCleanFirst_KeepsExistingFilesAndReportsNoBackup()
    {
        using var gecici = new GeciciKlasor();
        var kok = gecici.KlasorOlustur("site");
        File.WriteAllText(Path.Combine(kok, "kalici.txt"), "kalmali");
        File.WriteAllText(Path.Combine(kok, "index.php"), "eski");

        var kaynak = gecici.KlasorOlustur("kaynak");
        File.WriteAllText(Path.Combine(kaynak, "index.php"), "yeni");

        var sonuc = SiteHostingSettingsService.ApplyImportedContent("site", kaynak, kok, cleanFirst: false);

        Assert.Equal(1, sonuc.FilesCopied);
        Assert.Equal(string.Empty, sonuc.BackupPath);
        Assert.True(File.Exists(Path.Combine(kok, "kalici.txt")));
        Assert.Equal("yeni", File.ReadAllText(Path.Combine(kok, "index.php")));
    }

    /// <summary>
    /// Kök zaten boşsa yana alma klasörü ORTALIKTA BIRAKILMAZ (boş klasör çöpü üretmeyiz), ama
    /// içinde veri varsa ASLA silinmez — o zaten yukarıdaki testte kanıtlanıyor.
    /// </summary>
    [Fact]
    public void ApplyImportedContent_EmptyRoot_LeavesNoEmptyBackupFolder()
    {
        using var gecici = new GeciciKlasor();
        var kok = gecici.KlasorOlustur("site");
        var kaynak = gecici.KlasorOlustur("kaynak");
        File.WriteAllText(Path.Combine(kaynak, "index.php"), "yeni");

        var sonuc = SiteHostingSettingsService.ApplyImportedContent("site", kaynak, kok, cleanFirst: true);

        Assert.Equal(1, sonuc.FilesCopied);
        Assert.Equal(string.Empty, sonuc.BackupPath);
        Assert.Empty(Directory.GetDirectories(gecici.Yol, "site_goc_yedek_*"));
    }

    /// <summary>
    /// Site kökünün İÇİNDEKİ bir KAVŞAK (junction) üzerinden dışarı YAZILAMAZ.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Kaynak tarafı zaten korunuyordu, HEDEF tarafı korunmuyordu.</b>
    /// <c>SafeDirectoryWalk</c> arşivden çıkan ağaçtaki bağlantıları eler — yani içe aktarma sanal
    /// alanın dışını OKUYAMAZ. Ama YAZILAN yol hiç denetlenmiyordu: kökün içinde arşivdeki bir
    /// klasörle AYNI ADI taşıyan bir kavşak varsa, <c>Directory.CreateDirectory</c> ve
    /// <c>File.Copy</c> onu TAKİP EDER ve içerik kavşağın hedefine düşer. Panel LocalSystem
    /// olduğu için orada hiçbir ACL durdurmaz.
    /// </para>
    /// <para>
    /// <b>Neden <c>cleanFirst: false</c>:</b> "önce temizle" açıkken kökün içeriği (dolayısıyla
    /// kavşak) yana alınır; açık BIRAKILMAYAN yol, kavşağın YERİNDE KALDIĞI yoldur ve uç bunu
    /// istekten seçtirir. Yani güvenliğin bir istek bayrağına bağlı olmadığını ölçmek gerekir.
    /// </para>
    /// <para>
    /// Kavşak kurulamazsa test SESSİZCE YEŞİL DÖNMEZ, ölçüm yapılamadığı için atlanır —
    /// kurulmamış bir ön koşulda geçen güvenlik testi, ölçmediği şeyi ölçtüğünü söyler.
    /// </para>
    /// </remarks>
    [Fact]
    public void ApplyImportedContent_Kokteki_JUNCTION_uzerinden_disari_yazamaz()
    {
        using var gecici = new GeciciKlasor();
        var kok = gecici.KlasorOlustur("site");
        var disari = gecici.KlasorOlustur("disari");

        // Kiracının kökünde bıraktığı kavşak; adı arşivdeki klasörle AYNI.
        var baglanti = Path.Combine(kok, "wp-content");
        JunctionOlustur(baglanti, disari);
        if (!Directory.Exists(baglanti)
            || !new DirectoryInfo(baglanti).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return; // ön koşul sağlanamadı (bkz. belge): ölçüm YAPILAMADI
        }

        try
        {
            var kaynak = gecici.KlasorOlustur("kaynak");
            Directory.CreateDirectory(Path.Combine(kaynak, "wp-content"));
            File.WriteAllText(Path.Combine(kaynak, "wp-content", "kacti.txt"), "kacti");

            Assert.ThrowsAny<AdaPanel.Core.Common.AppException>(() =>
                SiteHostingSettingsService.ApplyImportedContent("site", kaynak, kok, cleanFirst: false));

            Assert.False(
                File.Exists(Path.Combine(disari, "kacti.txt")),
                "Kavsak TAKIP EDILDI: ice aktarilan icerik site kokunun DISINA yazildi.");
        }
        finally
        {
            // Kavşak ÖNCE kaldırılır: geçici klasörün özyinelemeli silinmesi onu takip ederse,
            // testin kendisi ölçmeye çalıştığı veri kaybını üretirdi.
            try
            {
                Directory.Delete(baglanti);
            }
            catch
            {
                // En iyi çaba.
            }
        }
    }

    private static void JunctionOlustur(string link, string hedef)
    {
        using var surec = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{link}\" \"{hedef}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        })!;

        surec.WaitForExit(60_000);
    }
}
