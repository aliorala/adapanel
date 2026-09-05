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
/// <b>SINIR — bu testlerin asıl konusu:</b> yalnız adı GÜN BİÇİMİNDE olan klasör kaldırılır.
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
