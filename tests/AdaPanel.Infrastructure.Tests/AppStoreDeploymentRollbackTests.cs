using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.AppStore;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Uygulama kurulumunun GERI ALINMASI.
/// <para>
/// Mevcut <c>AppStoreServiceTests</c> yalnizca yapicinin DI'dan kurulup kurulmadigina bakiyordu.
/// Asil tehlike ise buradaydi: geri alma, arsivin her ust duzey girdisini hedefte OZYINELEMELI
/// siliyor (<c>Directory.Delete(recursive: true)</c>) ama yedege yalnizca 5 sabit DOSYA adi
/// aliniyor ve geri koyarken yalnizca ust duzey DOSYALAR kopyalaniyordu. Hedef, sitenin CANLI
/// uygulama koku; yani cakisan bir musteri klasoru kurulum patladiginda kokunden siliniyor ve
/// yedekte olmadigi icin GERI GELMIYORDU.
/// </para>
/// </summary>
public sealed class AppStoreDeploymentRollbackTests
{
    [Fact]
    public void Rollback_RestoresCollidingCustomerDirectory()
    {
        using var alan = new KurulumAlani();

        // Musterinin sitesinde zaten bir "temp" klasoru var (Roundcube'de temp/roundcube.db = posta verisi).
        var hedef = alan.Hedef;
        alan.HedefeYaz("temp", "roundcube.db", "musterinin posta verisi");
        alan.HedefeYaz("index.html", "eski acilis sayfasi");

        // Kurulum paketi de "temp" iceriyor → cakisma.
        alan.KaynagaYaz("temp", ".gitkeep", string.Empty);
        alan.KaynagaYaz("index.php", "<?php // roundcube");

        var durum = AppStoreService.DeployApplication(alan.Kaynak, hedef);

        // Yerlestirme yapildi: paket dosyalari yerinde, eski icerik yana alindi.
        Assert.True(File.Exists(Path.Combine(hedef, "index.php")));
        Assert.False(File.Exists(Path.Combine(hedef, "index.html")));

        // Sonraki adim patlar (ACL / PHP / composer / DB) → geri alma.
        InvokeRestore(durum);

        // ESKI DAVRANIS: temp/ ozyinelemeli silinir, yedekte olmadigi icin geri gelmez.
        Assert.Equal(
            "musterinin posta verisi",
            File.ReadAllText(Path.Combine(hedef, "temp", "roundcube.db")));
        Assert.Equal("eski acilis sayfasi", File.ReadAllText(Path.Combine(hedef, "index.html")));
        Assert.False(File.Exists(Path.Combine(hedef, "index.php")));
    }

    [Fact]
    public void Rollback_DoesNotDeleteUntouchedCustomerDirectories()
    {
        using var alan = new KurulumAlani();

        // App_Data hazirlik adiminda BILEREK yerinde birakilir; paketle cakismadigi surece
        // geri alma da ona DOKUNMAMALI.
        alan.HedefeYaz("App_Data", "musteri.mdf", "veritabani");
        alan.KaynagaYaz("index.php", "<?php");

        var durum = AppStoreService.DeployApplication(alan.Kaynak, alan.Hedef);
        InvokeRestore(durum);

        Assert.Equal("veritabani", File.ReadAllText(Path.Combine(alan.Hedef, "App_Data", "musteri.mdf")));
    }

    [Fact]
    public void PrepareThenRollback_PutsCleanInstallBackupBackInPlace()
    {
        using var alan = new KurulumAlani();

        // "Yedekle + temizle" (cleanInstall) yolu: musterinin butun icerigi kardes klasore tasinir.
        alan.HedefeYaz("wp-content", "uploads.txt", "musteri yuklemeleri");
        alan.HedefeYaz("wp-config.php", "eski yapilandirma");

        var hazirlik = AppStoreService.EnsureLaravelDestinationReady(alan.Hedef, cleanInstall: true);
        Assert.Equal(2, hazirlik.Sidelined.Count);
        Assert.False(File.Exists(Path.Combine(alan.Hedef, "wp-config.php")));

        alan.KaynagaYaz("index.php", "<?php");
        var durum = AppStoreService.DeployApplication(alan.Kaynak, alan.Hedef, hazirlik);

        InvokeRestore(durum);

        // ESKI DAVRANIS: EnsureLaravelDestinationReady'nin donusu IKI CAGIRANDA DA ATILIYORDU;
        // kurulum patlayinca musterinin butun sitesi kardes klasorde OKSUZ kaliyor, site bos
        // kaliyor ve nereye gittigini kimse soylemiyordu.
        Assert.Equal(
            "musteri yuklemeleri",
            File.ReadAllText(Path.Combine(alan.Hedef, "wp-content", "uploads.txt")));
        Assert.Equal("eski yapilandirma", File.ReadAllText(Path.Combine(alan.Hedef, "wp-config.php")));
        Assert.False(File.Exists(Path.Combine(alan.Hedef, "index.php")));

        // Her sey yerine konduysa kardes klasor de ortalikta kalmaz.
        Assert.False(Directory.Exists(hazirlik.SidelineDirectory!));
    }

