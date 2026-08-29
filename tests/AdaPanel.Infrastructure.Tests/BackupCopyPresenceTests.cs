using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Infrastructure.Backup;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// PANEL, IKI KOPYANIN DA GERCEKTEN DURUP DURMADIGINI OLCER (ve olcmediginde bunu SOYLER).
/// </summary>
/// <remarks>
/// <para>
/// <b>KULLANICININ SOZLERI:</b> "Panel bu dosyanin diskte hala durup durmadigini OLCMUYOR: yol
/// kayittan geliyor. Dosya elle silinmisse yol yine gorunur." Ayni sey uzak kopya icin de
/// gecerliydi: FTP istemcisiyle ya da uzak tarafin kendi saklama politikasiyla silinen bir yedek
/// panelde "duruyor" gibi gorunuyordu. Bu, deponun kendi kuralinin ihlali — olculmemis bir seyi
/// kesin soylemek.
/// </para>
/// <para>
/// <b>UC DEGERLI:</b> var / yok / olculemedi. Ag arizasini "dosya silinmis" diye gostermek
/// kullaniciyi yedegi OLMADIGINA inandirir; bu yuzden "olcemedim" ayri bir degerdir ve son KESIN
/// olcumun uzerine YAZMAZ.
/// </para>
/// </remarks>
public sealed class BackupCopyPresenceTests
{
    // =======================================================================================
    // 1) MALIYET KARARININ KORUMASI
    // =======================================================================================

    [Fact]
    public async Task Listeleme_HICBIR_ag_istegi_yapmaz()
    {
        // MALIYET: uzak olcum kayit basina BIR AG ISTEGIDIR. Yuz kayitli bir panelde her liste
        // cizilisinde yuz FTP/HTTP istegi atmak listeyi kilitler ve hedefin hiz sinirina takar.
        // Karar: yerel varlik her listelemede (ucuz), uzak varlik ISTEK UZERINE (Dogrula ucu) ve
        // sonucu kayitta onbelleklenir. Bu test o karari korur.
        using var alan = new BulutAlani();
        var bulut = new SahteBulut();
        var ftp = new SahteFtp();

        await alan.KayitlariYazAsync(
            Kayit(alan, "k1", "ftp", "ftp://yedek.ornek.com/yedekler/k1.zip"),
            Kayit(alan, "k2", "dropbox", "dropbox:/yedekler/k2.zip"));

        var kayitlar = await alan.ServisKur(bulut, ftp: ftp).GetBackupsAsync(alan.Iptal);

        Assert.Equal(2, kayitlar.Count);
        Assert.Empty(bulut.Istekler);
        Assert.Empty(ftp.Komutlar);
    }

    [Fact]
    public async Task Olculmemis_uzak_kopya_DURUYOR_sayilmaz()
    {
        using var alan = new BulutAlani();
        await alan.KayitlariYazAsync(Kayit(alan, "k1", "ftp", "ftp://yedek.ornek.com/yedekler/k1.zip"));

        var kayit = Assert.Single(await alan.ServisKur(new SahteBulut()).GetBackupsAsync(alan.Iptal));

        Assert.Equal("unknown", kayit.RemoteState);
        Assert.False(kayit.OffsiteCopyLost);
        Assert.False(kayit.AllCopiesMissing);
    }

    // =======================================================================================
    // 2) OFF-SITE KOPYA SESSIZCE KAYBOLDUGUNDA
    // =======================================================================================

