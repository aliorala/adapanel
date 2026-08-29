using System.Net;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Infrastructure.Automation;
using AdaPanel.Infrastructure.Backup;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// "UZAK KOPYA ZATEN YOKTU" HER HEDEFTE AYNI ANLAMA GELMELI.
/// </summary>
/// <remarks>
/// <para>
/// <b>OLCULEN HATA (canli sunucu):</b> kullanici yedek silerken su mesaji aliyordu —
/// <c>"Yerel kopya silindi, uzak kopya silinemedi (ftp): The remote server returned an error:
/// (550) File unavailable (e.g., file not found, no access)."</c> Bu cumlenin KENDISI sorunun
/// parcasi: RFC 959'da 550 HEM "dosya yok" HEM "erisim yok" demektir. Kullanici ne yapacagini
/// bilemez ve kayit listede cozulemeyen bir uyariyla kalir.
/// </para>
/// <para>
/// <b>ASIL TUTARSIZLIK:</b> ayni anlam ("uzak kopya zaten yok") HTTP'de 404/410 ile BASARI,
/// S3'te idempotent DELETE ile BASARI, FTP'de ise HATA sayiliyordu. Uc hedef, uc davranis.
/// </para>
/// <para>
/// <b>IKINCI OLCULEN HATA:</b> S3 ve WebDAV/HTTP silme yollari HTTP yanitini HIC OKUMUYORDU.
/// <c>403 AccessDenied</c> bile sessizce "sildim" sayiliyor, kayit kapaniyor ve dosya hedefte
/// YETIM kaliyordu — panel onu bir daha ne gorebilir ne silebilirdi.
/// </para>
/// <para>
/// <b>DURUSTLUK NOTU:</b> buradaki FTP ve HTTP yuzeyleri SAHTEDIR; hicbir sokete dokunulmaz.
/// Olculen sey PANELIN KARARIDIR ("550 gelince ne yapiyor"), gercek bir FTP sunucusunun
/// davranisi degil.
/// </para>
/// </remarks>
public sealed class BackupRemoteDeleteHonestyTests
{
    private const string FtpAdres = "ftp://yedek.ornek.com/yedekler/2026-08-01/sites/shop/x.zip";

    // =======================================================================================
    // 1) 550'NIN UC HALI
    // =======================================================================================

