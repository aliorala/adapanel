using System.IO.Compression;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Veritabani dokumlerinin SIKISTIRILARAK saklanmasi (Plesk'in yontemi: dokum arsivde duz metin
/// tutulmaz).
/// <para>
/// DENETIMDE OLCULEN DURUM: tek basina alinan veritabani yedekleri ham <c>.sql</c> olarak
/// saklaniyordu. SQL dokumu duz metindir ve <c>INSERT INTO</c> onekini, sutun adlarini, tarih
/// bicimini satir satir tekrar eder. Gercek bir mysqldump ciktisinda (65,07 MiB) olculdu: ZSTD-3
/// <b>4,31 kat</b> kucultuyor. Sikistirilmadigi icin disk, FTP yukleme suresi ve saklama penceresi
/// boyunca biriken toplam dort katina cikiyordu.
/// </para>
/// <para>
/// Bicim ZSTD'dir (Plesk paritesi: Plesk Obsidian 18.0.40'tan beri GZIP yerine ZSTD kullaniyor).
/// </para>
/// <para>
/// Bu testler "dosya kuculdu mu"yu degil, ozelligin GUVENILIR olmasini korur:
/// (1) sikistir-ac turu birebir ayni baytlari verir (cok tamponlu dosyada da),
/// (2) SIKISTIRILMIS DOKUM GERCEKTEN GERI YUKLENIR -- ImportDumpAsync'e GERCEK .sql dosyasi gider,
/// (3) ESKI duz <c>.sql</c> yedekler calismaya devam eder (karar dosya BASLIGINDAN),
/// (4) GZip'li bir dokum de okunabilir (panel uretmez ama okur),
/// (5) kayittaki etiket yanlis olsa bile dosya basligi kazanir,
/// (6) SIRA: once SIKISTIR sonra SIFRELE (ters olsa kazanc sifir olurdu),
/// (7) ZATEN sikisik bicimler (zip / MSSQL .bak) tekrar sikistirilmaz,
/// (8) BOZUK arsiv net hata verir ve hicbir sey geri yuklenmez.
/// </para>
/// </summary>
public sealed class BackupDumpCompressionTests
{
    private const string Parola = "Musteri-Yedek-Parolasi-2026!";

    // ---------------------------------------------------------------------------------------
    // 1) Sikistir -> ac turu
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]              // bos dosya
    [InlineData(64)]             // tek, kucuk parca
    [InlineData(1 << 20)]        // TAM tampon siniri
    [InlineData((1 << 20) * 3 + 7)] // cok tamponlu (akitarak calisildiginin olcumu)
    public async Task CompressThenDecompress_ReturnsTheExactSameBytes(int uzunluk)
    {
        using var alan = new GeciciAlan();
        var kaynak = alan.DokumDosyasi("kaynak.sql", uzunluk);
        var beklenen = await File.ReadAllBytesAsync(kaynak);

        var sikisik = Path.Combine(alan.Kok, "kaynak.sql.zst");
        var acilan = Path.Combine(alan.Kok, "acilan.sql");

        await BackupDumpCompression.CompressFileAsync(kaynak, sikisik);

        // Sikisik dosya ZSTD olarak TANINIR; duz kaynak tanINMAZ.
        Assert.Equal(BackupDumpCompression.DumpFormat.Zstandard, BackupDumpCompression.DetectFormat(sikisik));
        Assert.Equal(BackupDumpCompression.DumpFormat.None, BackupDumpCompression.DetectFormat(kaynak));
        Assert.True(BackupDumpCompression.IsCompressedFile(sikisik));
        Assert.False(BackupDumpCompression.IsCompressedFile(kaynak));

        await BackupDumpCompression.DecompressFileAsync(sikisik, acilan);
        Assert.Equal(beklenen, await File.ReadAllBytesAsync(acilan));
    }

    /// <summary>
    /// Gercek bir dokumde kazanc OLCULUR. Esik bilincli olarak dusuk (2x) tutuldu: amac
    /// sikistirmanin ACIK ve CALISIYOR oldugunu kanitlamak, belirli bir orani sabitlemek degil.
    /// </summary>
    [Fact]
    public async Task Compression_ActuallyShrinksARealisticSqlDump()
    {
        using var alan = new GeciciAlan();
        var kaynak = alan.DokumDosyasi("dump.sql", 2_000_000);
        var sikisik = Path.Combine(alan.Kok, "dump.sql.zst");

        await BackupDumpCompression.CompressFileAsync(kaynak, sikisik);

        var ham = new FileInfo(kaynak).Length;
        var yeni = new FileInfo(sikisik).Length;
        Assert.True(yeni * 2 < ham, $"Sikistirma beklenen kazanci vermedi: {ham} -> {yeni}");
    }

    // ---------------------------------------------------------------------------------------
    // 2) KARAR TEK KAYNAKTAN: hangi bicim sikistirilir?
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("sql", true)]   // duz metin dokum -- ASIL kazanc burada
    [InlineData("json", true)]  // yeni bir duz bicim eklenirse KENDILIGINDEN sikisir
    [InlineData("zip", false)]  // site/abonelik/panel arsivi: icindeki her girdi zaten Deflate'li
    [InlineData("bak", false)]  // MSSQL: BACKUP ... WITH COMPRESSION ile zaten sikistirilmis
    [InlineData("gz", false)]
    [InlineData("zst", false)]
    [InlineData("zstd", false)]
    [InlineData("", false)]
    public void ShouldCompress_IsDecidedInOnePlace(string bicim, bool beklenen) =>
        Assert.Equal(beklenen, BackupDumpCompression.ShouldCompress(bicim));

    /// <summary>
    /// Uretilen dosya GERCEKTEN ZSTD olmali. Uzanti <c>.zst</c> deyip icine baska bir bicim
    /// koymak, kullanicinin dosyayi <c>zstd -d</c> ile acmaya calisip basarisiz olmasi demekti.
    /// </summary>
    [Fact]
    public async Task ProducedFile_CarriesTheZstandardFrameMagic()
    {
        using var alan = new GeciciAlan();
        var kaynak = alan.DokumDosyasi("dump.sql", 50_000);
        var sikisik = Path.Combine(alan.Kok, "dump.sql.zst");

        await BackupDumpCompression.CompressFileAsync(kaynak, sikisik);

        var baslik = new byte[4];
        await using (var akis = File.OpenRead(sikisik))
        {
            Assert.Equal(4, await akis.ReadAsync(baslik));
        }

        // RFC 8878: 0xFD2FB528 kucuk-endian.
        Assert.Equal(new byte[] { 0x28, 0xB5, 0x2F, 0xFD }, baslik);
    }

    // ---------------------------------------------------------------------------------------
    // 3) Servis seviyesi: yedek gercekten sikisik ve GERCEKTEN GERI GELIYOR
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task DatabaseBackup_IsStoredCompressed()
    {
        using var alan = new GeciciAlan();
        var servis = alan.ServisKur();

        var kayit = await servis.BackupDatabaseAsync(new BackupDatabaseRequest
        {
            Engine = "mariadb",
            DatabaseName = "musteri_wp"
        });

        Assert.True(kayit.Compressed);
        Assert.EndsWith(".sql.zst", kayit.Name, StringComparison.Ordinal);
        Assert.True(BackupDumpCompression.IsCompressedFile(kayit.Path));

        // DUZ KOPYA DISKTE KALMAZ: kalsaydi kazanc sifir olurdu (iki dosya birden dururdu).
        Assert.False(File.Exists(kayit.Path[..^".zst".Length]));

        // Bicim ANLAMSAL kalir: sikistirma bir sarmalayicidir, yedegin turunu degistirmez.
        Assert.Equal("sql", kayit.Format);
        Assert.True(kayit.Size < alan.SonDokumBoyutu, "Kayittaki boyut ham dokumden kucuk olmali.");
    }

    /// <summary>
    /// EN KRITIK TEST. <c>ImportDumpAsync</c> bir <c>.sql</c> DOSYASI bekler (mysql istemcisine
    /// <c>source &lt;yol&gt;</c> olarak verilir). Sikistirilmis baytlar dogrudan verilseydi ozellik
    /// "yedek kuculdu" der ama GERI YUKLEME CALISMAZDI -- yani yedek fiilen yok olurdu.
    /// Burada ice aktarima giden dosyanin baytlari, dokumun ASLIYLA birebir karsilastirilir.
    /// </summary>
    [Fact]
    public async Task CompressedDump_IsDecompressedBeforeImport_AndRestoresByteForByte()
    {
        using var alan = new GeciciAlan();
        var servis = alan.ServisKur();

        var kayit = await servis.BackupDatabaseAsync(new BackupDatabaseRequest
        {
            Engine = "mariadb",
            DatabaseName = "musteri_wp"
        });

        var sonuc = await servis.RestoreAsync(new RestoreBackupRequest { BackupId = kayit.Id });

        Assert.Equal("restored", sonuc.Status);
        Assert.NotNull(alan.IceAktarilanIcerik);

        // 1) Motora GZip baytlari DEGIL, gercek SQL metni gitti.
        Assert.False(alan.IceAktarilanSikisikMiydi, "Motora sikistirilmis baytlar gonderildi!");
        Assert.Equal(alan.UretilenDokum, alan.IceAktarilanIcerik);

        // 2) Dosya adi da motorun bekledigi bicimde (arti: teshiste ne oldugu okunabiliyor).
        Assert.EndsWith(".sql", alan.IceAktarilanYol, StringComparison.OrdinalIgnoreCase);

        // 3) Cozulmus DUZ METIN musteri verisi %TEMP%'te KALMAZ.
        Assert.False(File.Exists(alan.IceAktarilanYol));
    }

    [Fact]
    public async Task EncryptedAndCompressedDump_AlsoRestores()
    {
        using var alan = new GeciciAlan();
        var servis = alan.ServisKur(kayitliParola: Parola);

        var kayit = await servis.BackupDatabaseAsync(new BackupDatabaseRequest
        {
            Engine = "mariadb",
            DatabaseName = "musteri_wp"
        });

        Assert.True(kayit.Compressed);
        Assert.True(kayit.Encrypted);
        Assert.EndsWith(".sql.zst.enc", kayit.Name, StringComparison.Ordinal);

        var sonuc = await servis.RestoreAsync(new RestoreBackupRequest { BackupId = kayit.Id });

        Assert.Equal("restored", sonuc.Status);
        Assert.Equal(alan.UretilenDokum, alan.IceAktarilanIcerik);
    }

    // ---------------------------------------------------------------------------------------
    // 4) SIRA: once SIKISTIR, sonra SIFRELE
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Sifrelenmis veri rastgeleye yakindir ve SIKISMAZ. Sira ters olsaydi dosya HIC kucculmez,
    /// yalnizca CPU harcanirdi -- ozellik "acik" gorunur, kazanc sifir olurdu.
    /// Olcum: sifreli dosya cozulur; icinden GZip cikmali (once sikistirildiginin kaniti).
    /// </summary>
    [Fact]
    public async Task CompressionHappensBeforeEncryption()
    {
        using var alan = new GeciciAlan();
        var servis = alan.ServisKur(kayitliParola: Parola);

        var kayit = await servis.BackupDatabaseAsync(new BackupDatabaseRequest
        {
            Engine = "mariadb",
            DatabaseName = "musteri_wp"
        });

        Assert.True(BackupArchiveCipher.IsEncryptedFile(kayit.Path));

        var cozulen = Path.Combine(alan.Kok, "cozulen.bin");
        await BackupArchiveCipher.DecryptFileAsync(kayit.Path, cozulen, Parola);

        // Sifrenin ALTINDAN ZSTD cercevesi cikmali. Ters sirada burada duz SQL metni olurdu.
        Assert.True(BackupDumpCompression.IsCompressedFile(cozulen), "Sifrenin altindan GZip cikmadi: sira ters!");

        // Ve gercekten kucculmus olmali (sifreli veri sikistirilsaydi boyut ~ayni kalirdi).
        Assert.True(
            new FileInfo(kayit.Path).Length * 2 < alan.SonDokumBoyutu,
            $"Sifreli yedek beklendigi kadar kucculmedi: {new FileInfo(kayit.Path).Length} / {alan.SonDokumBoyutu}");
    }

    // ---------------------------------------------------------------------------------------
    // 5) GERIYE DONUK UYUM: karar DOSYA BASLIGINDAN verilir, kayittaki etiketten degil
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task LegacyPlainSqlBackup_StillRestores()
    {
        using var alan = new GeciciAlan();

        // Panelin sikistirma oncesi urettigi bicim: duz .sql, kayitta Compressed alani bile yok.
        var duzDokum = Path.Combine(alan.Kok, "eski-yedek.sql");
        await File.WriteAllTextAsync(duzDokum, alan.UretilenDokum);

        await alan.DosyaDeposu.WriteAsync("config/backups.json", new[]
        {
            new BackupRecordDto
            {
                Id = "eski",
                Name = "eski-yedek.sql",
                Type = "database",
                Source = "mariadb:musteri_wp",
                Path = duzDokum,
                Format = "sql",
                CreatedAtUtc = DateTime.UtcNow
            }
        });

        var servis = alan.ServisKur();
        var sonuc = await servis.RestoreAsync(new RestoreBackupRequest { BackupId = "eski" });

        Assert.Equal("restored", sonuc.Status);
        Assert.Equal(alan.UretilenDokum, alan.IceAktarilanIcerik);
    }

    /// <summary>
    /// Kayit "sikisik" diyor ama dosya DUZ (kullanici dosyayi elle acmis / kayit bozulmus).
    /// Karar etiketten verilseydi panel duz SQL'i sikisik sanip acmaya kalkar ve saglam bir yedek
    /// "bozuk" ilan edilirdi.
    /// </summary>
    [Fact]
    public async Task RecordSaysCompressedButFileIsPlain_HeaderWins()
    {
        using var alan = new GeciciAlan();

        var duzDokum = Path.Combine(alan.Kok, "yanlis-etiket.sql");
        await File.WriteAllTextAsync(duzDokum, alan.UretilenDokum);

        await alan.DosyaDeposu.WriteAsync("config/backups.json", new[]
        {
            new BackupRecordDto
            {
                Id = "etiket",
                Name = "yanlis-etiket.sql.zst",
                Type = "database",
                Source = "mariadb:musteri_wp",
                Path = duzDokum,
                Format = "sql",
                Compressed = true, // YANLIS etiket
                CreatedAtUtc = DateTime.UtcNow
            }
        });

        var servis = alan.ServisKur();
        var sonuc = await servis.RestoreAsync(new RestoreBackupRequest { BackupId = "etiket" });

        Assert.Equal("restored", sonuc.Status);
        Assert.Equal(alan.UretilenDokum, alan.IceAktarilanIcerik);
    }

    /// <summary>
    /// GZIP'LI BIR DOKUM DE OKUNUR. Panel artik GZip URETMEZ (bicim ZSTD'ye cevrildi), ama okuma
    /// yolunda taniniyor: bedeli .NET'te zaten bulunan GZipStream'e bir dallanma, karsiligi ise ara
    /// bir surumle uretilmis ya da yonetici tarafindan elle <c>gzip</c>'lenmis bir dokumun SESSIZCE
    /// geri yuklenemez olmamasi. Bir yedegi okuyamamanin bedeli birkac satir kodun bedelinden kat
    /// kat yuksektir.
    /// </summary>
    [Fact]
    public async Task GzipCompressedDump_IsStillReadable()
    {
        using var alan = new GeciciAlan();

        // Panelin URETMEDIGI ama okuyabildigi bicim: elle gzip'lenmis bir dokum.
        var gzipli = Path.Combine(alan.Kok, "elle-gziplenmis.sql.gz");
        await using (var kaynak = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(alan.UretilenDokum)))
        await using (var hedef = File.Create(gzipli))
        await using (var gzip = new GZipStream(hedef, CompressionLevel.Optimal, leaveOpen: true))
        {
            await kaynak.CopyToAsync(gzip);
        }

        Assert.Equal(BackupDumpCompression.DumpFormat.GZip, BackupDumpCompression.DetectFormat(gzipli));

        await alan.DosyaDeposu.WriteAsync("config/backups.json", new[]
        {
            new BackupRecordDto
            {
                Id = "gzipli",
                Name = "elle-gziplenmis.sql.gz",
                Type = "database",
                Source = "mariadb:musteri_wp",
                Path = gzipli,
                Format = "sql",
                CreatedAtUtc = DateTime.UtcNow
            }
        });

        var servis = alan.ServisKur();
        var sonuc = await servis.RestoreAsync(new RestoreBackupRequest { BackupId = "gzipli" });

        Assert.Equal("restored", sonuc.Status);
        Assert.False(alan.IceAktarilanSikisikMiydi, "Motora sikistirilmis baytlar gonderildi!");
        Assert.Equal(alan.UretilenDokum, alan.IceAktarilanIcerik);
    }

    // ---------------------------------------------------------------------------------------
    // 6) BOZUK arsiv: net hata, hicbir sey geri yuklenmez
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// BU TESTIN KORUDUGU SEY, BICIM DEGISIKLIGINDE NEREDEYSE SESSIZCE KAYBOLUYORDU.
    /// <para>
    /// ZSTD'nin cerceve saglama toplami (checksum) VARSAYILAN OLARAK KAPALIDIR; GZip'in sonundaki
    /// CRC32 ise her zaman vardir. Olculdu: saglama kapaliyken sikistirilmis govdeye atilan 40
    /// rastgele bit cevirmesinin <b>19'u hicbir hata vermeden SESSIZCE BOZUK SQL uretti</b> --
    /// yani bit curumus bir yedek, musterinin canli veritabanina cop olarak "geri yuklenirdi".
    /// Saglama acikken 40/40 yakalandi (bedeli 4 bayt).
    /// </para>
    /// <para>
    /// Bu test govdenin BIRCOK yerini ayri ayri bozar: tek bir bayta bakan bir test, saglama
    /// kapatilsa bile sansa gecebilirdi.
    /// </para>
    /// </summary>
    [Fact]
    public async Task CorruptedCompressedBytes_AreAlwaysDetected_NeverSilentlyRestoredAsGarbage()
    {
        using var alan = new GeciciAlan();
        var kaynak = alan.DokumDosyasi("dump.sql", 300_000);
        var beklenen = await File.ReadAllBytesAsync(kaynak);
        var sikisik = Path.Combine(alan.Kok, "dump.sql.zst");
        await BackupDumpCompression.CompressFileAsync(kaynak, sikisik);

        var saglam = await File.ReadAllBytesAsync(sikisik);
        var bozuk = Path.Combine(alan.Kok, "bozuk.zst");
        var acilan = Path.Combine(alan.Kok, "acilan.sql");
        var sessizBozulma = new List<int>();

        // Cerceve basligini (ilk 6 bayt) atlayip govdeye dagilmis 20 ayri noktayi boz.
        for (var adim = 0; adim < 20; adim++)
        {
            var yer = 6 + (adim * (saglam.Length - 8) / 20);
            var kopya = (byte[])saglam.Clone();
            kopya[yer] ^= 0xFF;
            await File.WriteAllBytesAsync(bozuk, kopya);

            try
            {
                await BackupDumpCompression.DecompressFileAsync(bozuk, acilan);
            }
            catch (AppException hata)
            {
                Assert.Equal("backup.archive_corrupt", hata.MessageKey);
                continue; // Yakalandi: dogru davranis.
            }

            // Hata VERMEDIYSE, cikan icerik ASLIYLA birebir ayni olmak ZORUNDA.
            if (!beklenen.SequenceEqual(await File.ReadAllBytesAsync(acilan)))
            {
                sessizBozulma.Add(yer);
            }
        }

        Assert.True(
            sessizBozulma.Count == 0,
            $"Bozuk arsiv SESSIZCE farkli veri uretti ({sessizBozulma.Count}/20 nokta: " +
            $"{string.Join(", ", sessizBozulma)}). ZSTD saglama toplami kapali olmali -- bu, bozuk " +
            "bir yedegin musterinin veritabanina cop olarak yazilmasi demektir.");
    }

    /// <summary>
    /// Kesilmis/bozulmus bir ZSTD cercevesi NET bir hata vermeli. Sarmalanmasaydi kullanici
    /// "Dictionary mismatch" gibi hicbir sey anlatmayan bir ic hata gorurdu (middleware bunu
    /// common.unexpected_error'a cevirirdi) ve saglam sanip yedegini silebilirdi.
    /// </summary>
    [Fact]
    public async Task CorruptCompressedArchive_IsRejectedClearly_AndNothingIsImported()
    {
        using var alan = new GeciciAlan();
        var servis = alan.ServisKur();

        var kayit = await servis.BackupDatabaseAsync(new BackupDatabaseRequest
        {
            Engine = "mariadb",
            DatabaseName = "musteri_wp"
        });

        // Cercevenin ortasindan tek bir bit cevrilir (imza saglam kalir, govde bozulur).
        var ham = await File.ReadAllBytesAsync(kayit.Path);
        ham[ham.Length / 2] ^= 0xFF;
        await File.WriteAllBytesAsync(kayit.Path, ham);

        var hata = await Assert.ThrowsAsync<AppException>(
            () => servis.RestoreAsync(new RestoreBackupRequest { BackupId = kayit.Id }));

        // ANAHTAR DEGISTI (icerik damgasi eklendikten sonra) VE BU BILINCLI.
        //
        // Test dosyayi OLUSTURULDUKTAN SONRA bozuyor; icerik damgasi tam olarak bunu olcer ve
        // sikistirma acilmadan once, daha ucuza yakalar. Anahtarin isi kullaniciyi DOGRU ize
        // surumek: "arsiv bozuk" dosyanin hep boyle oldugunu dusundururdu, oysa gercek "dosya
        // yazildiktan SONRA degisti" -- kullaniciyi diski ve uzak hedefteki kopyayi denetlemeye
        // yonlendiren tek dogru cumle budur.
        //
        // backup.archive_corrupt OLU DEGIL: damgasiz eski yedeklerde ve bastan bozuk yazilmis
        // arsivlerde hala o anahtar doner.
        Assert.Equal("backup.integrity_mismatch", hata.MessageKey);

        // HICBIR SEY ICE AKTARILMADI: yarim bir dokumu veritabanina yazmak, hic yazmamaktan
        // cok daha pahaliya mal olurdu.
        Assert.Null(alan.IceAktarilanIcerik);
    }

    /// <summary>
    /// Tersi de dogru olmali: kayit "sikisik degil" diyor ama dosya sikisik (kayit eski surumden
    /// kalmis / elle tasinmis). Etikete guvenilseydi sikisik baytlar ham SQL sanilip motora
    /// gonderilirdi.
    /// </summary>
    [Fact]
    public async Task RecordSaysPlainButFileIsCompressed_HeaderWins()
    {
        using var alan = new GeciciAlan();

        var duzDokum = Path.Combine(alan.Kok, "gizli-sikisik.sql");
        await File.WriteAllTextAsync(duzDokum, alan.UretilenDokum);
        var sikisik = duzDokum + ".zst";
        await BackupDumpCompression.CompressFileAsync(duzDokum, sikisik);

        await alan.DosyaDeposu.WriteAsync("config/backups.json", new[]
        {
            new BackupRecordDto
            {
                Id = "gizli",
                Name = "gizli-sikisik.sql",
                Type = "database",
                Source = "mariadb:musteri_wp",
                Path = sikisik,
                Format = "sql",
                Compressed = false, // YANLIS etiket
                CreatedAtUtc = DateTime.UtcNow
            }
        });

        var servis = alan.ServisKur();
        var sonuc = await servis.RestoreAsync(new RestoreBackupRequest { BackupId = "gizli" });

        Assert.Equal("restored", sonuc.Status);
        Assert.False(alan.IceAktarilanSikisikMiydi, "Motora sikistirilmis baytlar gonderildi!");
        Assert.Equal(alan.UretilenDokum, alan.IceAktarilanIcerik);
    }

    // ---------------------------------------------------------------------------------------
    // 6) ZATEN SIKISIK bicimler tekrar sikistirilmaz
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// MSSQL yedegi: panel bunu <c>BACKUP DATABASE ... WITH COMPRESSION</c> ile alir, yani SQL
    /// Server dosyayi kendi sikistirmasiyla yazmistir. Olculdu: zaten sikistirilmis veriyi tekrar
    /// GZip'lemenin kazanci %0,29. Ustelik <c>RESTORE ... FROM DISK</c> gercek bir .bak bekler;
    /// sarmalamak, kazanc sifirken geri yukleme riski almak olurdu.
    /// </summary>
    [Fact]
    public async Task MsSqlBakBackup_IsNotCompressed()
    {
        using var alan = new GeciciAlan();
        var servis = alan.ServisKur();

        var kayit = await servis.BackupDatabaseAsync(new BackupDatabaseRequest
        {
            Engine = "mssql",
            DatabaseName = "MusteriDb"
        });

        Assert.False(kayit.Compressed);
        Assert.EndsWith(".bak", kayit.Name, StringComparison.Ordinal);
        Assert.False(BackupDumpCompression.IsCompressedFile(kayit.Path));
    }

    /// <summary>
    /// Site arsivi zaten Deflate'li bir .zip'tir; ustune GZip sarmak CPU harcar, boyut kazandirmaz.
    /// Ayrica arsive gozatma/ayrintili geri yukleme yollari dosyayi ZIP olarak acar.
    /// </summary>
    [Fact]
    public async Task SiteZipBackup_IsNotDoubleCompressed()
    {
        using var alan = new GeciciAlan();
        var siteKoku = alan.KlasorAc("wwwroot", "shop");
        await File.WriteAllTextAsync(Path.Combine(siteKoku, "index.php"), "MUSTERI ICERIGI");

        var servis = alan.ServisKur();
        var kayit = await servis.BackupSiteAsync(new BackupSiteRequest
        {
            SiteName = "shop.example.com",
            IncludeFiles = true,
            IncludeConfig = false
        });

        Assert.False(kayit.Compressed);
        Assert.EndsWith(".zip", kayit.Name, StringComparison.Ordinal);
        // Hala ZIP olarak acilabiliyor (gozatma ve ayrintili geri yukleme buna dayanir).
        using var arsiv = ZipFile.OpenRead(kayit.Path);
        Assert.Contains(arsiv.Entries, girdi => girdi.FullName.EndsWith("index.php", StringComparison.OrdinalIgnoreCase));
    }

    // =======================================================================================

    private sealed class GeciciAlan : IDisposable
    {
        public GeciciAlan()
        {
            Kok = Path.Combine(Path.GetTempPath(), $"adapanel-sikistirma-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Kok);
            DosyaDeposu = new JsonFileStore(
                Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Kok }));
            SiteKoku = KlasorAc("wwwroot", "shop");
            UretilenDokum = SahteDokum(400);
        }

        public string Kok { get; }

        public string SiteKoku { get; private set; }

        public JsonFileStore DosyaDeposu { get; }

        /// <summary>Sahte motorun urettigi dokumun METNI (geri yuklemede birebir beklenen icerik).</summary>
        public string UretilenDokum { get; }

        public long SonDokumBoyutu { get; private set; }

        public string? IceAktarilanIcerik { get; private set; }

        public string IceAktarilanYol { get; private set; } = string.Empty;

        public bool IceAktarilanSikisikMiydi { get; private set; }

        public string KlasorAc(params string[] parcalar)
        {
            var yol = Path.Combine([Kok, .. parcalar]);
            Directory.CreateDirectory(yol);
            SiteKoku = yol;
            return yol;
        }

        /// <summary>Gercek mysqldump ciktisinin karakteri: tekrar eden onekler + degisen degerler.</summary>
        private static string SahteDokum(int satir)
        {
            var yazici = new System.Text.StringBuilder();
            yazici.AppendLine("-- MariaDB dump 10.19  Host: 127.0.0.1    Database: musteri_wp");
            yazici.AppendLine("/*!40101 SET @OLD_CHARACTER_SET_CLIENT=@@CHARACTER_SET_CLIENT */;");
            yazici.AppendLine("CREATE TABLE `wp_posts` (`ID` bigint(20) unsigned NOT NULL AUTO_INCREMENT) ENGINE=InnoDB;");
            for (var i = 0; i < satir; i++)
            {
                yazici.AppendLine(
                    $"INSERT INTO `wp_posts` VALUES ({i},'2026-08-17 10:00:00','<p>Musteri icerigi {i}</p>','publish','yazi-{i}');");
            }

            yazici.AppendLine("/*!40101 SET CHARACTER_SET_CLIENT=@OLD_CHARACTER_SET_CLIENT */;");
            return yazici.ToString();
        }

        /// <summary>Sikistirilabilir ama gercekci bir dokum dosyasi uretir.</summary>
        public string DokumDosyasi(string ad, int enAzUzunluk)
        {
            var yol = Path.Combine(Kok, ad);
            if (enAzUzunluk == 0)
            {
                File.WriteAllBytes(yol, []);
                return yol;
            }

            var metin = new System.Text.StringBuilder();
            var i = 0;
            while (metin.Length < enAzUzunluk)
            {
                metin.AppendLine($"INSERT INTO `wp_postmeta` VALUES ({i},{i % 997},'_yoast_wpseo_metadesc','aciklama {i}');");
                i++;
            }

            File.WriteAllText(yol, metin.ToString(0, enAzUzunluk));
            return yol;
        }

        public BackupService ServisKur(string? kayitliParola = null)
        {
            BackupEncryptionStore? kasa = null;
            if (kayitliParola is not null)
            {
                kasa = new BackupEncryptionStore(DosyaDeposu, new SahteKoruyucu());
                kasa.SetPasswordAsync(kayitliParola).GetAwaiter().GetResult();
            }

            return new BackupService(
                new SahteSiteServisi(SiteKoku),
                new SahteVeritabaniServisi(Disaridan, Iceriden),
                null!,
                new SahteTercihServisi(Path.Combine(Kok, "backups")),
                null!,
                DosyaDeposu,
                null!,
                kasa);
        }

        /// <summary>Sahte <c>ExportDumpAsync</c>: istenen yola gercek bir dokum metni yazar.</summary>
        private void Disaridan(DatabaseDumpRequest istek)
        {
            // MSSQL'de panel .bak uretir; icerigi SQL Server'in kendi (sikistirilmis) bicimidir.
            // Burada yalnizca "zaten sikisik, duz metin degil" olmasi onemli.
            if (istek.Engine.Equals("mssql", StringComparison.OrdinalIgnoreCase))
            {
                var rastgele = new byte[64 * 1024];
                Random.Shared.NextBytes(rastgele);
                File.WriteAllBytes(istek.FilePath, rastgele);
            }
            else
            {
                File.WriteAllText(istek.FilePath, UretilenDokum);
            }

            SonDokumBoyutu = new FileInfo(istek.FilePath).Length;
        }

        /// <summary>Sahte <c>ImportDumpAsync</c>: motora NE VERILDIGINI kaydeder.</summary>
        private void Iceriden(DatabaseDumpRequest istek)
        {
            IceAktarilanYol = istek.FilePath;
            var ham = File.ReadAllBytes(istek.FilePath);
            // ZSTD (28 B5 2F FD) ya da GZip (1F 8B) -- ikisi de "motora ham SQL gitmedi" demektir.
            IceAktarilanSikisikMiydi =
                (ham.Length > 3 && ham[0] == 0x28 && ham[1] == 0xB5 && ham[2] == 0x2F && ham[3] == 0xFD)
                || (ham.Length > 1 && ham[0] == 0x1F && ham[1] == 0x8B);
            IceAktarilanIcerik = System.Text.Encoding.UTF8.GetString(ham);
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

    private sealed class SahteKoruyucu : ISecretProtector
    {
        public string Protect(string plaintext) =>
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext));

        public string Unprotect(string protectedValue) =>
            System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue));
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

    private sealed class SahteVeritabaniServisi(
        Action<DatabaseDumpRequest> disariAktarim,
        Action<DatabaseDumpRequest> iceAktarim) : IDatabaseService
    {
        public Task<DatabaseDumpResultDto> ExportDumpAsync(DatabaseDumpRequest request, CancellationToken cancellationToken = default)
        {
            disariAktarim(request);
            return Task.FromResult(new DatabaseDumpResultDto
            {
                Engine = request.Engine,
                DatabaseName = request.DatabaseName,
                FilePath = request.FilePath,
                Size = new FileInfo(request.FilePath).Length
            });
        }

        public Task<DatabaseDumpResultDto> ImportDumpAsync(DatabaseDumpRequest request, CancellationToken cancellationToken = default)
        {
            iceAktarim(request);
            return Task.FromResult(new DatabaseDumpResultDto
            {
                Engine = request.Engine,
                DatabaseName = request.DatabaseName,
                FilePath = request.FilePath
            });
        }

        public Task<IReadOnlyCollection<DatabaseEngineStatusDto>> GetEnginesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DatabaseEngineStatusDto> RegisterCredentialsAsync(RegisterDatabaseCredentialsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DatabaseWorkspaceDto> GetWorkspaceAsync(string engine, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DatabaseWorkspaceDto> CreateDatabaseAsync(CreateDatabaseRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DatabaseWorkspaceDto> ImportDatabaseAsync(ImportDatabaseRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteDatabaseAsync(string engine, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DatabaseWorkspaceDto> CreateUserAsync(CreateDatabaseUserRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteUserAsync(string engine, string userName, string host, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DatabaseWorkspaceDto> SetUserPasswordAsync(SetDatabaseUserPasswordRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DatabaseWorkspaceDto> GrantAccessAsync(GrantDatabaseAccessRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DatabaseTransferResultDto> TransferDatabaseAsync(DatabaseTransferRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DatabaseDetailDto> GetDetailAsync(string engine, string databaseName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DatabaseCheckResultDto> CheckAndRepairAsync(string engine, string databaseName, IReadOnlyCollection<string> repairTables, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AdaPanel.Core.Dtos.Databases.DatabaseHardeningReport> HardenAdminAccountsAsync(string engine, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
