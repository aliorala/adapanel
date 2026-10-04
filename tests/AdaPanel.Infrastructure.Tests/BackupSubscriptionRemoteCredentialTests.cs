using System.Net;
using System.Reflection;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Infrastructure.Backup;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// ABONELIK YEDEGININ UZAK KOPYASI DA SILINEBILIR.
/// </summary>
/// <remarks>
/// <para>
/// <b>OLCULEN HATA:</b> abonelik yedekleme programi kendi uzak hedefini ve kimligini KENDI
/// dosyasinda saklar (<c>config/subscription-backups.json</c>), ama silme/dogrulama yolundaki
/// okuyucu YALNIZ <c>config/automation.json</c>'a bakiyordu. Sonuc: abonelik yedeginin uzak
/// kopyasi HICBIR ZAMAN silinemiyordu. Eski "sildim" yalani kapanmisti; yerine gelen durust
/// <c>deleted_remote_failed</c> durumu CALISMIYORDU — kayit her denemede yeniden isaretleniyor,
/// dosya hedefte birikiyordu (hem gizlilik hem faturalanan yer).
/// </para>
/// <para>
/// Korunan kararlar:
/// (1) kimlik KAYIT BASINA cozulur: kaydi URETEN programin kimligi kullanilir,
/// (2) abonelik programi yoksa/yerele cevrilmisse sunucu geneli kimlige DUSULUR (sessizce
///     vazgecilmez),
/// (3) sunucu geneli budama da abonelik kayitlarini dogru kimlikle siler,
/// (4) alan adlari abonelik servisiyle AYNI kalir (sessiz surukleme alarmi).
/// </para>
/// </remarks>
public sealed class BackupSubscriptionRemoteCredentialTests
{
    private const string AbonelikKimligi = "8f14e45fceea167a5a36dedd4bea2543";

    // =======================================================================================
    // 1) ABONELIK YEDEGI KENDI KIMLIGIYLE SILINIR
    // =======================================================================================

    [Fact]
    public async Task Abonelik_yedeginin_uzak_kopyasi_ABONELIK_kimligiyle_silinir()
    {
        using var alan = new BulutAlani();
        var bulut = new SahteBulut { VarsayilanDurum = HttpStatusCode.NoContent };

        // Sunucu geneli hedef YEREL: otomasyonun uzak kimligi YOK. Eskiden bu, abonelik
        // yedeginin uzak kopyasinin ASLA silinememesi demekti.
        await alan.UzakHedefiYazAsync("local", string.Empty, string.Empty, string.Empty);
        await AbonelikProgramiYazAsync(alan, "dropbox", SahteKoruyucu.Onek + "ABONELIK-JETONU");

        var kayit = AbonelikKaydi(alan, "a1");
        await alan.KayitlariYazAsync(kayit);

        var servis = alan.ServisKur(bulut);
        await servis.DeleteBackupAsync("a1", alan.Iptal);

        // (a) Silme GERCEKTEN atildi ve ABONELIGIN jetonuyla gitti.
        var istek = Assert.Single(bulut.Istekler);
        Assert.Equal("Bearer ABONELIK-JETONU", istek.Authorization);

        // (b) Kayit da, yerel dosya da gitti: yetim kalmadi.
        Assert.Empty(await servis.GetBackupsAsync(alan.Iptal));
        Assert.False(File.Exists(kayit.Path));
    }

    [Fact]
    public async Task Sunucu_geneli_budama_da_abonelik_kaydini_DOGRU_kimlikle_siler()
    {
        // Gecelik otomasyon budamasi bir aboneligin eski yedegine de dokunur; kimligi otomasyon
        // ayarlarindan alsaydi her turda "uzak kopya silinemedi" isaretleyip birakirdi.
        using var alan = new BulutAlani();
        var bulut = new SahteBulut { VarsayilanDurum = HttpStatusCode.NoContent };

        await alan.UzakHedefiYazAsync("local", string.Empty, string.Empty, string.Empty);
        await AbonelikProgramiYazAsync(alan, "dropbox", SahteKoruyucu.Onek + "ABONELIK-JETONU");

        var eski = AbonelikKaydi(alan, "eski", gunOnce: 40);
        var taze = AbonelikKaydi(alan, "taze");
        await alan.KayitlariYazAsync(eski, taze);

        var servis = alan.ServisKur(bulut);
        var sonuc = await servis.PruneAsync(14, cancellationToken: alan.Iptal);

        Assert.Equal(1, sonuc.Removed);
        Assert.Equal(0, sonuc.RemoteDeleteFailed);
        Assert.Equal("Bearer ABONELIK-JETONU", Assert.Single(bulut.Istekler).Authorization);
    }

