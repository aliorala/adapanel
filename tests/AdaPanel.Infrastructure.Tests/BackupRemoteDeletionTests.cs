using System.Net;
using System.Reflection;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Infrastructure.Backup;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// ELLE SILME UZAK KOPYAYI DA SILER.
/// </summary>
/// <remarks>
/// <para>
/// <b>OLCULEN HATA:</b> <c>DeleteBackupAsync</c> ve <c>DeleteBackupChainAsync</c>, uzak silme
/// secenegine <c>null</c> geciyordu. Yani panelden "sil" denince kayit gidiyor, dosya uzak
/// hedefte KALIYORDU. Kayit silindigi icin budama da o dosyaya bir daha ulasamiyordu: kalici
/// YETIM. Kullanici "sildim" saniyor, musteri verisi ucuncu taraf depoda durmaya devam
/// ediyordu (hem gizlilik hem faturalanan yer).
/// </para>
/// <para>
/// Korunan kararlar:
/// (1) elle silme uzak kopyayi da siler ve kimligi OTOMATIK TEMIZLIGIN OKUDUGU AYNI kaynaktan alir,
/// (2) yeni hedefler (dropbox/onedrive/azure) silme kapsaminda,
/// (3) uzak silme basarisizsa KAYIT SILINMEZ — isaretlenir (yetim uretmemek icin),
/// (4) isaretli kayit "taze yedek" SAYILMAZ (budamanin kaynak korumasini yanlis tetiklemesin),
/// (5) tekrar silmeye basmak uzak silmeyi YENIDEN dener,
/// (6) kimlik bilgisi duz metin saklanmaz; korunmus degerden cozulur.
/// </para>
/// </remarks>
public sealed class BackupRemoteDeletionTests
{
    // =======================================================================================
    // 1) UZAK KOPYA GERCEKTEN SILINIYOR
    // =======================================================================================

    [Theory]
    [InlineData("dropbox", "dropbox:/yedekler/2026-08-01/sites/shop/x.zip", "POST", "https://api.dropboxapi.com/2/files/delete_v2")]
    [InlineData("onedrive", "onedrive:/yedekler/2026-08-01/sites/shop/x.zip", "DELETE", "https://graph.microsoft.com/v1.0/me/drive/root:/yedekler/2026-08-01/sites/shop/x.zip")]
    [InlineData("webdav", "https://bulut.ornek.com/dav/2026-08-01/sites/shop/x.zip", "DELETE", "https://bulut.ornek.com/dav/2026-08-01/sites/shop/x.zip")]
    public async Task Elle_silme_UZAK_kopyayi_da_siler(string tur, string uzakAdres, string yontem, string beklenenUc)
    {
        using var alan = new BulutAlani();
        var bulut = new SahteBulut { VarsayilanDurum = HttpStatusCode.NoContent };
        // Kullanici adi BOS: Dropbox/OneDrive'da bu, "parola alani dogrudan erisim jetonudur"
        // demektir (jeton tazeleme yolu ayri bir testte olculuyor).
        await alan.UzakHedefiYazAsync(tur, "/yedekler", string.Empty, SahteKoruyucu.Onek + "gizli-jeton");

        var kayit = KayitYaz(alan, "k1", tur, uzakAdres);
        await alan.KayitlariYazAsync(kayit);

        var servis = alan.ServisKur(bulut);
        await servis.DeleteBackupAsync("k1", alan.Iptal);

        // (a) Uzak silme GERCEKTEN atildi ve DOGRU ucca gitti.
        var istek = Assert.Single(bulut.Istekler);
        Assert.Equal(yontem, istek.Method);
        Assert.Equal(beklenenUc, istek.Url);

        // (b) Kayit da gitti.
        Assert.Empty(await servis.GetBackupsAsync(alan.Iptal));

        // (c) Yerel dosya da gitti.
        Assert.False(File.Exists(kayit.Path));
    }