    [Fact]
    public async Task Uzak_kopya_YOK_yerel_VAR_ise_UYARI_alani_yanar()
    {
        using var alan = new BulutAlani();
        // FTP ust dizini listelendi, dosya ORADA DEGIL: uzak kopya gitmis.
        var ftp = new SahteFtp { ListeYaniti = new FtpListingResult(true, ["baska.zip"], 226, "226 OK") };
        await alan.UzakHedefiYazAsync("ftp", "ftp://yedek.ornek.com/yedekler", "kullanici", SahteKoruyucu.Onek + "parola");
        await alan.KayitlariYazAsync(Kayit(alan, "k1", "ftp", "ftp://yedek.ornek.com/yedekler/k1.zip"));

        var servis = alan.ServisKur(new SahteBulut(), ftp: ftp);
        var sonuc = await servis.VerifyBackupsAsync(alan.Iptal);

        Assert.Equal(1, sonuc.MissingRemote);
        Assert.Equal(0, sonuc.Unreachable);

        var kayit = Assert.Single(await servis.GetBackupsAsync(alan.Iptal));
        Assert.Equal("missing", kayit.RemoteState);
        // Felaket aninda gec kalmamak icin: "off-site yedegim var" sanisi burada kirilir.
        Assert.True(kayit.OffsiteCopyLost);
        Assert.False(kayit.AllCopiesMissing);

        // OLCUM SONUCU KAYDA YAZILIR (onbellek): liste bunu tureterek degil OKUYARAK gosterir.
        // Yazilmasaydi her liste cizilisinde yeniden ag istegi gerekirdi — reddedilen maliyet.
        var diskteki = await alan.DosyaDeposu.ReadAsync(
            "config/backups.json", Array.Empty<BackupRecordDto>(), alan.Iptal);
        Assert.Equal("missing", Assert.Single(diskteki).RemoteState);
    }

    [Fact]
    public async Task Yerel_YOK_uzak_VAR_da_soylenir()
    {
        using var alan = new BulutAlani();
        var ftp = new SahteFtp { ListeYaniti = new FtpListingResult(true, ["k1.zip"], 226, "226 OK") };
        await alan.UzakHedefiYazAsync("ftp", "ftp://yedek.ornek.com/yedekler", "kullanici", SahteKoruyucu.Onek + "parola");

        var kayit = Kayit(alan, "k1", "ftp", "ftp://yedek.ornek.com/yedekler/k1.zip");
        await alan.KayitlariYazAsync(kayit);
        // Kullanicinin senaryosu: dosya diskten ELLE silinmis.
        File.Delete(kayit.Path);

        var servis = alan.ServisKur(new SahteBulut(), ftp: ftp);
        await servis.VerifyBackupsAsync(alan.Iptal);

        var okunan = Assert.Single(await servis.GetBackupsAsync(alan.Iptal));
        Assert.True(okunan.LocalMissing);
        Assert.Equal("present", okunan.RemoteState);
        // Geri yukleme yerelden yapilacaksa bu onemli; ama "hicbir kopya yok" DEGIL.
        Assert.False(okunan.AllCopiesMissing);
        Assert.False(okunan.OffsiteCopyLost);
    }

    [Fact]
    public async Task Iki_kopya_da_yoksa_kayit_ARTIK_YOK_damgasi_yer_ama_SILINMEZ()
    {
        using var alan = new BulutAlani();
        var ftp = new SahteFtp { ListeYaniti = new FtpListingResult(true, [], 226, "226 OK") };
        await alan.UzakHedefiYazAsync("ftp", "ftp://yedek.ornek.com/yedekler", "kullanici", SahteKoruyucu.Onek + "parola");

        var kayit = Kayit(alan, "k1", "ftp", "ftp://yedek.ornek.com/yedekler/k1.zip");
        await alan.KayitlariYazAsync(kayit);
        File.Delete(kayit.Path);

        var servis = alan.ServisKur(new SahteBulut(), ftp: ftp);
        await servis.VerifyBackupsAsync(alan.Iptal);

        var okunan = Assert.Single(await servis.GetBackupsAsync(alan.Iptal));
        Assert.True(okunan.AllCopiesMissing);

        // KAYIT DURUYOR: sessizce silmek "yedegim vardi, ne oldu?" sorusunun cevabini yok
        // ederdi. Damgalanir, silme karari kullanicinindir.
        Assert.Equal("k1", okunan.Id);
    }

