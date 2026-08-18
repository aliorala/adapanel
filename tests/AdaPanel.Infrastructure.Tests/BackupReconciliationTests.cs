using System.Net;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// ELLE SILINMIS YEDEKLERIN ALGILANMASI (uzlastirma).
/// </summary>
/// <remarks>
/// <para>
/// <b>OLCULEN HATA:</b> <c>GetBackupsAsync</c> kayitlari JSON'dan okuyup OLDUGU GIBI donduruyordu;
/// dosya varligi hic kontrol edilmiyordu. Diskten ya da FTP'den ELLE silinmis bir yedek panelde
/// "geri yuklenebilir" gorunmeye devam ediyordu. Kullanici bunu ancak GERI YUKLEMEYE
/// CALISTIGINDA — yani felaket aninda — ogreniyordu. Yedegin var oldugunu SANMAK, hic yedegin
/// olmamasindan tehlikelidir.
/// </para>
/// <para>
/// <b>EN KRITIK KURAL (bu dosyanin asil isi):</b> AG ARIZASI "DOSYA YOK" DEGILDIR. Gecici bir
/// baglanti sorunu, kimlik hatasi ya da hiz siniri yaniti "silinmis" olarak yorumlanirsa
/// kullanicinin BUTUN yedek gecmisi kayip damgasi yer. Bu yuzden olcumun UC sonucu vardir ve
/// ulasilamayan hedefte kayit ISARETLENMEZ.
/// </para>
/// <para>
/// Ikinci kural: HICBIR KAYIT SILINMEZ. Sessizce silinen kayit, yedegin kendiliginden
/// buharlastigi hissini verir; kullanici ne oldugunu asla ogrenemez.
/// </para>
/// </remarks>
public sealed class BackupReconciliationTests
{
    // =======================================================================================
    // 1) YEREL DOSYA — her listelemede olculur
    // =======================================================================================

    [Fact]
    public async Task Elle_silinmis_yerel_dosya_LISTEDE_isaretlenir()
    {
        using var alan = new BulutAlani();
        var bulut = new SahteBulut();

        var duran = Kayit(alan, "duran", dosyaYaz: true);
        var silinen = Kayit(alan, "silinen", dosyaYaz: true);
        await alan.KayitlariYazAsync(duran, silinen);

        // Kullanici dosyayi Explorer'dan siliyor.
        File.Delete(silinen.Path);

        var kayitlar = await alan.ServisKur(bulut).GetBackupsAsync(alan.Iptal);

        Assert.True(kayitlar.Single(item => item.Id == "silinen").LocalMissing);
        Assert.False(kayitlar.Single(item => item.Id == "duran").LocalMissing);
    }

    [Fact]
    public async Task Kayip_dosya_kaydi_SILINMEZ()
    {
        // Sessiz silme, kullanicinin "yedegim neden yok oldu" sorusunu cevapsiz birakir.
        using var alan = new BulutAlani();
        var bulut = new SahteBulut();

        var kayit = Kayit(alan, "k1", dosyaYaz: true);
        await alan.KayitlariYazAsync(kayit);
        File.Delete(kayit.Path);

        var servis = alan.ServisKur(bulut);
        Assert.Single(await servis.GetBackupsAsync(alan.Iptal));
        // Ikinci okuma da ayni: kayit hâlâ yerinde.
        Assert.Single(await servis.GetBackupsAsync(alan.Iptal));
    }

    [Fact]
    public async Task Dosya_GERI_KONULURSA_isaret_kalkar()
    {
        // Isaret diske YAZILMAZ, her listelemede yeniden olculur. Yazilsaydi dosyayi geri koyan
        // kullanici kalici "kayip" damgasiyla kalirdi.
        using var alan = new BulutAlani();
        var bulut = new SahteBulut();

        var kayit = Kayit(alan, "k1", dosyaYaz: true);
        await alan.KayitlariYazAsync(kayit);

        var servis = alan.ServisKur(bulut);
        File.Delete(kayit.Path);
        Assert.True((await servis.GetBackupsAsync(alan.Iptal)).Single().LocalMissing);

        await File.WriteAllTextAsync(kayit.Path, "geri konuldu", alan.Iptal);
        Assert.False((await servis.GetBackupsAsync(alan.Iptal)).Single().LocalMissing);
    }

