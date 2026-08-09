using System.IO.Compression;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Yedek sifrelemesi. DENETIMDE OLCULEN DURUM: <c>Backup</c> altinda tek bir <c>AesGcm</c> gecmiyordu,
/// yani yedekler duz <c>.zip</c>'ti ve FTP/S3/WebDAV hedeflerine SIFRESIZ cikiyordu -- musterinin
/// veritabani dokumu ve postasi ucuncu taraf depoda okunabilir duruyordu (KVKK/GDPR satis engeli).
/// <para>
/// Bu testler sifrelemenin kendisini degil, GUVENILEBILIR OLMASINI korur:
/// (1) sifrele-coz turu birebir ayni veriyi verir (cok bloklu dosyada da),
/// (2) YANLIS PAROLA net bir hata dondurur -- "bozuk dosya" degil,
/// (3) KURCALANMIS arsiv REDDEDILIR ve tek bayt bile cikarilmaz (GCM etiketi),
/// (4) SONDAN KESILMIS arsiv de reddedilir (son blok isareti nonce'ta),
/// (5) ESKI SIFRESIZ yedekler calismaya devam eder (bicim tespiti dosya basligindan),
/// (6) PAROLASIZ calisan ZAMANLANMIS yedek, panelde kayitli parolayla sifrelenir.
/// </para>
/// </summary>
public sealed class BackupEncryptionTests
{
    private const string Parola = "Musteri-Yedek-Parolasi-2026!";