    [Fact]
    public async Task Abonelik_programi_YOKSA_sunucu_geneli_kimlige_dusulur()
    {
        // Kullanici abonelik programini silmis ya da yerele cevirmis olabilir; bilinen tek kimlik
        // sunucu genelidir. Denemek ve sonucu SOYLEMEK, hic denememekten iyidir.
        using var alan = new BulutAlani();
        var bulut = new SahteBulut { VarsayilanDurum = HttpStatusCode.NoContent };

        await alan.UzakHedefiYazAsync("dropbox", "/yedekler", string.Empty, SahteKoruyucu.Onek + "SUNUCU-JETONU");

        var kayit = AbonelikKaydi(alan, "a2");
        await alan.KayitlariYazAsync(kayit);

        var servis = alan.ServisKur(bulut);
        await servis.DeleteBackupAsync("a2", alan.Iptal);

        Assert.Equal("Bearer SUNUCU-JETONU", Assert.Single(bulut.Istekler).Authorization);
        Assert.Empty(await servis.GetBackupsAsync(alan.Iptal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Abonelik_kimligi_SITE_yedegini_ETKILEMEZ(bool measuredOwner)
    {
        // KAPSAM: abonelik kimligi yalnizca O ABONELIGE ait kayitlarda kullanilir. Aksi halde bir
        // musterinin jetonu baska bir kaydin silinmesinde kullanilirdi.
        using var alan = new BulutAlani();
        var bulut = new SahteBulut { VarsayilanDurum = HttpStatusCode.NoContent };

        await alan.UzakHedefiYazAsync("dropbox", "/yedekler", string.Empty, SahteKoruyucu.Onek + "SUNUCU-JETONU");
        await AbonelikProgramiYazAsync(alan, "dropbox", SahteKoruyucu.Onek + "ABONELIK-JETONU");

        var siteKaydi = AbonelikKaydi(alan, "s1");
        siteKaydi.Type = "site";
        // The durable owner authorizes access; it does not turn a manually created SITE
        // archive into a subscription scheduler archive with different credentials.
        if (!measuredOwner) siteKaydi.SubscriptionUserId = string.Empty;
        await alan.KayitlariYazAsync(siteKaydi);

        var servis = alan.ServisKur(bulut);
        await servis.DeleteBackupAsync("s1", alan.Iptal);

        Assert.Equal("Bearer SUNUCU-JETONU", Assert.Single(bulut.Istekler).Authorization);
    }

    // =======================================================================================
    // 2) KIMLIK KAYNAGININ SURUKLENME ALARMI
    // =======================================================================================

    [Fact]
    public void Abonelik_programi_alan_adlari_ve_DOSYA_YOLU_okuyucuyla_AYNI_kalmali()
    {
        // BackupRemoteCredentialReader, SubscriptionBackupScheduleService'in yazdigi dosyayi okur
        // (ters yonde bagimlilik DI dongusu olurdu). Ad ya da yol orada degisirse okuyucu SESSIZCE
        // sunucu geneli kimlige duser ve hata "abonelik yedegi uzaktan silinmiyor" olarak COK GEC
        // fark edilirdi.
        var servisTipi = typeof(AdaPanel.Infrastructure.Subscriptions.SubscriptionBackupScheduleService);

        var yol = servisTipi
            .GetField("StatePath", BindingFlags.NonPublic | BindingFlags.Static)
            ?.GetRawConstantValue() as string;

        Assert.Equal(BackupRemoteCredentialReader.SubscriptionStatePath, yol);

        var kayitTipi = servisTipi.GetNestedType("ScheduleRecord", BindingFlags.NonPublic);
        Assert.NotNull(kayitTipi);

        foreach (var alanAdi in new[]
                 {
                     "UserId",
                     "TargetType",
                     "RemoteUserName",
                     "RemoteRegion",
                     "RemotePasswordProtected"
                 })
        {
            Assert.True(
                kayitTipi!.GetProperty(alanAdi) is not null,
                $"ScheduleRecord.{alanAdi} kayboldu; BackupRemoteCredentialReader abonelik kimligini bulamaz.");
        }
    }

    // =======================================================================================
    // Yardimcilar
    // =======================================================================================

    /// <summary>
    /// Abonelik yedek programini diske yazar. Alan adlari GERCEK <c>ScheduleRecord</c> adlaridir;
    /// ayrilirlarsa yukaridaki surukleme alarmi kizarir.
    /// </summary>
    private static Task AbonelikProgramiYazAsync(BulutAlani alan, string tur, string korunmusParola) =>
        alan.DosyaDeposu.WriteAsync(
            BackupRemoteCredentialReader.SubscriptionStatePath,
            new[]
            {
                new Dictionary<string, object>
                {
                    ["userId"] = AbonelikKimligi,
                    ["enabled"] = true,
                    ["targetType"] = tur,
                    ["remoteUrl"] = "/abonelik-yedekleri",
                    ["remoteUserName"] = string.Empty,
                    ["remoteRegion"] = string.Empty,
                    ["remotePasswordProtected"] = korunmusParola
                }
            },
            alan.Iptal);

    private static BackupRecordDto AbonelikKaydi(BulutAlani alan, string id, int gunOnce = 0)
    {
        var yol = Path.Combine(alan.Kok, $"{id}.zip");
        File.WriteAllText(yol, "abonelik yedegi");
        return new BackupRecordDto
        {
            Id = id,
            Name = $"{id}.zip",
            Type = "subscription",
            Source = AbonelikKimligi,
            SubscriptionUserId = AbonelikKimligi,
            Path = yol,
            Format = "zip",
            CreatedAtUtc = DateTime.UtcNow.AddDays(-gunOnce),
            TargetType = "dropbox",
            RemoteUri = $"dropbox:/abonelik-yedekleri/{id}.zip",
            Status = "created_remote_synced"
        };
    }
}