    [Fact]
    public async Task Uzak_kopyasi_HIC_olmayan_kayitta_durum_none_olur()
    {
        using var alan = new BulutAlani();
        var kayit = Kayit(alan, "k1", "local", string.Empty);
        await alan.KayitlariYazAsync(kayit);

        var servis = alan.ServisKur(new SahteBulut());
        var oncesi = Assert.Single(await servis.GetBackupsAsync(alan.Iptal));
        Assert.Equal("none", oncesi.RemoteState);
        Assert.False(oncesi.AllCopiesMissing);

        File.Delete(kayit.Path);
        var sonrasi = Assert.Single(await servis.GetBackupsAsync(alan.Iptal));
        // Uzak kopya HIC olmadigi icin yerelin gitmesi "hicbir kopya kalmadi" demektir.
        Assert.True(sonrasi.AllCopiesMissing);
    }

    // =======================================================================================
    // 3) "OLCEMEDIM" ILE "YOK" ASLA AYNI SEY DEGIL
    // =======================================================================================

    [Fact]
    public async Task Ulasilamayan_hedef_son_KESIN_olcumun_uzerine_YAZMAZ()
    {
        using var alan = new BulutAlani();
        await alan.UzakHedefiYazAsync("ftp", "ftp://yedek.ornek.com/yedekler", "kullanici", SahteKoruyucu.Onek + "parola");
        await alan.KayitlariYazAsync(Kayit(alan, "k1", "ftp", "ftp://yedek.ornek.com/yedekler/k1.zip"));

        // Once KESIN olcum: duruyor.
        var saglikli = new SahteFtp { ListeYaniti = new FtpListingResult(true, ["k1.zip"], 226, "226 OK") };
        await alan.ServisKur(new SahteBulut(), ftp: saglikli).VerifyBackupsAsync(alan.Iptal);

        // Sonra hedefe ULASILAMIYOR (ag arizasi).
        var arizali = new SahteFtp { ListeYaniti = new FtpListingResult(false, [], 0, "connection refused") };
        var servis = alan.ServisKur(new SahteBulut(), ftp: arizali);
        var sonuc = await servis.VerifyBackupsAsync(alan.Iptal);

        Assert.Equal(1, sonuc.Unreachable);
        Assert.Equal(0, sonuc.MissingRemote);

        var kayit = Assert.Single(await servis.GetBackupsAsync(alan.Iptal));
        // Son KESIN olcum korunur; ag arizasi yedegi kayip ilan ETMEZ.
        Assert.Equal("present", kayit.RemoteState);
        Assert.False(kayit.OffsiteCopyLost);
    }

    [Fact]
    public async Task Olcum_yapilamayan_hedefte_yerel_yoksa_bile_ARTIK_YOK_denmez()
    {
        using var alan = new BulutAlani();
        await alan.UzakHedefiYazAsync("ftp", "ftp://yedek.ornek.com/yedekler", "kullanici", SahteKoruyucu.Onek + "parola");

        var kayit = Kayit(alan, "k1", "ftp", "ftp://yedek.ornek.com/yedekler/k1.zip");
        await alan.KayitlariYazAsync(kayit);
        File.Delete(kayit.Path);

        var arizali = new SahteFtp { ListeYaniti = new FtpListingResult(false, [], 0, "connection refused") };
        var servis = alan.ServisKur(new SahteBulut(), ftp: arizali);
        await servis.VerifyBackupsAsync(alan.Iptal);

        var okunan = Assert.Single(await servis.GetBackupsAsync(alan.Iptal));
        Assert.True(okunan.LocalMissing);
        Assert.Equal("unknown", okunan.RemoteState);
        // Uzagi OLCEMEDIK: "hicbir kopya yok" demek kullaniciyi yedegi olmadigina inandirirdi.
        Assert.False(okunan.AllCopiesMissing);
    }

    [Fact]
    public async Task Kimlik_yoksa_uzak_kopya_DURUYOR_da_YOK_da_denmez()
    {
        using var alan = new BulutAlani();
        // Kayitli uzak hedef YOK (kullanici yerele donmus): olcum hic yapilamaz.
        await alan.KayitlariYazAsync(Kayit(alan, "k1", "ftp", "ftp://yedek.ornek.com/yedekler/k1.zip"));

        var servis = alan.ServisKur(new SahteBulut(), ftp: new SahteFtp());
        var sonuc = await servis.VerifyBackupsAsync(alan.Iptal);

        Assert.Equal(1, sonuc.Unreachable);
        Assert.Equal("unknown", Assert.Single(await servis.GetBackupsAsync(alan.Iptal)).RemoteState);
    }