    [Fact]
    public async Task Silme_kimligi_KORUNMUS_degerden_cozulur_duz_metin_saklanmaz()
    {
        using var alan = new BulutAlani();
        var bulut = new SahteBulut { VarsayilanDurum = HttpStatusCode.NoContent };

        // Diskteki deger KORUNMUS hâlde; duz metin hicbir yerde durmuyor.
        await alan.UzakHedefiYazAsync("dropbox", "/yedekler", string.Empty, SahteKoruyucu.Onek + "GERCEK-JETON");

        var diskteki = await File.ReadAllTextAsync(
            alan.DosyaDeposu.ResolvePath("config/automation.json"), alan.Iptal);
        Assert.DoesNotContain("\"GERCEK-JETON\"", diskteki, StringComparison.Ordinal);

        await alan.KayitlariYazAsync(KayitYaz(alan, "k1", "dropbox", "dropbox:/yedekler/x.zip"));
        await alan.ServisKur(bulut).DeleteBackupAsync("k1", alan.Iptal);

        // Silme isteginde COZULMUS jeton kullanildi.
        Assert.Equal("Bearer GERCEK-JETON", Assert.Single(bulut.Istekler).Authorization);
    }

    [Fact]
    public void Uzak_silme_kapsami_TEK_kaynaktan_gelir_ve_yeni_hedefleri_icerir()
    {
        // Eskiden kapsam listesi ("s3"/"ftp"/"webdav"/"http") silme yolunda ELLE yaziliydi;
        // yeni bir hedef eklendiginde guncellenmesi unutulursa o hedefte HER silme bir yetim
        // uretirdi. Liste artik tektir.
        foreach (var tur in new[] { "ftp", "s3", "webdav", "http", "dropbox", "onedrive", "azure" })
        {
            Assert.True(BackupService.SupportsRemoteDeletion(tur), $"{tur} uzak silme kapsaminda olmali.");
        }

        Assert.False(BackupService.SupportsRemoteDeletion("local"));
        Assert.False(BackupService.SupportsRemoteDeletion(""));
    }

    // =======================================================================================
    // 2) UZAK SILME BASARISIZSA KAYIT SILINMEZ (yetim uretmemek)
    // =======================================================================================

    [Fact]
    public async Task Uzak_silme_COKERSE_kayit_SILINMEZ_isaretlenir()
    {
        using var alan = new BulutAlani();
        var bulut = new SahteBulut { VarsayilanDurum = HttpStatusCode.Forbidden, VarsayilanGovde = "erisim reddedildi" };
        await alan.UzakHedefiYazAsync("dropbox", "/yedekler", string.Empty, SahteKoruyucu.Onek + "jeton");

        var kayit = KayitYaz(alan, "k1", "dropbox", "dropbox:/yedekler/x.zip");
        await alan.KayitlariYazAsync(kayit);

        var servis = alan.ServisKur(bulut);

        // SESSIZ "tamam" YOK: kullanici uzak kopyanin kaldigini ogrenir.
        var hata = await Assert.ThrowsAsync<AppException>(() => servis.DeleteBackupAsync("k1", alan.Iptal));
        Assert.Equal("backup.remote_delete_failed", hata.MessageKey);

        // KAYIT DURUYOR: silinseydi uzak dosya bir daha ne gorulur ne silinirdi.
        var kalan = Assert.Single(await servis.GetBackupsAsync(alan.Iptal));
        Assert.Equal("deleted_remote_failed", kalan.Status);
        Assert.Equal("dropbox:/yedekler/x.zip", kalan.RemoteUri);

        // Sebep kayitta duruyor.
        var uyari = Assert.Single(kalan.Warnings, item => item.MessageKey == "backup.remote_delete_failed");
        Assert.Contains("403", uyari.Detail, StringComparison.Ordinal);

        // Yerel dosya YINE DE silindi (kullanicinin istedigi buydu) ve kayit bunu soyluyor.
        Assert.False(File.Exists(kayit.Path));
        Assert.True(kalan.LocalMissing || string.IsNullOrEmpty(kalan.Path));
    }