    // ---------------------------------------------------------------------------------------
    // 1) Sifrele -> coz turu
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]            // bos dosya
    [InlineData(64)]           // tek, kucuk blok
    [InlineData(1 << 20)]      // TAM blok siniri (son blok bos kalir)
    [InlineData((1 << 20) + 7)] // cok bloklu
    public async Task EncryptThenDecrypt_ReturnsTheExactSameBytes(int uzunluk)
    {
        using var alan = new GeciciAlan();
        var kaynak = alan.RastgeleDosya("kaynak.zip", uzunluk);
        var beklenen = await File.ReadAllBytesAsync(kaynak);

        var sifreli = Path.Combine(alan.Kok, "kaynak.zip.enc");
        var cozulen = Path.Combine(alan.Kok, "cozulen.zip");

        await BackupArchiveCipher.EncryptFileAsync(kaynak, sifreli, Parola);

        // Sifreli dosya duz metni ICERMEZ ve AdaPanel sifreli yedegi olarak TANINIR.
        Assert.True(BackupArchiveCipher.IsEncryptedFile(sifreli));
        Assert.False(BackupArchiveCipher.IsEncryptedFile(kaynak));

        await BackupArchiveCipher.DecryptFileAsync(sifreli, cozulen, Parola);
        Assert.Equal(beklenen, await File.ReadAllBytesAsync(cozulen));
    }

    [Fact]
    public async Task EncryptedFile_DoesNotContainThePlainText()
    {
        using var alan = new GeciciAlan();
        var kaynak = Path.Combine(alan.Kok, "dump.sql");
        await File.WriteAllTextAsync(kaynak, "INSERT INTO musteri VALUES ('TCKN-12345678901');");

        var sifreli = kaynak + ".enc";
        await BackupArchiveCipher.EncryptFileAsync(kaynak, sifreli, Parola);

        var ham = await File.ReadAllBytesAsync(sifreli);
        var metin = System.Text.Encoding.UTF8.GetString(ham);
        Assert.DoesNotContain("TCKN-12345678901", metin, StringComparison.Ordinal);
    }

    /// <summary>
    /// Iki ayri sifreleme AYNI parolayla bile ayni ciktiyi vermez: tuz ve nonce kayit basina
    /// rastgeledir. Ayni cikti gelseydi nonce tekrar kullanilmis olurdu -- GCM'de bu, anahtarin
    /// pratikte kirilmasi demektir.
    /// </summary>
    [Fact]
    public async Task TwoEncryptionsOfTheSameFile_NeverProduceTheSameBytes()
    {
        using var alan = new GeciciAlan();
        var kaynak = alan.RastgeleDosya("kaynak.zip", 4096);

        var birinci = Path.Combine(alan.Kok, "bir.enc");
        var ikinci = Path.Combine(alan.Kok, "iki.enc");
        await BackupArchiveCipher.EncryptFileAsync(kaynak, birinci, Parola);
        await BackupArchiveCipher.EncryptFileAsync(kaynak, ikinci, Parola);

        Assert.NotEqual(await File.ReadAllBytesAsync(birinci), await File.ReadAllBytesAsync(ikinci));
    }

    // ---------------------------------------------------------------------------------------
    // 2) Yanlis parola NET hata verir
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task WrongPassword_ReportsWrongPassword_NotCorruptFile()
    {
        using var alan = new GeciciAlan();
        var kaynak = alan.RastgeleDosya("kaynak.zip", 5000);
        var sifreli = Path.Combine(alan.Kok, "kaynak.zip.enc");
        var hedef = Path.Combine(alan.Kok, "cozulen.zip");

        await BackupArchiveCipher.EncryptFileAsync(kaynak, sifreli, Parola);

        var hata = await Assert.ThrowsAsync<AppException>(
            () => BackupArchiveCipher.DecryptFileAsync(sifreli, hedef, "yanlis-parola"));

        // "backup.decrypt_corrupt" DEMEK KABUL EDILEMEZ: kullanici saglam yedeginden supheye duser,
        // parolasini yanlis yazdigini asla ogrenemez.
        Assert.Equal("backup.decrypt_wrong_password", hata.MessageKey);
        Assert.False(File.Exists(hedef));
    }

    // ---------------------------------------------------------------------------------------
    // 3) Kurcalanmis arsiv REDDEDILIR (GCM etiketi) -- tek bayt bile cikmaz
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task TamperedArchive_IsRejectedAndNothingIsExtracted()
    {
        using var alan = new GeciciAlan();
        var kaynak = alan.RastgeleDosya("kaynak.zip", 5000);
        var sifreli = Path.Combine(alan.Kok, "kaynak.zip.enc");
        var hedef = Path.Combine(alan.Kok, "cozulen.zip");

        await BackupArchiveCipher.EncryptFileAsync(kaynak, sifreli, Parola);

        // Sifreli govdenin ortasindan tek bir bit cevrilir.
        var ham = await File.ReadAllBytesAsync(sifreli);
        ham[ham.Length / 2] ^= 0x01;
        await File.WriteAllBytesAsync(sifreli, ham);

        var hata = await Assert.ThrowsAsync<AppException>(
            () => BackupArchiveCipher.DecryptFileAsync(sifreli, hedef, Parola));

        Assert.Equal("backup.decrypt_corrupt", hata.MessageKey);
        // Etiket dogrulanmadan HICBIR SEY cikarilmamali: yarim/degistirilmis duz metin diskte kalmaz.
        Assert.False(File.Exists(hedef));
    }

    /// <summary>Basligi kurcalamak da yakalanir: baslik her blogun iliskili verisidir (AAD).</summary>
    [Fact]
    public async Task TamperedHeader_IsRejected()
    {
        using var alan = new GeciciAlan();
        var kaynak = alan.RastgeleDosya("kaynak.zip", 3000);
        var sifreli = Path.Combine(alan.Kok, "kaynak.zip.enc");
        var hedef = Path.Combine(alan.Kok, "cozulen.zip");

        await BackupArchiveCipher.EncryptFileAsync(kaynak, sifreli, Parola);

        var ham = await File.ReadAllBytesAsync(sifreli);
        ham[20] ^= 0x01; // tuzun icinden bir bayt
        await File.WriteAllBytesAsync(sifreli, ham);

        var hata = await Assert.ThrowsAsync<AppException>(
            () => BackupArchiveCipher.DecryptFileAsync(sifreli, hedef, Parola));

        // Tuz degisti -> turetilen anahtar degisti -> baslik etiketi tutmaz.
        Assert.Equal("backup.decrypt_wrong_password", hata.MessageKey);
        Assert.False(File.Exists(hedef));
    }

    /// <summary>
    /// SONDAN KESME saldirisi: son bloklar atilirsa kalan bloklar tek tek gecerlidir. Son blok
    /// isareti nonce'a girdigi icin "yeni son blok" dogrulanamaz ve arsiv reddedilir.
    /// </summary>
    [Fact]
    public async Task TruncatedArchive_IsRejected()
    {
        using var alan = new GeciciAlan();
        var kaynak = alan.RastgeleDosya("kaynak.zip", (1 << 20) * 2 + 100); // 3 blok
        var sifreli = Path.Combine(alan.Kok, "kaynak.zip.enc");
        var hedef = Path.Combine(alan.Kok, "cozulen.zip");

        await BackupArchiveCipher.EncryptFileAsync(kaynak, sifreli, Parola);

        // Son blogu (100 bayt + 16 bayt etiket) at.
        var ham = await File.ReadAllBytesAsync(sifreli);
        await File.WriteAllBytesAsync(sifreli, ham[..(ham.Length - 116)]);

        var hata = await Assert.ThrowsAsync<AppException>(
            () => BackupArchiveCipher.DecryptFileAsync(sifreli, hedef, Parola));

        Assert.Equal("backup.decrypt_corrupt", hata.MessageKey);
        Assert.False(File.Exists(hedef));
    }

    // ---------------------------------------------------------------------------------------
    // 4) Servis seviyesi: gercek yedek al -> gercekten sifreli -> gercekten geri gelir
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task SiteBackup_WithPassword_IsStoredEncryptedAndRestoresWithTheSamePassword()
    {
        using var alan = new GeciciAlan();
        var siteKoku = alan.KlasorAc("wwwroot", "shop");
        await File.WriteAllTextAsync(Path.Combine(siteKoku, "index.php"), "MUSTERI ICERIGI");

        var servis = alan.ServisKur();
        var kayit = await servis.BackupSiteAsync(new BackupSiteRequest
        {
            SiteName = "shop.example.com",
            IncludeFiles = true,
            IncludeConfig = false,
            EncryptionPassword = Parola
        });

        Assert.True(kayit.Encrypted);
        Assert.EndsWith(".zip.enc", kayit.Name, StringComparison.Ordinal);
        Assert.True(BackupArchiveCipher.IsEncryptedFile(kayit.Path));

        // DUZ METIN KOPYA DISKTE KALMAZ: kalsaydi sifreleme yalniz uzak kopyayi korurdu.
        Assert.False(File.Exists(kayit.Path[..^".enc".Length]));

        // Sifreli dosya zip olarak ACILAMAZ (gercekten sifreli oldugunun olcumu).
        Assert.ThrowsAny<Exception>(() => ZipFile.OpenRead(kayit.Path).Dispose());

        // Icerik degistirilir; geri yukleme yedekteki hali dondurmeli.
        await File.WriteAllTextAsync(Path.Combine(siteKoku, "index.php"), "BOZULDU");

        var sonuc = await servis.RestoreAsync(new RestoreBackupRequest
        {
            BackupId = kayit.Id,
            EncryptionPassword = Parola
        });

        Assert.Equal("restored", sonuc.Status);
        Assert.Equal("MUSTERI ICERIGI", await File.ReadAllTextAsync(Path.Combine(siteKoku, "index.php")));
    }

    [Fact]
    public async Task Restore_WithWrongPassword_FailsClearlyAndLeavesLiveContentUntouched()
    {
        using var alan = new GeciciAlan();
        var siteKoku = alan.KlasorAc("wwwroot", "shop");
        await File.WriteAllTextAsync(Path.Combine(siteKoku, "index.php"), "yedekteki");

        var servis = alan.ServisKur();
        var kayit = await servis.BackupSiteAsync(new BackupSiteRequest
        {
            SiteName = "shop.example.com",
            IncludeConfig = false,
            EncryptionPassword = Parola
        });

        await File.WriteAllTextAsync(Path.Combine(siteKoku, "index.php"), "CANLI");

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.RestoreAsync(new RestoreBackupRequest
        {
            BackupId = kayit.Id,
            EncryptionPassword = "hatirladigimi-sandigim-parola"
        }));

        Assert.Equal("backup.decrypt_wrong_password", hata.MessageKey);
        // CANLI ICERIGE DOKUNULMADI: cozme, tek dosya yazilmadan once basarisiz olur.
        Assert.Equal("CANLI", await File.ReadAllTextAsync(Path.Combine(siteKoku, "index.php")));
    }

    [Fact]
    public async Task Restore_OfEncryptedBackupWithoutAnyPassword_SaysPasswordRequired()
    {
        using var alan = new GeciciAlan();
        var siteKoku = alan.KlasorAc("wwwroot", "shop");
        await File.WriteAllTextAsync(Path.Combine(siteKoku, "index.php"), "yedekteki");

        var servis = alan.ServisKur();
        var kayit = await servis.BackupSiteAsync(new BackupSiteRequest
        {
            SiteName = "shop.example.com",
            IncludeConfig = false,
            EncryptionPassword = Parola
        });

        var hata = await Assert.ThrowsAsync<AppException>(
            () => servis.RestoreAsync(new RestoreBackupRequest { BackupId = kayit.Id }));

        // Dosya SAGLAM, eksik olan parola. "Bozuk dosya" demek kullaniciyi yanlis yone gonderirdi.
        Assert.Equal("backup.password_required", hata.MessageKey);
    }

    // ---------------------------------------------------------------------------------------
    // 5) ESKI SIFRESIZ yedekler calismaya devam eder
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task LegacyUnencryptedBackup_StillRestores()
    {
        using var alan = new GeciciAlan();
        var siteKoku = alan.KlasorAc("wwwroot", "shop");
        await File.WriteAllTextAsync(Path.Combine(siteKoku, "index.php"), "eski surumden");

        // Panelin sifreleme oncesi urettigi bicim: duz .zip, kayitta Encrypted alani bile yok.
        var zip = Path.Combine(alan.Kok, "eski-yedek.zip");
        using (var akis = new FileStream(zip, FileMode.Create, FileAccess.Write))
        using (var arsiv = new ZipArchive(akis, ZipArchiveMode.Create))
        {
            var girdi = arsiv.CreateEntry("files/index.php", CompressionLevel.Fastest);
            using var yazici = new StreamWriter(girdi.Open());
            yazici.Write("eski surumden");
        }

        await alan.DosyaDeposu.WriteAsync("config/backups.json", new[]
        {
            new BackupRecordDto
            {
                Id = "eski",
                Name = "eski-yedek.zip",
                Type = "site",
                Source = "shop.example.com",
                Path = zip,
                Format = "zip",
                CreatedAtUtc = DateTime.UtcNow
            }
        });

        await File.WriteAllTextAsync(Path.Combine(siteKoku, "index.php"), "BOZULDU");

        // Panelde artik bir parola KAYITLI olsa bile eski yedek etkilenmez: karar dosya basligindan.
        var servis = alan.ServisKur(kayitliParola: Parola);
        var sonuc = await servis.RestoreAsync(new RestoreBackupRequest { BackupId = "eski" });

        Assert.Equal("restored", sonuc.Status);
        Assert.Equal("eski surumden", await File.ReadAllTextAsync(Path.Combine(siteKoku, "index.php")));
    }

    // ---------------------------------------------------------------------------------------
    // 6) ZAMANLANMIS (parolasiz) yedek
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Otomasyon gecelik yedegi PAROLA VERMEDEN cagirir (AutomationService istekte parola tasimaz).
    /// Panelde kayitli parola varsa yedek yine de SIFRELENIR ve o parolayla geri gelir; boyle
    /// olmasaydi "sifreleme aciktir" denip butun otomatik yedekler sifresiz kalirdi.
    /// </summary>
    [Fact]
    public async Task ScheduledBackup_WithoutRequestPassword_UsesTheStoredPanelPassword()
    {
        using var alan = new GeciciAlan();
        var siteKoku = alan.KlasorAc("wwwroot", "shop");
        await File.WriteAllTextAsync(Path.Combine(siteKoku, "index.php"), "gecelik");

        var servis = alan.ServisKur(kayitliParola: Parola);

        // Otomasyonun cagirdigi bicim: EncryptionPassword YOK.
        var kayit = await servis.BackupSiteAsync(new BackupSiteRequest
        {
            SiteName = "shop.example.com",
            IncludeConfig = false
        });

        Assert.True(kayit.Encrypted);
        Assert.True(BackupArchiveCipher.IsEncryptedFile(kayit.Path));

        await File.WriteAllTextAsync(Path.Combine(siteKoku, "index.php"), "BOZULDU");

        // Geri yukleme de parolasiz calisir: kayitli parola bulunur.
        var sonuc = await servis.RestoreAsync(new RestoreBackupRequest { BackupId = kayit.Id });
        Assert.Equal("restored", sonuc.Status);
        Assert.Equal("gecelik", await File.ReadAllTextAsync(Path.Combine(siteKoku, "index.php")));
    }

    /// <summary>
    /// Panelde parola YOKSA zamanlanmis yedek yine de ALINIR (sifresiz). Sifreleme istege baglidir;
    /// parola yok diye gecelik yedegi hic almamak, korumasiz yedekten cok daha pahaliya mal olurdu.
    /// </summary>
    [Fact]
    public async Task ScheduledBackup_WithoutAnyPassword_StillSucceedsUnencrypted()
    {
        using var alan = new GeciciAlan();
        var siteKoku = alan.KlasorAc("wwwroot", "shop");
        await File.WriteAllTextAsync(Path.Combine(siteKoku, "index.php"), "gecelik");

        var servis = alan.ServisKur();
        var kayit = await servis.BackupSiteAsync(new BackupSiteRequest
        {
            SiteName = "shop.example.com",
            IncludeConfig = false
        });

        Assert.False(kayit.Encrypted);
        Assert.False(BackupArchiveCipher.IsEncryptedFile(kayit.Path));
        Assert.EndsWith(".zip", kayit.Name, StringComparison.Ordinal);

        var sonuc = await servis.RestoreAsync(new RestoreBackupRequest { BackupId = kayit.Id });
        Assert.Equal("restored", sonuc.Status);
    }

    /// <summary>
    /// Cozulemeyen bir parola kaydi (panel baska makineye tasindi, DataProtection anahtarlari gitti)
    /// "hazir" gorunmemeli: aksi halde zamanlanmis yedekler sessizce sifresiz alinir, kullanici da
    /// ekranda "sifreleme acik" yazisina bakip korundugunu sanirdi.
    /// </summary>
    [Fact]
    public async Task EncryptionSettings_WhenStoredPasswordCannotBeDecrypted_ReportPasswordNotUsable()
    {
        using var alan = new GeciciAlan();

        // Once calisan bir koruyucuyla parola kaydedilir...
        var kasa = new BackupEncryptionStore(alan.DosyaDeposu, new SahteKoruyucu());
        await kasa.SetPasswordAsync(Parola);

        // ...sonra panel "baska makinede" acilir: cozme artik patlar.
        var bozukKasa = new BackupEncryptionStore(alan.DosyaDeposu, new CozemeyenKoruyucu());
        var servis = alan.ServisKur(kasa: bozukKasa);

        var ayar = await servis.GetEncryptionSettingsAsync();
        Assert.True(ayar.Enabled);
        Assert.False(ayar.PasswordUsable);
        Assert.Contains("AES-256-GCM", ayar.Algorithm, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------

    private sealed class GeciciAlan : IDisposable
    {
        public GeciciAlan()
        {
            Kok = Path.Combine(Path.GetTempPath(), $"adapanel-sifre-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Kok);
            DosyaDeposu = new JsonFileStore(
                Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Kok }));
            SiteKoku = KlasorAc("wwwroot", "shop");
        }

        public string Kok { get; }

        public string SiteKoku { get; private set; }

        public JsonFileStore DosyaDeposu { get; }

        public string KlasorAc(params string[] parcalar)
        {
            var yol = Path.Combine([Kok, .. parcalar]);
            Directory.CreateDirectory(yol);
            SiteKoku = yol;
            return yol;
        }

        public string RastgeleDosya(string ad, int uzunluk)
        {
            var yol = Path.Combine(Kok, ad);
            var veri = new byte[uzunluk];
            Random.Shared.NextBytes(veri);
            File.WriteAllBytes(yol, veri);
            return yol;
        }

        public BackupService ServisKur(string? kayitliParola = null, BackupEncryptionStore? kasa = null)
        {
            var store = kasa;
            if (store is null && kayitliParola is not null)
            {
                store = new BackupEncryptionStore(DosyaDeposu, new SahteKoruyucu());
                store.SetPasswordAsync(kayitliParola).GetAwaiter().GetResult();
            }

            return new BackupService(
                new SahteSiteServisi(SiteKoku),
                null!,
                null!,
                new SahteTercihServisi(Path.Combine(Kok, "backups")),
                null!,
                DosyaDeposu,
                null!,
                store);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Kok, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Testte DataProtection yok; kasanin "koru/coz" sozlesmesini tersinir bicimde taklit eder.</summary>
    private sealed class SahteKoruyucu : ISecretProtector
    {
        public string Protect(string plaintext) =>
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext));

        public string Unprotect(string protectedValue) =>
            System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue));
    }

    /// <summary>Panel baska makineye tasinmis: kayit duruyor ama cozulemiyor.</summary>
    private sealed class CozemeyenKoruyucu : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;

        public string Unprotect(string protectedValue) =>
            throw new InvalidOperationException("DataProtection anahtari bulunamadi.");
    }

    private sealed class SahteTercihServisi(string yedekKoku) : IPanelPreferencesService
    {
        public Task<PanelPreferencesDto> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PanelPreferencesDto { DefaultBackupRoot = yedekKoku });

        public Task<PanelPreferencesDto> UpdateAsync(UpdatePanelPreferencesRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class SahteSiteServisi(string uygulamaKoku) : IIisSiteService
    {
        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SiteDto
            {
                Name = siteName,
                PhysicalPath = uygulamaKoku,
                ApplicationRootPath = uygulamaKoku
            });

        public Task<IReadOnlyCollection<SiteDto>> GetSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> CreateSiteAsync(CreateSiteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> UpdatePhysicalPathAsync(string siteName, string physicalPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> StartSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> StopSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> AddBindingAsync(string siteName, SiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> RemoveBindingAsync(string siteName, RemoveSiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyCollection<SiteDto>> ImportExistingSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