    [Fact]
    public async Task Ftp_550_ve_dosya_listede_YOKSA_silme_BASARILI_sayilir_kayit_KAPANIR()
    {
        using var alan = new BulutAlani();
        var ftp = new SahteFtp
        {
            SilmeYaniti = FtpCommandResult.Fail(550, "550 File unavailable"),
            // Ust dizin listelendi, hedef ad ORADA DEGIL: uzak kopya zaten gitmis.
            ListeYaniti = new FtpListingResult(true, ["baska.zip", "eski.zip"], 226, "226 OK")
        };

        var servis = await FtpServisiKurAsync(alan, ftp);
        var kayit = Assert.Single(await servis.GetBackupsAsync(alan.Iptal));

        // ISTISNA YOK: silinecek bir sey kalmamis, amac gerceklesmis.
        await servis.DeleteBackupAsync(kayit.Id, alan.Iptal);

        Assert.Empty(await servis.GetBackupsAsync(alan.Iptal));
        Assert.Contains(ftp.Komutlar, komut => komut.StartsWith("NLST", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ftp_550_ve_dosya_listede_VARSA_DURUYOR_denir_ve_kayit_isaretlenir()
    {
        using var alan = new BulutAlani();
        var ftp = new SahteFtp
        {
            SilmeYaniti = FtpCommandResult.Fail(550, "550 Permission denied"),
            ListeYaniti = new FtpListingResult(true, ["x.zip"], 226, "226 OK")
        };

        var servis = await FtpServisiKurAsync(alan, ftp);
        var kayit = Assert.Single(await servis.GetBackupsAsync(alan.Iptal));

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.DeleteBackupAsync(kayit.Id, alan.Iptal));

        // Kullaniciya HANGI DURUM oldugu soylenir: dosya duruyor, silinemedi.
        Assert.Equal("backup.remote_delete_failed", hata.MessageKey);
        Assert.Contains("remote_presence=present", hata.Details, StringComparison.Ordinal);
        Assert.Contains("cause=permission_or_lock", hata.Details, StringComparison.Ordinal);
        // Adres de var ki kullanici nereye bakacagini bilsin.
        Assert.Contains("ftp://yedek.ornek.com/yedekler/2026-08-01/sites/shop/x.zip", hata.Details, StringComparison.Ordinal);

        var kalan = Assert.Single(await servis.GetBackupsAsync(alan.Iptal));
        Assert.Equal("deleted_remote_failed", kalan.Status);
    }

    [Fact]
    public async Task Ftp_550_ve_LISTELEME_de_basarisizsa_BELIRLENEMEDI_denir_SILDIM_DENMEZ()
    {
        using var alan = new BulutAlani();
        var ftp = new SahteFtp
        {
            SilmeYaniti = FtpCommandResult.Fail(550, "550 File unavailable"),
            // Listeleme de takildi: dosyanin orada olup olmadigini OLCEMEDIK.
            ListeYaniti = new FtpListingResult(false, [], 550, "550 Access denied")
        };

        var servis = await FtpServisiKurAsync(alan, ftp);
        var kayit = Assert.Single(await servis.GetBackupsAsync(alan.Iptal));

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.DeleteBackupAsync(kayit.Id, alan.Iptal));

        // "OLCEMEDIM" ile "YOK" AYNI SEY DEGILDIR: kayit kapatilmaz.
        Assert.Contains("remote_presence=unknown", hata.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("remote_presence=missing", hata.Details, StringComparison.Ordinal);
        Assert.Single(await servis.GetBackupsAsync(alan.Iptal));
    }

    [Fact]
    public async Task Ftp_550_DISINDAKI_hata_yoklama_YAPILMADAN_bilinmiyor_der()
    {
        using var alan = new BulutAlani();
        var ftp = new SahteFtp
        {
            // 530: oturum acilamadi. Dosyanin durumu hakkinda hicbir sey soylemez.
            SilmeYaniti = FtpCommandResult.Fail(530, "530 Login authentication failed")
        };

        var servis = await FtpServisiKurAsync(alan, ftp);
        var kayit = Assert.Single(await servis.GetBackupsAsync(alan.Iptal));

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.DeleteBackupAsync(kayit.Id, alan.Iptal));

        Assert.Contains("ftp_delete=530", hata.Details, StringComparison.Ordinal);
        Assert.Contains("probe=skipped", hata.Details, StringComparison.Ordinal);
        // Bosuna listeleme istegi ATILMAZ.
        Assert.DoesNotContain(ftp.Komutlar, komut => komut.StartsWith("NLST", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ftp_silme_GECERSE_hicbir_yoklama_yapilmaz()
    {
        using var alan = new BulutAlani();
        var ftp = new SahteFtp();

        var servis = await FtpServisiKurAsync(alan, ftp);
        var kayit = Assert.Single(await servis.GetBackupsAsync(alan.Iptal));

        await servis.DeleteBackupAsync(kayit.Id, alan.Iptal);

        Assert.Single(ftp.Komutlar);
        Assert.Empty(await servis.GetBackupsAsync(alan.Iptal));
    }

    [Theory]
    // Kimi sunucu NLST'de TAM YOL doner, kimi duz ad; ikisi de "dosya duruyor" demektir.
    [InlineData("/yedekler/2026-08-01/sites/shop/x.zip")]
    [InlineData("x.zip")]
    // Windows FTP sunuculari buyuk/kucuk harf ayirmaz; "yok" demek icin EMIN olmak gerekir.
    [InlineData("X.ZIP")]
    public async Task Ftp_listesindeki_ad_GENIS_eslesir_supheliyken_DURUYOR_denir(string listedeki)
    {
        using var alan = new BulutAlani();
        var ftp = new SahteFtp
        {
            SilmeYaniti = FtpCommandResult.Fail(550, "550 File unavailable"),
            ListeYaniti = new FtpListingResult(true, [listedeki], 226, "226 OK")
        };

        var servis = await FtpServisiKurAsync(alan, ftp);
        var kayit = Assert.Single(await servis.GetBackupsAsync(alan.Iptal));

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.DeleteBackupAsync(kayit.Id, alan.Iptal));
        Assert.Contains("remote_presence=present", hata.Details, StringComparison.Ordinal);
    }

    // =======================================================================================
    // 2) BUDAMA (gece isi) AYNI YOLU KULLANIR
    // =======================================================================================

    [Fact]
    public async Task Budama_da_zaten_yok_durumunu_BASARI_sayar_ve_kaydi_kapatir()
    {
        using var alan = new BulutAlani();
        var ftp = new SahteFtp
        {
            SilmeYaniti = FtpCommandResult.Fail(550, "550 File unavailable"),
            ListeYaniti = new FtpListingResult(true, [], 226, "226 OK")
        };

        var eski = FtpKaydi(alan, "eski", gunOnce: 30);
        var taze = FtpKaydi(alan, "taze", gunOnce: 1);
        await alan.KayitlariYazAsync(eski, taze);

        var servis = alan.ServisKur(new SahteBulut(), ftp: ftp);
        var sonuc = await servis.PruneAsync(14, FtpKimligi(), alan.Iptal);

        Assert.Equal(1, sonuc.Removed);
        Assert.Equal(0, sonuc.RemoteDeleteFailed);
        Assert.False(sonuc.HasFailures);
        Assert.Single(await servis.GetBackupsAsync(alan.Iptal));
    }

    [Fact]
    public async Task Budama_silinemeyen_uzak_kopyayi_CAGIRANA_bildirir()
    {
        using var alan = new BulutAlani();
        var ftp = new SahteFtp
        {
            SilmeYaniti = FtpCommandResult.Fail(550, "550 Permission denied"),
            ListeYaniti = new FtpListingResult(true, ["eski.zip"], 226, "226 OK")
        };

        var eski = FtpKaydi(alan, "eski", gunOnce: 30);
        var taze = FtpKaydi(alan, "taze", gunOnce: 1);
        await alan.KayitlariYazAsync(eski, taze);

        var servis = alan.ServisKur(new SahteBulut(), ftp: ftp);
        var sonuc = await servis.PruneAsync(14, FtpKimligi(), alan.Iptal);

        // OLCULEN HATA: budama yalniz BASARI SAYISI donuyordu; cagiran (gece isi) basarisizligi
        // hic ogrenemiyor, durumunu "completed" birakiyordu.
        Assert.Equal(0, sonuc.Removed);
        Assert.Equal(1, sonuc.RemoteDeleteFailed);
        Assert.True(sonuc.HasFailures);
        Assert.Contains(sonuc.Failures, satir => satir.Contains("remote_delete_failed", StringComparison.Ordinal));

        // Kayit SILINMEZ, isaretlenir (yetim uretmemek icin) ve LISTEDE GORUNUR bir alani olur.
        var isaretli = Assert.Single(
            await servis.GetBackupsAsync(alan.Iptal), kayit => kayit.Id == "eski");
        Assert.True(isaretli.RemoteDeleteFailed);
    }

    [Fact]
    public async Task Isaretli_kayit_SONRAKI_budamada_YENIDEN_denenir()
    {
        // MarkRemoteDeleteFailedAsync'in kendi yorumu "bir sonraki budamada YENIDEN denenir" diyor.
        // Planlayici isaretli kaydi artik secmeseydi dosya KALICI yetim olurdu; bu test o iddiayi
        // olcer.
        using var alan = new BulutAlani();
        var basarisiz = new SahteFtp
        {
            SilmeYaniti = FtpCommandResult.Fail(550, "550 Permission denied"),
            ListeYaniti = new FtpListingResult(true, ["eski.zip"], 226, "226 OK")
        };

        await alan.KayitlariYazAsync(FtpKaydi(alan, "eski", gunOnce: 30), FtpKaydi(alan, "taze", gunOnce: 1));
        var ilkTur = await alan.ServisKur(new SahteBulut(), ftp: basarisiz).PruneAsync(14, FtpKimligi(), alan.Iptal);
        Assert.Equal(1, ilkTur.RemoteDeleteFailed);

        // Hedef duzeldi: ikinci turda ayni kayit yeniden denenmeli ve KAPANMALI.
        var duzelen = new SahteFtp();
        var ikinciServis = alan.ServisKur(new SahteBulut(), ftp: duzelen);
        var ikinciTur = await ikinciServis.PruneAsync(14, FtpKimligi(), alan.Iptal);

        Assert.Equal(1, ikinciTur.Removed);
        Assert.Contains(duzelen.Komutlar, komut => komut.StartsWith("DELE", StringComparison.Ordinal));
        Assert.DoesNotContain(await ikinciServis.GetBackupsAsync(alan.Iptal), kayit => kayit.Id == "eski");
    }

    // =======================================================================================
    // 3) "ZATEN YOK" TABLOSU: BUTUN HEDEFLER AYNI ANLAMA GELIR
    // =======================================================================================

    [Theory]
    [InlineData("webdav", "https://bulut.ornek.com/dav/x.zip", HttpStatusCode.NotFound, "{}")]
    [InlineData("webdav", "https://bulut.ornek.com/dav/x.zip", HttpStatusCode.Gone, "{}")]
    [InlineData("onedrive", "onedrive:/yedekler/x.zip", HttpStatusCode.NotFound, "{}")]
    [InlineData("dropbox", "dropbox:/yedekler/x.zip", HttpStatusCode.Conflict, "{\"error_summary\":\"path_lookup/not_found/\"}")]
    public async Task Zaten_yok_durumu_her_hedefte_BASARIDIR(
        string tur, string uzakAdres, HttpStatusCode durum, string govde)
    {
        using var alan = new BulutAlani();
        var bulut = new SahteBulut { VarsayilanDurum = durum, VarsayilanGovde = govde };
        await alan.UzakHedefiYazAsync(tur, "/yedekler", string.Empty, SahteKoruyucu.Onek + "jeton");
        await alan.KayitlariYazAsync(Kayit(alan, "k1", tur, uzakAdres));

        var servis = alan.ServisKur(bulut);
        await servis.DeleteBackupAsync("k1", alan.Iptal);

        Assert.Empty(await servis.GetBackupsAsync(alan.Iptal));
    }

    [Fact]
    public async Task Azure_404_de_zaten_yok_sayilir()
    {
        using var alan = new BulutAlani();
        var bulut = new SahteBulut { VarsayilanDurum = HttpStatusCode.NotFound };
        await alan.UzakHedefiYazAsync(
            "azure",
            "https://hesap.blob.core.windows.net/kova/yedekler",
            "hesap",
            SahteKoruyucu.Onek + "c2VjcmV0LWtleS0xMjM0NTY3ODkwMTIzNA==");
        await alan.KayitlariYazAsync(
            Kayit(alan, "k1", "azure", "https://hesap.blob.core.windows.net/kova/yedekler/x.zip"));

        await alan.ServisKur(bulut).DeleteBackupAsync("k1", alan.Iptal);

        Assert.Empty(await alan.ServisKur(bulut).GetBackupsAsync(alan.Iptal));
    }

    [Theory]
    // OLCULEN HATA: bu iki yol HTTP yanitini HIC okumuyordu; 403 bile "sildim" sayiliyordu.
    [InlineData("webdav", "https://bulut.ornek.com/dav/x.zip")]
    [InlineData("http", "https://bulut.ornek.com/dav/x.zip")]
    [InlineData("s3", "https://kova.s3.amazonaws.com/yedekler/x.zip")]
    public async Task Erisim_reddedilince_sessizce_SILDIM_denmez(string tur, string uzakAdres)
    {
        using var alan = new BulutAlani();
        var bulut = new SahteBulut
        {
            VarsayilanDurum = HttpStatusCode.Forbidden,
            VarsayilanGovde = "<Error><Code>AccessDenied</Code></Error>"
        };
        // S3 imzali silme icin erisim anahtari gerekir; anahtarsiz yol zaten ayri bir dalda reddediyor.
        await alan.UzakHedefiYazAsync(tur, "/yedekler", "anahtar", SahteKoruyucu.Onek + "gizli");
        await alan.KayitlariYazAsync(Kayit(alan, "k1", tur, uzakAdres));

        var servis = alan.ServisKur(bulut);
        var hata = await Assert.ThrowsAsync<AppException>(() => servis.DeleteBackupAsync("k1", alan.Iptal));

        Assert.Equal("backup.remote_delete_failed", hata.MessageKey);
        Assert.Contains("403", hata.Details, StringComparison.Ordinal);

        // KAYIT DURUYOR: silinseydi dosya hedefte yetim kalirdi.
        var kalan = Assert.Single(await servis.GetBackupsAsync(alan.Iptal));
        Assert.True(kalan.RemoteDeleteFailed);
    }

    // =======================================================================================
    // 4) GECE ISININ OZETI: "TAMAMLANDI" DEMEDEN ONCE BUDAMAYA DA BAKAR
    // =======================================================================================

    [Fact]
    public void Budama_basarisizligi_gece_isini_TAMAMLANDI_saydirmaz()
    {
        // OLCULEN HATA (canli sunucu, 28.08.2026): "Tamamlandi · 2 site, 3 veritabani yedeklendi,
        // 7 eski yedek silindi." Kullanici dosyalari FTP'de goruyordu.
        var sonuc = new BackupPruneResultDto
        {
            Removed = 7,
            RemoteDeleteFailed = 3,
            Failures = { "eski.zip: remote_delete_failed; ftp_delete=550; remote_presence=present" }
        };

        var ozet = AutomationService.SummarizePrune(sonuc);

        Assert.True(ozet.HasFailures);
        Assert.Equal("completed_with_errors", AutomationService.ResolveBackupStatus(hasError: false, ozet));

        // Cumle ARTIK silinemeyeni de soyluyor.
        Assert.Contains("7 eski yedek silindi", ozet.Sentence, StringComparison.Ordinal);
        Assert.Contains("3", ozet.Sentence, StringComparison.Ordinal);
        Assert.Contains("SILINEMEDI", ozet.Sentence, StringComparison.Ordinal);

        // Teknik satirlar dilden bagimsiz etiket=deger.
        Assert.Contains(ozet.TechnicalLines, satir => satir.Contains("remote_delete_failed=3", StringComparison.Ordinal));
        Assert.Contains(ozet.TechnicalLines, satir => satir.Contains("ftp_delete=550", StringComparison.Ordinal));
    }

    [Fact]
    public void Temiz_budama_hala_TAMAMLANDI_der()
    {
        // Karsi kontrol: yeni koruma normal geceleri "hatali" gostermemeli.
        var ozet = AutomationService.SummarizePrune(new BackupPruneResultDto { Removed = 7 });

        Assert.False(ozet.HasFailures);
        Assert.Empty(ozet.TechnicalLines);
        Assert.Equal("completed", AutomationService.ResolveBackupStatus(hasError: false, ozet));
        Assert.DoesNotContain("SILINEMEDI", ozet.Sentence, StringComparison.Ordinal);

        // Yedeklemenin kendi hatasi da hala durumu bozar.
        Assert.Equal("completed_with_errors", AutomationService.ResolveBackupStatus(hasError: true, ozet));
    }

    // =======================================================================================
    // Yardimcilar
    // =======================================================================================

    private static async Task<BackupService> FtpServisiKurAsync(BulutAlani alan, SahteFtp ftp)
    {
        await alan.UzakHedefiYazAsync("ftp", "ftp://yedek.ornek.com/yedekler", "kullanici", SahteKoruyucu.Onek + "parola");
        await alan.KayitlariYazAsync(Kayit(alan, "k1", "ftp", FtpAdres));
        return alan.ServisKur(new SahteBulut(), ftp: ftp);
    }

    private static BackupPruneRemoteOptions FtpKimligi() =>
        new() { UserName = "kullanici", Password = "parola" };

    private static BackupRecordDto FtpKaydi(BulutAlani alan, string id, int gunOnce)
    {
        var kayit = Kayit(alan, id, "ftp", $"ftp://yedek.ornek.com/yedekler/{id}.zip");
        kayit.CreatedAtUtc = DateTime.UtcNow.AddDays(-gunOnce);
        return kayit;
    }

    private static BackupRecordDto Kayit(BulutAlani alan, string id, string tur, string uzakAdres)
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
            Status = "created_remote_synced"
        };
    }
}
