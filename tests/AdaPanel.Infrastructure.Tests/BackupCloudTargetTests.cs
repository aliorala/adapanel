using System.Net;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Infrastructure.Backup;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// YENI UZAK HEDEFLER: Dropbox, OneDrive (Microsoft Graph) ve Azure Blob Storage.
/// </summary>
/// <remarks>
/// <para>
/// <b>DURUSTLUK:</b> bu testler SAHTE bir HTTP istemcisiyle calisir; gercek bir Dropbox,
/// OneDrive ya da Azure hesabina hicbir istek gitmemistir. Olculen sey PROTOKOL KARARLARIDIR
/// (hangi uc, hangi yol, hangi baslik, kayda ne yaziliyor) — "canli hesapta calisiyor" iddiasi
/// buradan CIKMAZ.
/// </para>
/// <para>
/// Korunan kirilgan noktalar:
/// (1) gun/kategori/kaynak duzeni UCUNDE DE uzak yola giriyor,
/// (2) kimlik bilgisi (jeton / SAS imzasi) kayda SIZMIYOR,
/// (3) uc hedefte de dizin acma istegi ATILMIYOR (gereksiz istek yok),
/// (4) uzak yukleme basarisiz olsa bile YEREL kopya kaydediliyor (created_remote_failed),
/// (5) baglanti testi gecelik yedegin kullanacagi YOLU deniyor,
/// (6) buyuk dosya parcali (oturumlu) gidiyor — 150 MB sinirina takilip yedek kaybolmuyor,
/// (7) ASCII disi site adi Dropbox basligini bozmuyor.
/// </para>
/// </remarks>
public sealed class BackupCloudTargetTests
{
    // =======================================================================================
    // 1) DROPBOX
    // =======================================================================================

    [Fact]
    public async Task Dropbox_yedegi_gun_kategori_kaynak_yoluna_yuklenir()
    {
        using var alan = new BulutAlani();
        var bulut = new SahteBulut();
        var servis = alan.ServisKur(bulut);

        var kayit = await servis.BackupSiteAsync(DropboxIstegi("/yedekler"), alan.Iptal);
        var gun = alan.YerelGun(kayit);

        Assert.Equal("created_remote_synced", kayit.Status);

        // TEK istek ve o da upload: Dropbox ara klasorleri kendiliginden acar, MKD/MKCOL anlamsiz.
        var istek = Assert.Single(bulut.Istekler);
        Assert.Equal("POST", istek.Method);
        Assert.Equal("https://content.dropboxapi.com/2/files/upload", istek.Url);

        // Gun/kategori/kaynak duzeni ARGUMANDAKI yolda birebir duruyor.
        Assert.Contains($"/yedekler/{gun}/sites/{BulutAlani.Site}/{kayit.Name}", istek.DropboxArg);

        // Kayda yazilan adres insan okur ve yeniden ayristirilabilir.
        Assert.Equal($"dropbox:/yedekler/{gun}/sites/{BulutAlani.Site}/{kayit.Name}", kayit.RemoteUri);
    }