    [Fact]
    public async Task Kayip_dosyayi_GERI_YUKLEMEK_anlasilir_hata_verir()
    {
        // Eskiden ham FileNotFoundException'a dusuyordu: kullanici felaket aninda anlasilmaz
        // bir yigin izi goruyordu.
        using var alan = new BulutAlani();
        var bulut = new SahteBulut();

        var kayit = Kayit(alan, "k1", dosyaYaz: true);
        await alan.KayitlariYazAsync(kayit);
        File.Delete(kayit.Path);

        var hata = await Assert.ThrowsAsync<AppException>(() =>
            alan.ServisKur(bulut).RestoreAsync(new RestoreBackupRequest
            {
                BackupId = "k1",
                TargetPath = Path.Combine(alan.Kok, "hedef")
            }, alan.Iptal));

        Assert.Equal("backup.file_missing", hata.MessageKey);
        Assert.Contains("k1.zip", hata.Details, StringComparison.Ordinal);
    }

    // =======================================================================================
    // 2) UZAK KOPYA — yalniz DOGRULAMA calisinca olculur
    // =======================================================================================

    [Fact]
    public async Task Dogrulama_UZAKTA_OLMAYAN_kopyayi_bulur()
    {
        using var alan = new BulutAlani();
        var bulut = new SahteBulut { VarsayilanDurum = HttpStatusCode.NotFound };
        await alan.UzakHedefiYazAsync("dropbox", "/yedekler", string.Empty, SahteKoruyucu.Onek + "jeton");
        bulut.Yanit("POST", "get_metadata", HttpStatusCode.Conflict, """{"error_summary":"path/not_found/."}""");
        bulut.Yanit("POST", "list_folder", HttpStatusCode.OK, """{"entries":[],"has_more":false}""");

        await alan.KayitlariYazAsync(Kayit(alan, "k1", dosyaYaz: true, uzak: "dropbox:/yedekler/k1.zip", tur: "dropbox"));

        var servis = alan.ServisKur(bulut);
        var sonuc = await servis.VerifyBackupsAsync(alan.Iptal);

        Assert.Equal(1, sonuc.Checked);
        Assert.Equal(1, sonuc.MissingRemote);
        Assert.Equal(0, sonuc.Unreachable);
        Assert.Equal("backup.verify.missing_remote", Assert.Single(sonuc.Issues).MessageKey);

        // Olcum KAYDA islendi (bir daha ag istegi atmadan gorulebilsin diye).
        var kayit = Assert.Single(await servis.GetBackupsAsync(alan.Iptal));
        Assert.True(kayit.RemoteMissing);
        Assert.NotNull(kayit.RemoteVerifiedUtc);
    }

    /// <summary>
    /// BU DOSYANIN EN KRITIK TESTI.
    /// </summary>
    [Theory]
    [InlineData("aglada")]     // ag istisnasi
    [InlineData("401")]        // kimlik hatasi
    [InlineData("429")]        // hiz siniri
    [InlineData("500")]        // sunucu hatasi
    public async Task Ag_arizasi_DOSYA_YOK_sayilmaz(string ariza)
    {
        using var alan = new BulutAlani();
        var bulut = new SahteBulut();
        await alan.UzakHedefiYazAsync("dropbox", "/yedekler", string.Empty, SahteKoruyucu.Onek + "jeton");

        if (ariza == "aglada")
        {
            bulut.AgHatasi = url => url.Contains("get_metadata", StringComparison.Ordinal)
                ? new HttpRequestException("baglanti kurulamadi")
                : null;
            bulut.Yanit("POST", "list_folder", HttpStatusCode.OK, """{"entries":[],"has_more":false}""");
        }
        else
        {
            bulut.Yanit("POST", "get_metadata", (HttpStatusCode)int.Parse(ariza), "gecici hata");
            bulut.Yanit("POST", "list_folder", HttpStatusCode.OK, """{"entries":[],"has_more":false}""");
        }

        await alan.KayitlariYazAsync(Kayit(alan, "k1", dosyaYaz: true, uzak: "dropbox:/yedekler/k1.zip", tur: "dropbox"));

        var servis = alan.ServisKur(bulut);
        var sonuc = await servis.VerifyBackupsAsync(alan.Iptal);

        // (a) "YOK" DENMEDI.
        Assert.Equal(0, sonuc.MissingRemote);
        Assert.Equal(1, sonuc.Unreachable);
        Assert.Equal("backup.verify.unreachable", Assert.Single(sonuc.Issues).MessageKey);

        // (b) KAYIT ISARETLENMEDI: ulasilamayan hedef, kaydi kayip damgalamaz.
        var kayit = Assert.Single(await servis.GetBackupsAsync(alan.Iptal));
        Assert.False(kayit.RemoteMissing);

        // (c) KAYIT SILINMEDI.
        Assert.Equal("k1", kayit.Id);
    }