    [Fact]
    public void Isaretli_kayit_TAZE_YEDEK_sayilmaz()
    {
        // Budamanin "kaynak korumasi": bir kaynagin guncel yedegi yoksa eskisine dokunulmaz.
        // Yerelden silinmis (yalniz uzak kopyasi takilmis) bir kaydi taze yedek saymak, panelin
        // GERCEK eski yedekleri silmesine yol acardi.
        var isaretli = new BackupRecordDto
        {
            Id = "x",
            Status = "deleted_remote_failed",
            RemoteUri = "dropbox:/yedekler/x.zip",
            TargetType = "dropbox"
        };

        var kullanilabilir = (Func<BackupRecordDto, bool>)Delegate.CreateDelegate(
            typeof(Func<BackupRecordDto, bool>),
            typeof(BackupService).GetMethod("IsUsableBackup", BindingFlags.NonPublic | BindingFlags.Static)!);

        Assert.False(kullanilabilir(isaretli));

        // Karsi kontrol: normal bir uzak kayit HÂLÂ kullanilabilir sayilir (davranis daralmadi).
        Assert.True(kullanilabilir(new BackupRecordDto { Id = "y", RemoteUri = "dropbox:/y.zip", Status = "created_remote_synced" }));
    }

    [Fact]
    public async Task Tekrar_silmek_uzak_silmeyi_YENIDEN_dener()
    {
        using var alan = new BulutAlani();
        var bulut = new SahteBulut { VarsayilanDurum = HttpStatusCode.Forbidden };
        await alan.UzakHedefiYazAsync("dropbox", "/yedekler", string.Empty, SahteKoruyucu.Onek + "jeton");
        await alan.KayitlariYazAsync(KayitYaz(alan, "k1", "dropbox", "dropbox:/yedekler/x.zip"));

        var servis = alan.ServisKur(bulut);
        await Assert.ThrowsAsync<AppException>(() => servis.DeleteBackupAsync("k1", alan.Iptal));

        // Hedef duzeldi.
        var ikinciBulut = new SahteBulut { VarsayilanDurum = HttpStatusCode.OK };
        var ikinciServis = alan.ServisKur(ikinciBulut);
        await ikinciServis.DeleteBackupAsync("k1", alan.Iptal);

        Assert.Single(ikinciBulut.Istekler);
        Assert.Empty(await ikinciServis.GetBackupsAsync(alan.Iptal));
    }

    [Fact]
    public async Task Kayitli_uzak_hedef_YOKSA_sessizce_yetim_birakilmaz()
    {
        // Kullanici hedefi yerele cevirmis olabilir. Uzak kopyasi olan bir kaydi bu durumda
        // sessizce silmek, dosyayi hedefte sonsuza kadar birakirdi.
        using var alan = new BulutAlani();
        var bulut = new SahteBulut();
        await alan.UzakHedefiYazAsync("local", string.Empty, string.Empty, string.Empty);
        await alan.KayitlariYazAsync(KayitYaz(alan, "k1", "dropbox", "dropbox:/yedekler/x.zip"));

        var servis = alan.ServisKur(bulut);
        var hata = await Assert.ThrowsAsync<AppException>(() => servis.DeleteBackupAsync("k1", alan.Iptal));

        Assert.Equal("backup.remote_delete_failed", hata.MessageKey);
        Assert.Single(await servis.GetBackupsAsync(alan.Iptal));
        Assert.Empty(bulut.Istekler);
    }