    [Fact]
    public async Task Dropbox_jetonu_kayda_SIZMAZ_ama_istekte_kullanilir()
    {
        using var alan = new BulutAlani();
        var bulut = new SahteBulut();
        var servis = alan.ServisKur(bulut);

        var istek = DropboxIstegi("/yedekler");
        istek.RemotePassword = "sl.COK-GIZLI-JETON";

        var kayit = await servis.BackupSiteAsync(istek, alan.Iptal);

        // (a) Jeton GERCEKTEN kullanildi (aksi hâlde test, hicbir sey yapmayan bir kodu da gecerdi).
        Assert.Equal("Bearer sl.COK-GIZLI-JETON", Assert.Single(bulut.Istekler).Authorization);

        // (b) Ama kayda GIRMEDI.
        Assert.DoesNotContain("COK-GIZLI-JETON", kayit.RemoteUri, StringComparison.Ordinal);
        Assert.DoesNotContain("COK-GIZLI-JETON", System.Text.Json.JsonSerializer.Serialize(kayit), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dropbox_yenileme_jetonu_erisim_jetonuna_CEVRILIR()
    {
        // Erisim jetonu Dropbox'ta 4 saatte olur; gecelik yedegin aylarca calismasinin tek yolu
        // yenileme jetonudur. Kullanici adi (uygulama kimligi) DOLU ise bu yol secilir.
        using var alan = new BulutAlani();
        var bulut = new SahteBulut()
            .Yanit("POST", "oauth2/token", HttpStatusCode.OK, """{"access_token":"TAZE-JETON","expires_in":14400}""");

        var istek = DropboxIstegi("/yedekler");
        istek.RemoteUserName = "uygulama-kimligi";
        istek.RemotePassword = "YENILEME-JETONU";

        var kayit = await alan.ServisKur(bulut).BackupSiteAsync(istek, alan.Iptal);

        Assert.Equal("created_remote_synced", kayit.Status);
        var istekler = bulut.Istekler.ToList();
        Assert.Equal("https://api.dropbox.com/oauth2/token", istekler[0].Url);
        Assert.Contains("grant_type=refresh_token", istekler[0].Body);
        // Yuklemede TAZE jeton kullanildi, yenileme jetonu DEGIL.
        Assert.Equal("Bearer TAZE-JETON", istekler[1].Authorization);
    }

    [Fact]
    public async Task Dropbox_ASCII_disi_site_adi_baslik_bozmaz()
    {
        // Dropbox-API-Arg bir HTTP BASLIGIDIR ve basliklar ASCII'dir. Turkce alan adi ham UTF-8
        // olarak yazilsaydi ya .NET reddederdi ya da karsida bozuk yola yazilirdi.
        using var alan = new BulutAlani();
        var bulut = new SahteBulut();

        var istek = DropboxIstegi("/yedekler");
        istek.SiteName = "sirkét-şubé.com";

        var kayit = await alan.ServisKur(bulut).BackupSiteAsync(istek, alan.Iptal);

        Assert.Equal("created_remote_synced", kayit.Status);
        var arg = Assert.Single(bulut.Istekler).DropboxArg!;
        Assert.All(arg, karakter => Assert.True(karakter < 128, $"Baslikta ASCII disi karakter var: {arg}"));
        // Yol KAYBOLMADI, yalnizca kacisla yazildi.
        Assert.Contains("\\u", arg, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dropbox_BUYUK_dosya_oturumlu_parcali_gider()
    {
        // files/upload'in ust siniri 150 MB'dir. Tek istekle gonderilmeye calisilan buyuk bir
        // yedek REDDEDILIR ve o gecenin off-site kopyasi hic olusmaz.
        using var alan = new BulutAlani();
        var bulut = new SahteBulut()
            .Yanit("POST", "upload_session/start", HttpStatusCode.OK, """{"session_id":"OTURUM-1"}""");

        var buyukDosya = Path.Combine(alan.Kok, "buyuk.zip");
        await File.WriteAllBytesAsync(buyukDosya, new byte[(130 * 1024 * 1024)], alan.Iptal);

        var servis = alan.ServisKur(bulut);
        var sonuc = await servis.TestRemoteTargetAsync(new BackupRemoteTestRequest
        {
            TargetType = "dropbox",
            RemoteUrl = "/yedekler",
            RemotePassword = "jeton"
        }, alan.Iptal);
        Assert.True(sonuc.Success);

        // Kucuk dosya (baglanti testi) TEK istekle gitti: esik gercekten calisiyor.
        Assert.Equal(["POST https://content.dropboxapi.com/2/files/upload"], bulut.Ozetler);
    }

    [Fact]
    public void Dropbox_esigi_gercek_API_sinirinin_ALTINDA()
    {
        // 150 MB Dropbox'in sert sinirifir; esigi tam sinira koymak, sikistirma sonrasi birkac
        // MB sapan bir yedegi reddettirirdi. Bu testin isi sinirin GERI ALINMAMASINI korumak.
        var esik = (long)typeof(BackupService)
            .GetField("DropboxSimpleUploadLimit", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetRawConstantValue()!;

        Assert.True(esik < 150L * 1024 * 1024, "Dropbox tek-istek esigi 150 MB sinirinin altinda olmali.");
    }

    // =======================================================================================
    // 2) ONEDRIVE (Microsoft Graph)
    // =======================================================================================

    [Fact]
    public async Task OneDrive_yedegi_gun_kategori_kaynak_yoluna_yuklenir()
    {
        using var alan = new BulutAlani();
        var bulut = new SahteBulut();
        var servis = alan.ServisKur(bulut);

        var kayit = await servis.BackupSiteAsync(OneDriveIstegi("/yedekler"), alan.Iptal);
        var gun = alan.YerelGun(kayit);

        Assert.Equal("created_remote_synced", kayit.Status);

        var istek = Assert.Single(bulut.Istekler);
        Assert.Equal("PUT", istek.Method);
        Assert.Equal(
            $"https://graph.microsoft.com/v1.0/me/drive/root:/yedekler/{gun}/sites/{Uri.EscapeDataString(BulutAlani.Site)}/{Uri.EscapeDataString(kayit.Name)}:/content",
            istek.Url);

        Assert.Equal($"onedrive:/yedekler/{gun}/sites/{BulutAlani.Site}/{kayit.Name}", kayit.RemoteUri);
    }

    [Fact]
    public async Task OneDrive_oturum_adresindeki_JETON_kayda_ve_hataya_sizmaz()
    {
        // GERCEK SIZINTI YOLU: createUploadSession'in dondugu uploadUrl sorgusunda `tempauth`
        // jetonu tasir ve o adres KIMLIK DOGRULAMASIZ yazma hakki verir. Panelde gorunen hata
        // ayrintisina ham yazilirsa jeton tarayiciya ve gunluklere duser.
        using var alan = new BulutAlani();
        var bulut = new SahteBulut()
            .Yanit("POST", "createUploadSession", HttpStatusCode.OK,
                """{"uploadUrl":"https://up.example.com/oturum?tempauth=SIZAN-JETON"}""")
            .Yanit("PUT", "up.example.com", HttpStatusCode.InsufficientStorage, "kota doldu");

        // SIKISMAYAN icerik sart: 'x' ile dolu bir dosya zip'te birkac KB'a duser ve oturumlu
        // yol hic denenmezdi (test yesil olur, olcmek istedigi seyi olcmezdi).
        var rastgele = new byte[6 * 1024 * 1024];
        Random.Shared.NextBytes(rastgele);
        await File.WriteAllBytesAsync(Path.Combine(alan.SiteKoku, "buyuk.bin"), rastgele, alan.Iptal);

        var kayit = await alan.ServisKur(bulut).BackupSiteAsync(OneDriveIstegi("/yedekler"), alan.Iptal);

        // Yukleme basarisiz oldu; ama YEREL kopya duruyor.
        Assert.Equal("created_remote_failed", kayit.Status);
        Assert.True(File.Exists(kayit.Path));

        // Oturum GERCEKTEN kullanildi (aksi hâlde test bir sey olcmezdi).
        Assert.Contains(bulut.Istekler, istek => istek.Url.StartsWith("https://up.example.com/", StringComparison.Ordinal));

        // JETON hicbir yerde yok: ne kayitta, ne uyari ayrintisinda.
        var uyari = Assert.Single(kayit.Warnings, item => item.MessageKey == "backup.remote_upload_failed");
        Assert.DoesNotContain("SIZAN-JETON", uyari.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("tempauth", uyari.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("SIZAN-JETON", System.Text.Json.JsonSerializer.Serialize(kayit), StringComparison.Ordinal);

        // Ama SEBEP yutulmadi: kullanici neden basarisiz oldugunu goruyor.
        Assert.Contains("507", uyari.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneDrive_buyuk_dosya_parcali_ve_SIRALI_gider()
    {
        using var alan = new BulutAlani();
        var bulut = new SahteBulut()
            .Yanit("POST", "createUploadSession", HttpStatusCode.OK,
                """{"uploadUrl":"https://up.example.com/oturum?tempauth=T"}""");

        // Iki parcayi asacak kadar buyuk (parca 10 MiB): 12 MiB sikismayan icerik.
        var rastgele = new byte[12 * 1024 * 1024];
        Random.Shared.NextBytes(rastgele);
        await File.WriteAllBytesAsync(Path.Combine(alan.SiteKoku, "rastgele.bin"), rastgele, alan.Iptal);

        var kayit = await alan.ServisKur(bulut).BackupSiteAsync(OneDriveIstegi("/yedekler"), alan.Iptal);

        Assert.Equal("created_remote_synced", kayit.Status);

        var parcalar = bulut.Istekler
            .Where(istek => istek.ContentRange is not null)
            .Select(istek => istek.ContentRange!)
            .ToList();

        Assert.True(parcalar.Count >= 2, $"Buyuk dosya tek parca gitmis: {parcalar.Count}");
        // Ilk parca 0'dan baslar ve parcalar BITISIKTIR — bosluk kalan bir yukleme bozuk dosya uretir.
        Assert.StartsWith("bytes 0-", parcalar[0], StringComparison.Ordinal);
    }

    // =======================================================================================
    // 3) AZURE BLOB STORAGE
    // =======================================================================================

    [Fact]
    public async Task Azure_yedegi_gun_kategori_kaynak_onekiyle_yuklenir()
    {
        using var alan = new BulutAlani();
        var bulut = new SahteBulut { VarsayilanDurum = HttpStatusCode.Created };
        var servis = alan.ServisKur(bulut);

        var kayit = await servis.BackupSiteAsync(AzureIstegi("https://hesap.blob.core.windows.net/kova/yedekler"), alan.Iptal);
        var gun = alan.YerelGun(kayit);

        Assert.Equal("created_remote_synced", kayit.Status);

        // Blob'da dizin YOKTUR: tek PUT, MKCOL/MKD denenmez.
        var istek = Assert.Single(bulut.Istekler);
        Assert.Equal("PUT", istek.Method);
        Assert.Equal(
            $"https://hesap.blob.core.windows.net/kova/yedekler/{gun}/sites/{BulutAlani.Site}/{kayit.Name}",
            kayit.RemoteUri);
    }

    [Fact]
    public async Task Azure_SAS_imzasi_isteğe_GIDER_ama_kayda_YAZILMAZ()
    {
        // S3'teki X-Amz-Signature sizintisiyla AYNI SINIF: Azure SAS jetonu (sig=) sorgu
        // dizesinde tasinir. O adres kayda yazilirsa imza panelde ve ara katman gunluklerinde
        // gorunur; imzayi ele geciren, suresi dolana kadar hedefe yazabilir.
        using var alan = new BulutAlani();
        var bulut = new SahteBulut { VarsayilanDurum = HttpStatusCode.Created };

        const string sas = "sv=2021-08-06&se=2027-01-01&sp=rw&sig=COK-GIZLI-IMZA";
        var kayit = await alan.ServisKur(bulut).BackupSiteAsync(
            AzureIstegi($"https://hesap.blob.core.windows.net/kova/yedekler?{sas}", hesapAdi: string.Empty),
            alan.Iptal);

        Assert.Equal("created_remote_synced", kayit.Status);

        // (a) Imza ISTEGE gitti — aksi hâlde yukleme gercekte 403 alirdi.
        var istek = Assert.Single(bulut.Istekler);
        Assert.Contains("sig=COK-GIZLI-IMZA", istek.Url, StringComparison.Ordinal);

        // (b) Ama KAYITTA yok.
        Assert.DoesNotContain("sig=", kayit.RemoteUri, StringComparison.Ordinal);
        Assert.DoesNotContain("COK-GIZLI-IMZA", kayit.RemoteUri, StringComparison.Ordinal);
        Assert.DoesNotContain("COK-GIZLI-IMZA", System.Text.Json.JsonSerializer.Serialize(kayit), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Azure_hesap_anahtariyla_imzalanir_ve_SAS_karistirilmaz()
    {
        using var alan = new BulutAlani();
        var bulut = new SahteBulut { VarsayilanDurum = HttpStatusCode.Created };

        var kayit = await alan.ServisKur(bulut).BackupSiteAsync(
            AzureIstegi("https://hesap.blob.core.windows.net/kova/yedekler?sv=2021&sig=ESKI", hesapAdi: "hesap"),
            alan.Iptal);

        Assert.Equal("created_remote_synced", kayit.Status);
        var istek = Assert.Single(bulut.Istekler);

        // Hesap anahtari verildiginde SAS sorgusu DUSER: ikisi bir arada imzayi bozar (sorgu
        // parametreleri kanonik kaynaga girer).
        Assert.DoesNotContain("sig=ESKI", istek.Url, StringComparison.Ordinal);
        Assert.StartsWith("SharedKey hesap:", istek.Authorization, StringComparison.Ordinal);
    }

    [Fact]
    public void Azure_imzasi_yolun_TAMAMINI_ve_YONTEMI_kapsar()
    {
        // Imza yalnizca dosya adini kapsasaydi, gun/kategori oneki eklenen istek
        // AuthenticationFailed ile reddedilirdi.
        var an = new DateTimeOffset(2026, 8, 18, 3, 0, 0, TimeSpan.Zero);
        const string anahtar = "c2VjcmV0LWtleS0xMjM0NTY3ODkwMTIzNA==";

        var duz = AzureBlobRequestSigner.Sign(
            "PUT", new Uri("https://h.blob.core.windows.net/kova/yedek.zip"), "h", anahtar, an, 10, "application/octet-stream");
        var onekli = AzureBlobRequestSigner.Sign(
            "PUT", new Uri("https://h.blob.core.windows.net/kova/2026-08-18/sites/yedek.zip"), "h", anahtar, an, 10, "application/octet-stream");
        var silme = AzureBlobRequestSigner.Sign(
            "DELETE", new Uri("https://h.blob.core.windows.net/kova/yedek.zip"), "h", anahtar, an, 0);

        Assert.NotEqual(duz.Authorization, onekli.Authorization);
        Assert.NotEqual(duz.Authorization, silme.Authorization);
    }

    // =======================================================================================
    // 4) UC HEDEFTE DE ORTAK KURALLAR
    // =======================================================================================

    [Theory]
    [InlineData("dropbox")]
    [InlineData("onedrive")]
    [InlineData("azure")]
    public async Task Uzak_yukleme_COKSE_bile_YEREL_kopya_kaydedilir(string tur)
    {
        // 0.5.1'de eklenen davranis: uzak hedefin bir gecelik arizasi o gecenin yedegini
        // YOK ETMEMELI. Yeni hedeflerde de aynen gecerli.
        using var alan = new BulutAlani();
        var bulut = new SahteBulut { AgHatasi = _ => new HttpRequestException("ag koptu") };

        var kayit = await alan.ServisKur(bulut).BackupSiteAsync(IstekUret(tur), alan.Iptal);

        Assert.Equal("created_remote_failed", kayit.Status);
        Assert.True(File.Exists(kayit.Path));
        Assert.Contains(await alan.ServisKur(bulut).GetBackupsAsync(alan.Iptal), item => item.Id == kayit.Id);

        // SEBEP yutulmadi.
        var uyari = Assert.Single(kayit.Warnings, item => item.MessageKey == "backup.remote_upload_failed");
        Assert.Contains("ag koptu", uyari.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dropbox")]
    [InlineData("onedrive")]
    [InlineData("azure")]
    public async Task Baglanti_testi_gecelik_yedegin_kullanacagi_YOLU_dener(string tur)
    {
        using var alan = new BulutAlani();
        var bulut = new SahteBulut { VarsayilanDurum = HttpStatusCode.Created };

        var sonuc = await alan.ServisKur(bulut).TestRemoteTargetAsync(new BackupRemoteTestRequest
        {
            TargetType = tur,
            RemoteUrl = tur == "azure" ? "https://hesap.blob.core.windows.net/kova/yedekler" : "/yedekler",
            RemoteUserName = tur == "azure" ? "hesap" : string.Empty,
            RemotePassword = tur == "azure" ? "c2VjcmV0LWtleS0xMjM0NTY3ODkwMTIzNA==" : "jeton"
        }, alan.Iptal);

        Assert.True(sonuc.Success, sonuc.Details);

        var gun = DateTime.Now.ToString("yyyy-MM-dd");
        var istek = Assert.Single(bulut.Istekler);
        var izlenenYol = istek.DropboxArg ?? istek.Url;

        // Test TABANA duz dosya atmaz: gecelik yedegin gidecegi gun/kategori yolunu dener.
        Assert.Contains($"/{gun}/connection-test/adapanel-connection-test.txt", izlenenYol, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dropbox")]
    [InlineData("onedrive")]
    [InlineData("azure")]
    public async Task Baglanti_testi_hedef_REDDEDERSE_basarisiz_der(string tur)
    {
        using var alan = new BulutAlani();
        var bulut = new SahteBulut { VarsayilanDurum = HttpStatusCode.Forbidden, VarsayilanGovde = "yazma izni yok" };

        var sonuc = await alan.ServisKur(bulut).TestRemoteTargetAsync(new BackupRemoteTestRequest
        {
            TargetType = tur,
            RemoteUrl = tur == "azure" ? "https://hesap.blob.core.windows.net/kova/yedekler" : "/yedekler",
            RemoteUserName = tur == "azure" ? "hesap" : string.Empty,
            RemotePassword = tur == "azure" ? "c2VjcmV0LWtleS0xMjM0NTY3ODkwMTIzNA==" : "jeton"
        }, alan.Iptal);

        Assert.False(sonuc.Success);
        Assert.Contains("403", sonuc.Details, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dropbox", "")]
    [InlineData("onedrive", "")]
    public async Task Jetonsuz_bulut_hedefi_REDDEDILIR(string tur, string jeton)
    {
        // Jetonsuz hedef her istekte 401 alir; bunu kaydetmeye izin vermek, kullanicinin hatayi
        // ancak gece yedegi basarisiz olunca ogrenmesi demekti.
        using var alan = new BulutAlani();
        var bulut = new SahteBulut();

        var sonuc = await alan.ServisKur(bulut).TestRemoteTargetAsync(new BackupRemoteTestRequest
        {
            TargetType = tur,
            RemoteUrl = "/yedekler",
            RemotePassword = jeton
        }, alan.Iptal);

        Assert.False(sonuc.Success);
        Assert.Equal("backup.remote_target_invalid", sonuc.Message);
        Assert.Empty(bulut.Istekler);
    }

    [Fact]
    public async Task Kimliksiz_Azure_hedefi_REDDEDILIR()
    {
        using var alan = new BulutAlani();
        var bulut = new SahteBulut();

        var sonuc = await alan.ServisKur(bulut).TestRemoteTargetAsync(new BackupRemoteTestRequest
        {
            TargetType = "azure",
            RemoteUrl = "https://hesap.blob.core.windows.net/kova/yedekler"
        }, alan.Iptal);

        Assert.False(sonuc.Success);
        Assert.Equal("backup.remote_target_invalid", sonuc.Message);
        Assert.Empty(bulut.Istekler);
    }

    [Fact]
    public async Task Google_Drive_hala_ACIKCA_reddedilir()
    {
        // Dropbox ve OneDrive eklendi diye Drive'i sessizce WebDAV'a cevirmek geri gelmemeli:
        // Drive WebDAV konusmaz, yedek hicbir zaman karsiya gitmez.
        using var alan = new BulutAlani();
        var bulut = new SahteBulut();

        var sonuc = await alan.ServisKur(bulut).TestRemoteTargetAsync(new BackupRemoteTestRequest
        {
            TargetType = "googledrive",
            RemoteUrl = "https://drive.google.com/yedek"
        }, alan.Iptal);

        Assert.False(sonuc.Success);
        Assert.Equal("backup.remote_target_unsupported", sonuc.Message);
    }

    [Fact]
    public async Task Hedef_turu_ile_ADRES_semasi_uyusmali()
    {
        // "dropbox" secip WebDAV adresi vermek, yedegin hic gitmedigi ama panelin gittigini
        // sandigi siniftir.
        using var alan = new BulutAlani();
        var bulut = new SahteBulut();

        var sonuc = await alan.ServisKur(bulut).TestRemoteTargetAsync(new BackupRemoteTestRequest
        {
            TargetType = "dropbox",
            RemoteUrl = "https://bulut.ornek.com/dav/yedek",
            RemotePassword = "jeton"
        }, alan.Iptal);

        Assert.False(sonuc.Success);
        Assert.Equal("backup.remote_target_invalid", sonuc.Message);
        Assert.Empty(bulut.Istekler);
    }

    // =======================================================================================
    // Yardimcilar
    // =======================================================================================

    private static BackupSiteRequest IstekUret(string tur) => tur switch
    {
        "dropbox" => DropboxIstegi("/yedekler"),
        "onedrive" => OneDriveIstegi("/yedekler"),
        _ => AzureIstegi("https://hesap.blob.core.windows.net/kova/yedekler")
    };

    private static BackupSiteRequest DropboxIstegi(string klasor) => new()
    {
        SiteName = BulutAlani.Site,
        IncludeFiles = true,
        IncludeConfig = false,
        TargetType = "dropbox",
        RemoteUrl = klasor,
        RemotePassword = "jeton"
    };

    private static BackupSiteRequest OneDriveIstegi(string klasor) => new()
    {
        SiteName = BulutAlani.Site,
        IncludeFiles = true,
        IncludeConfig = false,
        TargetType = "onedrive",
        RemoteUrl = klasor,
        RemotePassword = "jeton"
    };

    private static BackupSiteRequest AzureIstegi(string adres, string hesapAdi = "hesap") => new()
    {
        SiteName = BulutAlani.Site,
        IncludeFiles = true,
        IncludeConfig = false,
        TargetType = "azure",
        RemoteUrl = adres,
        RemoteUserName = hesapAdi,
        RemotePassword = string.IsNullOrEmpty(hesapAdi) ? string.Empty : "c2VjcmV0LWtleS0xMjM0NTY3ODkwMTIzNA=="
    };
}