    [Fact]
    public async Task Uzakta_DURAN_kopya_saglam_isaretlenir()
    {
        // Karsi kontrol: dogrulama her seye "sorunlu" demiyor.
        using var alan = new BulutAlani();
        var bulut = new SahteBulut { VarsayilanDurum = HttpStatusCode.OK };
        await alan.UzakHedefiYazAsync("dropbox", "/yedekler", string.Empty, SahteKoruyucu.Onek + "jeton");
        bulut.Yanit("POST", "list_folder", HttpStatusCode.OK, """{"entries":[],"has_more":false}""");

        await alan.KayitlariYazAsync(Kayit(alan, "k1", dosyaYaz: true, uzak: "dropbox:/yedekler/k1.zip", tur: "dropbox"));

        var servis = alan.ServisKur(bulut);
        var sonuc = await servis.VerifyBackupsAsync(alan.Iptal);

        Assert.Equal(0, sonuc.MissingRemote);
        Assert.Equal(0, sonuc.MissingLocal);
        Assert.Equal(0, sonuc.Unreachable);
        Assert.Empty(sonuc.Issues);
        Assert.False((await servis.GetBackupsAsync(alan.Iptal)).Single().RemoteMissing);
    }

    [Fact]
    public async Task Dogrulama_YEREL_kaybi_da_raporlar_ve_denetime_yazar()
    {
        using var alan = new BulutAlani();
        var bulut = new SahteBulut();

        var kayit = Kayit(alan, "k1", dosyaYaz: true);
        await alan.KayitlariYazAsync(kayit);
        File.Delete(kayit.Path);

        var sonuc = await alan.ServisKur(bulut).VerifyBackupsAsync(alan.Iptal);

        Assert.Equal(1, sonuc.MissingLocal);
        Assert.Equal("backup.verify.missing_local", Assert.Single(sonuc.Issues).MessageKey);

        // Iz birakiliyor: yedegin kendiliginden buharlastigi hissi, izi olmayan degisiklikten dogar.
        Assert.Contains(alan.Denetim.Kayitlar, item => item.Action == "backup.verification");
    }

    // =======================================================================================
    // 3) TERS YETIM — hedefte duran ama kaydi olmayan dosyalar
    // =======================================================================================