    [Fact]
    public void EnsureDestinationReady_WithoutCleanInstall_StillRefusesNonEmptySite()
    {
        using var alan = new KurulumAlani();
        alan.HedefeYaz("wp-content", "uploads.txt", "musteri yuklemeleri");

        var hata = Assert.Throws<AppException>(() =>
            AppStoreService.EnsureLaravelDestinationReady(alan.Hedef, cleanInstall: false));

        Assert.Equal("store.site_has_content", hata.MessageKey);
        Assert.True(File.Exists(Path.Combine(alan.Hedef, "wp-content", "uploads.txt")));
    }

    [Fact]
    public void Deploy_WhenExistingEntryCannotBeMovedAside_DoesNotStartOverwriting()
    {
        using var alan = new KurulumAlani();

        alan.HedefeYaz("temp", "kilitli.dat", "musteri verisi");
        alan.KaynagaYaz("temp", "yeni.txt", "paket");
        alan.KaynagaYaz("index.php", "<?php");

        // Klasordeki bir dosyayi acik tutmak Directory.Move'u engeller: "yana alamiyorum" durumu.
        using (var kilit = new FileStream(
                   Path.Combine(alan.Hedef, "temp", "kilitli.dat"),
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.None))
        {
            var hata = Assert.Throws<AppException>(() => AppStoreService.DeployApplication(alan.Kaynak, alan.Hedef, null));
            Assert.Equal("store.preserve_failed", hata.MessageKey);
        }

        // VERIYI KORUYAMIYORSAK UZERINE YAZMAYIZ: paket hicbir sey kopyalamamis olmali.
        Assert.False(File.Exists(Path.Combine(alan.Hedef, "index.php")));
        Assert.Equal("musteri verisi", File.ReadAllText(Path.Combine(alan.Hedef, "temp", "kilitli.dat")));
        Assert.False(File.Exists(Path.Combine(alan.Hedef, "temp", "yeni.txt")));
    }

    /// <summary>Geri alma <c>private static</c>; uretim gorunurlugunu genisletmeden cagrilir.</summary>
    private static void InvokeRestore(AppStoreService.DeploymentState durum)
    {
        var metot = typeof(AppStoreService).GetMethod(
            "RestoreDeployment",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("RestoreDeployment bulunamadi.");

        metot.Invoke(null, [durum, null]);
    }

    private sealed class KurulumAlani : IDisposable
    {
        private readonly string _kok;

        public KurulumAlani()
        {
            _kok = Path.Combine(Path.GetTempPath(), $"adapanel-appstore-{Guid.NewGuid():N}");
            // Hedef, kok ALTINDA olmali: yana alma klasoru hedefe KARDES olarak acilir.
            Hedef = Path.Combine(_kok, "site", "httpdocs");
            Kaynak = Path.Combine(_kok, "staging");
            Directory.CreateDirectory(Hedef);
            Directory.CreateDirectory(Kaynak);
        }

        public string Hedef { get; }

        public string Kaynak { get; }

        public void HedefeYaz(params string[] parcalar) => Yaz(Hedef, parcalar);

        public void KaynagaYaz(params string[] parcalar) => Yaz(Kaynak, parcalar);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_kok, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        /// <summary>Son parca ICERIK, oncekiler yol parcalaridir.</summary>
        private static void Yaz(string kok, string[] parcalar)
        {
            var yol = Path.Combine([kok, .. parcalar[..^1]]);
            Directory.CreateDirectory(Path.GetDirectoryName(yol)!);
            File.WriteAllText(yol, parcalar[^1]);
        }
    }
}