    [Fact]
    public async Task Uzak_kopyasi_OLMAYAN_kayit_normalde_silinir()
    {
        // Karsi kontrol: yeni davranis, sıradan yerel yedeklerin silinmesini ZORLASTIRMAMALI.
        using var alan = new BulutAlani();
        var bulut = new SahteBulut();
        var kayit = KayitYaz(alan, "k1", "local", string.Empty);
        await alan.KayitlariYazAsync(kayit);

        var servis = alan.ServisKur(bulut);
        await servis.DeleteBackupAsync("k1", alan.Iptal);

        Assert.Empty(await servis.GetBackupsAsync(alan.Iptal));
        Assert.False(File.Exists(kayit.Path));
        Assert.Empty(bulut.Istekler);
    }

    // =======================================================================================
    // 3) ZINCIR SILME
    // =======================================================================================

    [Fact]
    public async Task Zincir_silme_her_halkanin_UZAK_kopyasini_siler()
    {
        using var alan = new BulutAlani();
        var bulut = new SahteBulut { VarsayilanDurum = HttpStatusCode.NoContent };
        await alan.UzakHedefiYazAsync("dropbox", "/yedekler", string.Empty, SahteKoruyucu.Onek + "jeton");

        var tam = KayitYaz(alan, "tam", "dropbox", "dropbox:/yedekler/tam.zip");
        tam.ChainId = "z1";
        tam.ChainIndex = 0;

        var halka = KayitYaz(alan, "halka", "dropbox", "dropbox:/yedekler/halka.zip");
        halka.ChainId = "z1";
        halka.ChainIndex = 1;
        halka.BackupMode = "incremental";
        halka.BaseBackupId = "tam";

        await alan.KayitlariYazAsync(tam, halka);

        var servis = alan.ServisKur(bulut);
        var sonuc = await servis.DeleteBackupChainAsync("tam", alan.Iptal);

        Assert.Equal(2, sonuc.DeletedIds.Length);
        Assert.Equal(2, bulut.Istekler.Count);
        Assert.Empty(await servis.GetBackupsAsync(alan.Iptal));
    }

    // =======================================================================================
    // 4) KIMLIK KAYNAGININ SURUKLENME ALARMI
    // =======================================================================================

    [Fact]
    public void Otomasyon_ayar_alan_adlari_okuyucuyla_AYNI_kalmali()
    {
        // BackupRemoteCredentialReader, AutomationService'in yazdigi dosyayi okur (ters yonde
        // bagimlilik DI dongusu olurdu). Adlar orada degisirse okuyucu SESSIZCE bos kimlik
        // dondurur ve hata "uzak dosya silinmiyor" olarak COK GEC fark edilirdi. Bu test o
        // sessiz sapmayi derleme sonrasi ilk kosuda yakalar.
        var durumTipi = typeof(AdaPanel.Infrastructure.Automation.AutomationService)
            .GetNestedType("AutomationStateRecord", BindingFlags.NonPublic);

        Assert.NotNull(durumTipi);

        foreach (var alanAdi in new[]
                 {
                     "BackupTargetType",
                     "BackupRemoteUrl",
                     "BackupRemoteUserName",
                     "BackupRemoteRegion",
                     "BackupRemotePasswordProtected"
                 })
        {
            Assert.True(
                durumTipi!.GetProperty(alanAdi) is not null,
                $"AutomationStateRecord.{alanAdi} kayboldu; BackupRemoteCredentialReader sessizce bos kimlik dondurur.");
        }
    }

    // =======================================================================================
    // Yardimcilar
    // =======================================================================================

    private static BackupRecordDto KayitYaz(BulutAlani alan, string id, string tur, string uzakAdres)
    {
        var yol = Path.Combine(alan.Kok, $"{id}.zip");
        File.WriteAllText(yol, "yedek icerigi");
        return new BackupRecordDto
        {
            Id = id,
            Name = $"{id}.zip",
            Type = "site",
            Source = BulutAlani.Site,
            Path = yol,
            Format = "zip",
            CreatedAtUtc = DateTime.UtcNow,
            TargetType = tur,
            RemoteUri = uzakAdres,
            Status = string.IsNullOrEmpty(uzakAdres) ? "created" : "created_remote_synced"
        };
    }
}