    [Fact]
    public async Task Kaydi_olmayan_uzak_dosya_YETIM_olarak_listelenir()
    {
        using var alan = new BulutAlani();
        var bulut = new SahteBulut { VarsayilanDurum = HttpStatusCode.OK };
        await alan.UzakHedefiYazAsync("dropbox", "/yedekler", string.Empty, SahteKoruyucu.Onek + "jeton");

        bulut.Yanit("POST", "list_folder", HttpStatusCode.OK, """
        {"entries":[
          {".tag":"file","path_display":"/yedekler/2026-08-01/sites/shop/bilinen.zip"},
          {".tag":"file","path_display":"/yedekler/2026-07-01/sites/shop/YETIM.zip"},
          {".tag":"file","path_display":"/yedekler/2026-08-01/connection-test/adapanel-connection-test.txt"},
          {".tag":"folder","path_display":"/yedekler/2026-08-01"}
        ],"has_more":false}
        """);

        await alan.KayitlariYazAsync(Kayit(
            alan, "k1", dosyaYaz: true,
            uzak: "dropbox:/yedekler/2026-08-01/sites/shop/bilinen.zip", tur: "dropbox"));

        var sonuc = await alan.ServisKur(bulut).VerifyBackupsAsync(alan.Iptal);

        // Yalniz GERCEK yetim: kaydi olan dosya, klasor girdisi ve baglanti testi dosyasi haric.
        var yetim = Assert.Single(sonuc.Orphans);
        Assert.Equal("/yedekler/2026-07-01/sites/shop/YETIM.zip", yetim.RemotePath);
        Assert.Equal("dropbox", yetim.TargetType);
    }

    [Fact]
    public async Task Yetim_taramasi_YAPILAMAYAN_hedef_ACIKCA_soylenir()
    {
        // Bos yetim listesi "yetim yok" anlamina GELMEMELI. FTP ve OneDrive'da tek istekle
        // ozyinelemeli listeleme yok; sessizce bos donmek kullaniciyi yanlis rahatlatirdi.
        using var alan = new BulutAlani();
        var bulut = new SahteBulut { VarsayilanDurum = HttpStatusCode.OK };
        await alan.UzakHedefiYazAsync("onedrive", "/yedekler", string.Empty, SahteKoruyucu.Onek + "jeton");

        await alan.KayitlariYazAsync(Kayit(alan, "k1", dosyaYaz: true, uzak: "onedrive:/yedekler/k1.zip", tur: "onedrive"));

        var sonuc = await alan.ServisKur(bulut).VerifyBackupsAsync(alan.Iptal);

        Assert.Empty(sonuc.Orphans);
        Assert.Contains("onedrive", sonuc.OrphanScanSkipped);
    }

    [Fact]
    public async Task Yetim_taramasi_hicbir_seyi_SILMEZ()
    {
        // Hedefteki klasor kullanicinin baska amacla da kullandigi bir yer olabilir; panele ait
        // olmayan dosyayi silmek geri alinamaz veri kaybi olurdu.
        using var alan = new BulutAlani();
        var bulut = new SahteBulut { VarsayilanDurum = HttpStatusCode.OK };
        await alan.UzakHedefiYazAsync("dropbox", "/yedekler", string.Empty, SahteKoruyucu.Onek + "jeton");
        bulut.Yanit("POST", "list_folder", HttpStatusCode.OK,
            """{"entries":[{".tag":"file","path_display":"/yedekler/YETIM.zip"}],"has_more":false}""");

        await alan.KayitlariYazAsync(Kayit(alan, "k1", dosyaYaz: true, uzak: "dropbox:/yedekler/k1.zip", tur: "dropbox"));

        var sonuc = await alan.ServisKur(bulut).VerifyBackupsAsync(alan.Iptal);

        Assert.Single(sonuc.Orphans);
        // Hicbir DELETE/silme istegi atilmadi.
        Assert.DoesNotContain(bulut.Istekler, istek =>
            istek.Method == "DELETE" || istek.Url.Contains("delete", StringComparison.OrdinalIgnoreCase));
    }

    // =======================================================================================
    // Yardimcilar
    // =======================================================================================

    private static BackupRecordDto Kayit(
        BulutAlani alan,
        string id,
        bool dosyaYaz,
        string uzak = "",
        string tur = "local")
    {
        var yol = Path.Combine(alan.Kok, $"{id}.zip");
        if (dosyaYaz)
        {
            using var arsiv = System.IO.Compression.ZipFile.Open(yol, System.IO.Compression.ZipArchiveMode.Create);
            arsiv.CreateEntry("files/index.php");
        }

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
            RemoteUri = uzak,
            Status = string.IsNullOrEmpty(uzak) ? "created" : "created_remote_synced"
        };
    }
}
