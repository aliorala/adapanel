using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Infrastructure.Backup;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Yedek silindikten sonra BOŞ kalan gün klasörü de kaldırılır (FTP).
/// </summary>
/// <remarks>
/// <para>
/// <b>Kullanıcı bildirimi (canlı kurulum):</b> budama yedek dosyalarını siliyordu ama uzak FTP
/// hedefinde gün klasörleri boş olarak birikiyordu — <i>"içleri boşmuş yani siliyor evet ama
/// klasörleri de silsin"</i>.
/// </para>
/// <para>
/// <b>SINIR:</b> bilinen yedek düzenindeki boş alt klasörler ve gün klasörü kaldırılır.
/// "Üst klasöre çık ve boşsa sil" kuralı daha basit olurdu ama kullanıcının yapılandırdığı kök
/// klasörü (<c>/yedekler</c>) son yedek silindiğinde YOK EDERDİ. Panelin oluşturmadığı bir
/// klasörü silmek yetki aşımıdır.
/// </para>
/// <para>
/// <b>DÜRÜSTLÜK NOTU:</b> burada ölçülen şey PANELİN KARARIDIR — hangi komutu hangi sırayla
/// gönderdiği. Gerçek bir FTP sunucusunun <c>RMD</c> davranışı bu testlerden çıkarılamaz.
/// </para>
/// </remarks>
public sealed class BosGunKlasoruTemizligiTests
{
    [Theory]
    [InlineData("sites/site.ornek", 3)]
    [InlineData("databases/mariadb-iptvhub", 3)]
    [InlineData("sites/site.ornek/databases/db", 5)]
    [InlineData("subscriptions/musteri", 3)]
    [InlineData("panel", 2)]
    [InlineData("mail", 2)]
    [InlineData("connection-test", 2)]
    [InlineData("sites/2026-09-01", 3)]
    [InlineData("sites/site%20name", 3)]
    public async Task Yeni_duzende_bos_klasorler_asagidan_yukari_gune_kadar_kaldirilir(string levels, int count)
    {
        using var alan = new BulutAlani();
        var ftp = new SahteFtp();
        const string root = "ftp://yedek.ornek.com/yedekler/";
        var directory = root + "2026-09-05/" + levels + "/";
        await alan.UzakHedefiYazAsync("ftp", root, "kullanici", SahteKoruyucu.Onek + "parola");
        await alan.KayitlariYazAsync(Kayit(alan, "k1", directory + "k1.zip"));

        await alan.ServisKur(new SahteBulut(), ftp: ftp).DeleteBackupAsync("k1", alan.Iptal);

        var removed = ftp.Komutlar.Where(command => command.StartsWith("RMD ")).ToArray();
        Assert.Equal(count, removed.Length);
        Assert.Equal("RMD " + new Uri(directory), removed[0]);
        Assert.Equal("RMD " + root + "2026-09-05/", removed[^1]);
        Assert.DoesNotContain("RMD " + root, removed);
    }

    [Theory]
    [InlineData("2026-09-05/custom/source")]
    [InlineData("2026-09-05/sites/site/custom")]
    [InlineData("not-a-day/sites/site")]
    public async Task Taninmayan_klasor_duzeni_temizlenmez(string levels)
    {
        using var alan = new BulutAlani();
        var ftp = new SahteFtp();
        await alan.UzakHedefiYazAsync("ftp", "ftp://yedek.ornek.com/yedekler", "kullanici", SahteKoruyucu.Onek + "parola");
        await alan.KayitlariYazAsync(Kayit(alan, "k1", "ftp://yedek.ornek.com/yedekler/" + levels + "/k1.zip"));
        await alan.ServisKur(new SahteBulut(), ftp: ftp).DeleteBackupAsync("k1", alan.Iptal);
        Assert.DoesNotContain(ftp.Komutlar, command => command.StartsWith("RMD "));
    }