    [Fact]
    public async Task Azure_SAS_hedefinde_404_YOK_diye_okunmaz()
    {
        // OLCULEN TUZAK: kayittaki adres SanitizeUri'den gecer, yani SAS SORGUSU DUSMUSTUR.
        // O adrese kimliksiz HEAD atmak ozel bir kapta 404 dondurur — Azure varligi GIZLEMEK icin
        // "yok" der. Bunu "yedek silinmis" saymak, DURAN bir off-site yedegi kayip ilan etmekti.
        using var alan = new BulutAlani();
        var bulut = new SahteBulut { VarsayilanDurum = System.Net.HttpStatusCode.NotFound };
        // Kullanici adi BOS = SAS ile kurulmus hedef: imza yeniden uretilemez.
        await alan.UzakHedefiYazAsync("azure", "https://hesap.blob.core.windows.net/kova/yedekler", string.Empty, string.Empty);

        var kayit = Kayit(alan, "k1", "azure", "https://hesap.blob.core.windows.net/kova/yedekler/k1.zip");
        await alan.KayitlariYazAsync(kayit);
        File.Delete(kayit.Path);

        var servis = alan.ServisKur(bulut);
        var sonuc = await servis.VerifyBackupsAsync(alan.Iptal);

        Assert.Equal(0, sonuc.MissingRemote);
        Assert.Equal(1, sonuc.Unreachable);
        // Hicbir HEAD istegi atilmadi: olcemeyecegimizi BILEREK denemedik.
        Assert.DoesNotContain(bulut.Istekler, istek => istek.Method == "HEAD");

        var okunan = Assert.Single(await servis.GetBackupsAsync(alan.Iptal));
        Assert.Equal("unknown", okunan.RemoteState);
        Assert.False(okunan.AllCopiesMissing);
    }

    // =======================================================================================
    // 4) ESKI KAYITLARLA GERIYE DONUK UYUM
    // =======================================================================================

    [Fact]
    public async Task Alan_eklenmeden_once_yazilmis_kayit_ESKI_olcumden_turetilir()
    {
        using var alan = new BulutAlani();
        var eski = Kayit(alan, "k1", "ftp", "ftp://yedek.ornek.com/yedekler/k1.zip");
        // Eski surumun yazdigi hâl: RemoteState alani YOK, iki degerli olcum var.
        eski.RemoteState = string.Empty;
        eski.RemoteMissing = true;
        eski.RemoteVerifiedUtc = DateTime.UtcNow.AddHours(-1);
        await alan.KayitlariYazAsync(eski);

        var kayit = Assert.Single(await alan.ServisKur(new SahteBulut()).GetBackupsAsync(alan.Iptal));

        Assert.Equal("missing", kayit.RemoteState);
        Assert.True(kayit.OffsiteCopyLost);
    }

    [Fact]
    public async Task Hic_olculmemis_eski_kayit_DURUYOR_sayilmaz()
    {
        using var alan = new BulutAlani();
        var eski = Kayit(alan, "k1", "ftp", "ftp://yedek.ornek.com/yedekler/k1.zip");
        eski.RemoteState = string.Empty;
        eski.RemoteMissing = false;
        eski.RemoteVerifiedUtc = null;
        await alan.KayitlariYazAsync(eski);

        // RemoteMissing=false HEM "olctuk, duruyor" HEM "hic olcmedik" demekti; ikisini ayirmak
        // icin olcum ANI'na bakilir.
        Assert.Equal(
            "unknown",
            Assert.Single(await alan.ServisKur(new SahteBulut()).GetBackupsAsync(alan.Iptal)).RemoteState);
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
            Status = string.IsNullOrEmpty(uzakAdres) ? "created" : "created_remote_synced"
        };
    }
}