    [Fact]
    public async Task Baska_kaynak_kalinca_ust_klasor_temizligi_durur()
    {
        using var alan = new BulutAlani();
        var ftp = new SahteFtp
        {
            ListDirectoryHandler = uri => new FtpListingResult(true,
                uri.AbsolutePath.EndsWith("/sites/") ? ["baska-site"] : [], 226, "OK")
        };
        await alan.UzakHedefiYazAsync("ftp", "ftp://yedek.ornek.com/yedekler", "kullanici", SahteKoruyucu.Onek + "parola");
        await alan.KayitlariYazAsync(Kayit(alan, "k1", "ftp://yedek.ornek.com/yedekler/2026-09-05/sites/site/k1.zip"));
        await alan.ServisKur(new SahteBulut(), ftp: ftp).DeleteBackupAsync("k1", alan.Iptal);
        Assert.Equal(["RMD ftp://yedek.ornek.com/yedekler/2026-09-05/sites/site/"],
            ftp.Komutlar.Where(command => command.StartsWith("RMD ")).ToArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Nlst_550_bos_sayilmaz_RMD_sonucuna_gore_ilerlenir(bool removed)
    {
        using var alan = new BulutAlani();
        var ftp = new SahteFtp
        {
            ListeYaniti = new FtpListingResult(false, [], 550, "Unavailable"),
            KlasorSilmeYaniti = removed ? FtpCommandResult.Ok(250, "Removed") : FtpCommandResult.Fail(550, "Denied or not empty")
        };
        await alan.UzakHedefiYazAsync("ftp", "ftp://yedek.ornek.com/yedekler", "kullanici", SahteKoruyucu.Onek + "parola");
        await alan.KayitlariYazAsync(Kayit(alan, "k1", "ftp://yedek.ornek.com/yedekler/2026-09-05/sites/site/k1.zip"));
        await alan.ServisKur(new SahteBulut(), ftp: ftp).DeleteBackupAsync("k1", alan.Iptal);
        Assert.Equal(removed ? 3 : 1, ftp.Komutlar.Count(command => command.StartsWith("RMD ")));
    }

    [Fact]
    public async Task Bos_kalan_GUN_klasoru_kaldirilir()
    {
        using var alan = new BulutAlani();
        var ftp = new SahteFtp();
        await alan.UzakHedefiYazAsync("ftp", "ftp://yedek.ornek.com/yedekler", "kullanici", SahteKoruyucu.Onek + "parola");
        await alan.KayitlariYazAsync(Kayit(alan, "k1", "ftp://yedek.ornek.com/yedekler/2026-09-05/k1.zip"));

        await alan.ServisKur(new SahteBulut(), ftp: ftp).DeleteBackupAsync("k1", alan.Iptal);

        Assert.Contains(ftp.Komutlar, komut => komut.StartsWith("DELE ", StringComparison.Ordinal));
        Assert.Contains(ftp.Komutlar, komut => komut.StartsWith("RMD ", StringComparison.Ordinal));
        Assert.Contains(ftp.Komutlar, komut => komut.Contains("2026-09-05", StringComparison.Ordinal));
    }

    /// <summary>
    /// KULLANICININ KOK KLASORU SILINMEZ — bu testin varlik sebebi budur.
    /// </summary>
    /// <remarks>
    /// Yedek doğrudan kökte duruyorsa (gün klasörü yok), silme sonrası kök boş kalır. Genel bir
    /// "boşsa sil" kuralı burada <c>/yedekler</c> klasörünü kaldırırdı; kullanıcı bir dahaki
    /// yedekte hedefini bulamazdı.
    /// </remarks>
    [Fact]
    public async Task Gun_klasoru_OLMAYAN_yolda_hicbir_klasor_kaldirilmaz()
    {
        using var alan = new BulutAlani();
        var ftp = new SahteFtp();
        await alan.UzakHedefiYazAsync("ftp", "ftp://yedek.ornek.com/yedekler", "kullanici", SahteKoruyucu.Onek + "parola");
        await alan.KayitlariYazAsync(Kayit(alan, "k1", "ftp://yedek.ornek.com/yedekler/k1.zip"));

        await alan.ServisKur(new SahteBulut(), ftp: ftp).DeleteBackupAsync("k1", alan.Iptal);

        Assert.Contains(ftp.Komutlar, komut => komut.StartsWith("DELE ", StringComparison.Ordinal));
        Assert.DoesNotContain(ftp.Komutlar, komut => komut.StartsWith("RMD ", StringComparison.Ordinal));
    }

    /// <summary>
    /// Klasör DOLUYSA kaldırılmaz — başka bir aboneliğin yedeği aynı günde durabilir.
    /// </summary>
    [Fact]
    public async Task Dolu_gun_klasoru_kaldirilmaz()
    {
        using var alan = new BulutAlani();
        var ftp = new SahteFtp
        {
            ListeYaniti = new FtpListingResult(true, ["baska-abonelik.zip"], 226, "226 OK")
        };
        await alan.UzakHedefiYazAsync("ftp", "ftp://yedek.ornek.com/yedekler", "kullanici", SahteKoruyucu.Onek + "parola");
        await alan.KayitlariYazAsync(Kayit(alan, "k1", "ftp://yedek.ornek.com/yedekler/2026-09-05/k1.zip"));

        await alan.ServisKur(new SahteBulut(), ftp: ftp).DeleteBackupAsync("k1", alan.Iptal);

        Assert.DoesNotContain(ftp.Komutlar, komut => komut.StartsWith("RMD ", StringComparison.Ordinal));
    }

    /// <summary>
    /// Klasör kaldırılamazsa SİLME BAŞARISIZ SAYILMAZ.
    /// </summary>
    /// <remarks>
    /// FAZLA KAPATMA NÖBETÇİSİ: asıl iş yedek dosyasının silinmesidir ve o yapıldı. Klasör
    /// temizliğinin başarısızlığını hata saymak, kotası dolmuş bir kullanıcının budamasını
    /// izin sorunu yüzünden tümden kırardı.
    /// </remarks>
    [Fact]
    public async Task Klasor_kaldirilamazsa_silme_yine_basarili()
    {
        using var alan = new BulutAlani();
        var ftp = new SahteFtp
        {
            KlasorSilmeYaniti = FtpCommandResult.Fail(550, "550 Permission denied.")
        };
        await alan.UzakHedefiYazAsync("ftp", "ftp://yedek.ornek.com/yedekler", "kullanici", SahteKoruyucu.Onek + "parola");
        await alan.KayitlariYazAsync(Kayit(alan, "k1", "ftp://yedek.ornek.com/yedekler/2026-09-05/k1.zip"));

        var hata = await Record.ExceptionAsync(() =>
            alan.ServisKur(new SahteBulut(), ftp: ftp).DeleteBackupAsync("k1", alan.Iptal));

        Assert.Null(hata);
    }

    /// <summary>
    /// Listeleme yapılamadıysa klasör kaldırılmaz — "okuyamadım" ile "boş" aynı şey değildir.
    /// </summary>
    /// <remarks>
    /// Bu, bu oturumda defalarca kapatılan sınıfın burada tekrarlanmaması içindir: başarısız bir
    /// listelemeyi "boş" saymak, DOLU bir klasörü silmeye çalışmak demekti.
    /// </remarks>
    [Fact]
    public async Task Listeleme_basarisizsa_klasor_kaldirilmaz()
    {
        using var alan = new BulutAlani();
        var ftp = new SahteFtp
        {
            ListeYaniti = new FtpListingResult(false, [], 530, "530 Not logged in.")
        };
        await alan.UzakHedefiYazAsync("ftp", "ftp://yedek.ornek.com/yedekler", "kullanici", SahteKoruyucu.Onek + "parola");
        await alan.KayitlariYazAsync(Kayit(alan, "k1", "ftp://yedek.ornek.com/yedekler/2026-09-05/k1.zip"));

        await alan.ServisKur(new SahteBulut(), ftp: ftp).DeleteBackupAsync("k1", alan.Iptal);

        Assert.DoesNotContain(ftp.Komutlar, komut => komut.StartsWith("RMD ", StringComparison.Ordinal));
    }

    private static BackupRecordDto Kayit(BulutAlani alan, string id, string uzakAdres)
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
            TargetType = "ftp",
            RemoteUri = uzakAdres
        };
    }
}
