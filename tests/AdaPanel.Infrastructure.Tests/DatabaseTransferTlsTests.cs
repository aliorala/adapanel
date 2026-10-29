using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Database;
using AdaPanel.Infrastructure.Migration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// VERİTABANI GÖÇÜNDE BAĞLANTI ŞİFRELEMESİ: uzak kaynağa parola şifresiz gidiyor muydu.
/// </summary>
/// <remarks>
/// <para>
/// <b>ÖLÇÜLEN AÇIK.</b> Taşımanın <c>mysqldump</c> komut satırında hiç <c>--ssl-mode</c> yoktu.
/// MySQL/MariaDB istemcilerinin varsayılanı <c>PREFERRED</c>'dır — sunucu TLS sunmuyorsa istemci
/// ŞİFRESİZ devam eder. Uzak bir kaynaktan çekilen göçte veritabanı parolası ve tüm tablo içeriği
/// açık metin akabilirdi.
/// </para>
/// <para>
/// <b>ÖLÇÜM NOKTASI KURULAN KOMUT SATIRIDIR</b>, kaynak metni değil: bayrağın gerçekten
/// <c>mysqldump</c>'a gidip gitmediğini ve DOĞRU YERDE (veritabanı adından önce) durduğunu ancak
/// dizenin kendisi söyler.
/// </para>
/// <para>
/// <b>FAZLA KAPATMA NÖBETÇİLERİ ayrı ayrı var.</b> <c>--ssl-mode=REQUIRED</c> her koşulda
/// zorlansaydı iki meşru yol ölürdü: (1) loopback'teki yerel motordan göç — araya girecek ağ
/// yokken TLS'i açık olmayan motorlarda göç kırılırdı, (2) TLS desteklemeyen eski bir sunucudan
/// göç eden kullanıcının BİLİNÇLİ tercihi.
/// </para>
/// </remarks>
public sealed class DatabaseTransferTlsTests
{
    // =====================================================================================
    // 1) ASIL AÇIK — uzak kaynağa şifresiz bağlanılmaz
    // =====================================================================================

    /// <summary>
    /// UZAK kaynak + kullanıcı bir şey söylememiş → <c>--ssl-mode=REQUIRED</c> komut satırına GİRER.
    /// </summary>
    /// <remarks>
    /// MUTASYON: bayrak kaldırılırsa (ya da <c>Coz</c> uzak konakta <c>Disabled</c> döndürürse)
    /// bu test düşer.
    /// </remarks>
    [Fact]
    public void Uzak_kaynakta_sifreleme_ZORUNLU_kilinir()
    {
        var argumanlar = DumpKomutu("203.0.113.10", istenenKip: string.Empty);

        Assert.Contains("--ssl-mode=REQUIRED", argumanlar, StringComparison.Ordinal);
    }

    /// <summary>
    /// Bayrak, veritabanı adından (pozisyonel argüman) ÖNCE gelmeli.
    /// </summary>
    /// <remarks>
    /// Sonra gelseydi <c>mysqldump</c> onu "şu tabloyu da al" diye okurdu: komut satırı hem
    /// şifrelenmemiş olur hem de sessizce yanlış bir dump üretirdi. Sıra bu yüzden ölçülüyor.
    /// </remarks>
    [Fact]
    public void Sifreleme_bayragi_veritabani_adindan_ONCE_gelir()
    {
        var argumanlar = DumpKomutu("203.0.113.10", istenenKip: MySqlTransferTls.Auto);

        var bayrak = argumanlar.IndexOf("--ssl-mode=REQUIRED", StringComparison.Ordinal);
        var veritabani = argumanlar.IndexOf("\"kaynak_db\"", StringComparison.Ordinal);

        Assert.True(bayrak >= 0 && veritabani >= 0, argumanlar);
        Assert.True(bayrak < veritabani, argumanlar);
    }

    /// <summary>
    /// Tanınmayan kip sessizce ŞİFRESİZE düşmez — güvenli varsayılana döner.
    /// </summary>
    /// <remarks>
    /// Bu, düzeltmenin kapatmaya çalıştığı hatanın kendisidir: bilinmeyen bir değerin "herhâlde
    /// istemiyordur" diye yorumlanması. Eski istemciler alanı hiç göndermez, yeni bir arayüz yazım
    /// hatası yapabilir; ikisi de korumasız kalmamalı.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("evet")]
    [InlineData("preferred")]
    public void Taninmayan_kip_uzak_kaynakta_SIFRESIZE_DUSMEZ(string kip)
    {
        Assert.Contains("--ssl-mode=REQUIRED", DumpKomutu("db.eski-sunucu.example", kip), StringComparison.Ordinal);
    }

    // =====================================================================================
    // 2) FAZLA KAPATMA NÖBETÇİSİ — loopback göçü KIRILMAZ
    // =====================================================================================

    /// <summary>
    /// FAZLA KAPATMA NÖBETÇİSİ: yerel/loopback kaynakta şifreleme ZORLANMAZ.
    /// </summary>
    /// <remarks>
    /// Loopback'te araya girecek ağ yoktur; TLS zorlamak hiçbir şey korumaz ama TLS'i açık olmayan
    /// yerel motorlarda çalışan göçü kırardı. <c>.</c> Windows'ta adlandırılmış kanaldır.
    /// </remarks>
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.1.2.3")]
    [InlineData("localhost")]
    [InlineData("LocalHost")]
    [InlineData("::1")]
    [InlineData("[::1]")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData(".")]
    public void Yerel_kaynakta_sifreleme_ZORLANMAZ(string konak)
    {
        Assert.DoesNotContain("--ssl-mode", DumpKomutu(konak, MySqlTransferTls.Auto), StringComparison.Ordinal);
    }

    /// <summary>
    /// FAZLA KAPATMA NÖBETÇİSİ: kullanıcı AÇIKÇA "şifrelemesiz" dediyse zorlanmaz.
    /// </summary>
    /// <remarks>
    /// Bu, panelin sessiz düşürmesinden farklıdır: kullanıcı TLS desteklemeyen kendi eski
    /// sunucusundan çekiyor olabilir ve bu onun kararıdır. Kapatılan şey korumanın sağlanmadığı
    /// hâlde sağlanmış GÖRÜNMESİYDİ.
    /// </remarks>
    [Fact]
    public void Kullanici_SIFRELEMESIZ_dediginde_uzak_kaynakta_da_zorlanmaz()
    {
        var argumanlar = DumpKomutu("203.0.113.10", MySqlTransferTls.Disabled);

        // Bayrak HİÇ yazılmaz: `--ssl-mode=DISABLED` bile yazılmamalı, çünkü onu tanımayan eski
        // istemcilerde (MariaDB 11.4 öncesi) taşıma "unknown variable" ile ölürdü.
        Assert.DoesNotContain("--ssl-mode", argumanlar, StringComparison.Ordinal);

        // Komutun geri kalanı bozulmadı: göç hâlâ çalışır bir komut satırı üretiyor.
        Assert.Contains("--host=203.0.113.10", argumanlar, StringComparison.Ordinal);
        Assert.Contains("--single-transaction", argumanlar, StringComparison.Ordinal);
        Assert.Contains("\"kaynak_db\"", argumanlar, StringComparison.Ordinal);
    }

    /// <summary>Kullanıcı yerel kaynakta da şifreleme İSTEYEBİLİR; tercihi uygulanır.</summary>
    [Fact]
    public void Kullanici_yerelde_de_sifreleme_isteyebilir()
    {
        Assert.Contains(
            "--ssl-mode=REQUIRED",
            DumpKomutu("127.0.0.1", MySqlTransferTls.Required),
            StringComparison.Ordinal);
    }

    // =====================================================================================
    // 3) HEDEF (import) tarafı
    // =====================================================================================

    /// <summary>
    /// FAZLA KAPATMA NÖBETÇİSİ: hedef import loopback olduğu için bayrak EKLENMEZ.
    /// </summary>
    /// <remarks>
    /// ÖLÇÜLDÜ: hedef profilin konağı <c>DatabaseProfiles</c>te sabit <c>127.0.0.1</c>. Burada TLS
    /// zorlamak, panelin kendi motoruna yapılan her taşımayı gereksiz yere kırma riskidir.
    /// </remarks>
    [Fact]
    public void Hedef_import_loopbackte_bayrak_EKLEMEZ()
    {
        var argumanlar = DatabaseService.BuildTransferImportArguments(
            "127.0.0.1",
            3306,
            "root",
            "hedef_db",
            MySqlTransferTls.Coz("127.0.0.1", MySqlTransferTls.Auto));

        Assert.DoesNotContain("--ssl-mode", argumanlar, StringComparison.Ordinal);
        Assert.Contains("\"hedef_db\"", argumanlar, StringComparison.Ordinal);
    }

    /// <summary>Hedef bir gün uzağa taşınırsa aynı kural KENDİLİĞİNDEN şifrelemeye geçer.</summary>
    [Fact]
    public void Hedef_uzaga_tasinirsa_bayrak_kendiliginden_eklenir()
    {
        var argumanlar = DatabaseService.BuildTransferImportArguments(
            "10.0.0.9",
            3306,
            "root",
            "hedef_db",
            MySqlTransferTls.Coz("10.0.0.9", MySqlTransferTls.Auto));

        Assert.Contains("--ssl-mode=REQUIRED", argumanlar, StringComparison.Ordinal);
    }

    // =====================================================================================
    // 4) KULLANICININ SEÇİMİ GÖÇ YOLUNDAN GEÇİYOR MU
    // =====================================================================================

    /// <summary>
    /// Göç formundaki şifreleme tercihi taşıma isteğine GERÇEKTEN ulaşır.
    /// </summary>
    /// <remarks>
    /// Zincirin bu halkası kopsaydı arayüzdeki seçim hiçbir şey yapmaz, kullanıcı "şifrelemesiz"
    /// dediği hâlde göç anlaşılmaz bir TLS hatasıyla ölürdü. Ölçüm gerçek servis üzerinden yapılır:
    /// <see cref="SiteMigrationService.MigrateSiteAsync"/> çağrılır ve taşıma servisine ULAŞAN
    /// istek okunur.
    /// </remarks>
    [Fact]
    public async Task Goc_formundaki_sifreleme_tercihi_tasima_istegine_ULASIR()
    {
        var veritabani = new IsteğiYakalayanVeritabani();
        var servis = new SiteMigrationService(
            new PatlayanSaglama(),
            new PatlayanDosyaGocu(),
            veritabani,
            new PatlayanSiteServisi(),
            NullLogger<SiteMigrationService>.Instance);

        var sonuc = await servis.MigrateSiteAsync(
            new MigrateSiteRequest
            {
                SiteName = "ornek.test",
                CreateSite = false,
                PullFiles = false,
                TransferDatabase = true,
                DbHost = "203.0.113.10",
                DbUserName = "kaynak_kullanici",
                DbPassword = "gizli",
                SourceDatabase = "kaynak_db",
                DbTlsMode = MySqlTransferTls.Disabled
            },
            actorUserId: null,
            ipAddress: null);

        Assert.True(sonuc.OverallSuccess);
        Assert.True(Assert.Single(sonuc.Steps).Success);
        Assert.Equal(1, veritabani.DatabaseCreations);
        Assert.Equal(1, veritabani.UserCreations);
        Assert.NotNull(veritabani.SonIstek);
        Assert.Equal(MySqlTransferTls.Disabled, veritabani.SonIstek!.SourceTlsMode);
    }

    // =====================================================================================
    // 5) BAŞARISIZLIĞIN SEBEBİ SÖYLENİYOR MU
    // =====================================================================================

    /// <summary>
    /// Şifreleme kaynaklı başarısızlıklar TANINIR — kullanıcı sebebi öğrenebilsin.
    /// </summary>
    /// <remarks>
    /// Metinler gerçek istemci çıktılarıdır: sunucu TLS sunmuyor (MySQL 2026), sunucu şifresiz
    /// bağlantıyı yasaklıyor (MySQL 3159) ve istemci <c>--ssl-mode</c> tanımıyor (eski MariaDB).
    /// </remarks>
    [Theory]
    [InlineData("ERROR 2026 (HY000): SSL connection error: protocol version mismatch")]
    [InlineData("mysqldump: unknown variable 'ssl-mode=REQUIRED'")]
    [InlineData("ERROR 3159 (HY000): Connections using insecure transport are prohibited while --require_secure_transport=ON.")]
    [InlineData("mysqldump: unknown option '--ssl-mode'")]
    public void Sifreleme_kaynakli_hata_TANINIR(string hataMetni)
    {
        Assert.True(MySqlTransferTls.TlsKaynakliHata(hataMetni), hataMetni);
    }

    /// <summary>
    /// FAZLA KAPATMA NÖBETÇİSİ: şifrelemeyle ilgisi olmayan hata TLS hatası SAYILMAZ.
    /// </summary>
    /// <remarks>
    /// Yanlış etiketlenen bir yetki hatası, kullanıcıyı parolasını düzeltmek yerine şifrelemeyi
    /// kapatmaya iterdi — yani hem sorunu çözmez hem korumayı söktürürdü. "ssl" harflerini aramak
    /// yeterli olsaydı, adında ssl geçen bir veritabanı bu tuzağı kurardı; alt satır onu ölçüyor.
    /// </remarks>
    [Theory]
    [InlineData("mysqldump: Got error: 1045: Access denied for user 'root'@'1.2.3.4' (using password: YES)")]
    [InlineData("mysqldump: Got error: 2003: Can't connect to MySQL server on '203.0.113.10' (10060)")]
    [InlineData("mysqldump: Got error: 1049: Unknown database 'ssl_musteri_db'")]
    [InlineData("")]
    public void Ilgisiz_hata_TLS_hatasi_SAYILMAZ(string hataMetni)
    {
        Assert.False(MySqlTransferTls.TlsKaynakliHata(hataMetni), hataMetni);
    }

    // =====================================================================================
    // Yardımcılar
    // =====================================================================================

    private static string DumpKomutu(string konak, string istenenKip)
        => DatabaseService.BuildTransferDumpArguments(
            konak,
            3306,
            "kaynak_kullanici",
            "kaynak_db",
            @"C:\Temp\gecici.sql",
            MySqlTransferTls.Coz(konak, istenenKip));

    private sealed class IsteğiYakalayanVeritabani : IDatabaseService
    {
        public DatabaseTransferRequest? SonIstek { get; private set; }
        public int DatabaseCreations { get; private set; }
        public int UserCreations { get; private set; }

        public Task<DatabaseTransferResultDto> TransferDatabaseAsync(DatabaseTransferRequest request, CancellationToken cancellationToken = default)
        {
            SonIstek = request;
            return Task.FromResult(new DatabaseTransferResultDto
            {
                Engine = request.Engine,
                SourceDatabase = request.SourceDatabase,
                TargetDatabase = request.TargetDatabase
            });
        }

        // This TLS propagation test models successful provisioning before transfer can start.
        public Task<DatabaseWorkspaceDto> CreateDatabaseAsync(CreateDatabaseRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DatabaseCreations++;
            return Task.FromResult(new DatabaseWorkspaceDto());
        }
        public Task<DatabaseWorkspaceDto> CreateUserAsync(CreateDatabaseUserRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UserCreations++;
            return Task.FromResult(new DatabaseWorkspaceDto());
        }

        public Task<IReadOnlyCollection<DatabaseEngineStatusDto>> GetEnginesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseEngineStatusDto> RegisterCredentialsAsync(RegisterDatabaseCredentialsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseWorkspaceDto> GetWorkspaceAsync(string engine, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseWorkspaceDto> ImportDatabaseAsync(ImportDatabaseRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteDatabaseAsync(string engine, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteUserAsync(string engine, string userName, string host, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseWorkspaceDto> SetUserPasswordAsync(SetDatabaseUserPasswordRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseWorkspaceDto> GrantAccessAsync(GrantDatabaseAccessRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseDumpResultDto> ExportDumpAsync(DatabaseDumpRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseDumpResultDto> ImportDumpAsync(DatabaseDumpRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseDetailDto> GetDetailAsync(string engine, string databaseName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseCheckResultDto> CheckAndRepairAsync(string engine, string databaseName, IReadOnlyCollection<string> repairTables, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseHardeningReport> HardenAdminAccountsAsync(string engine, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class PatlayanDosyaGocu : IFileMigrationService
    {
        public Task<AdaPanel.Core.Dtos.Sites.RemoteFileMigrationResult> PullAsync(AdaPanel.Core.Dtos.Sites.RemoteFileMigrationRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class PatlayanSaglama : ISiteProvisioningService
    {
        public Task<AdaPanel.Core.Dtos.Sites.SiteDto> SetDocumentRootAsync(string siteName, string physicalPath, Func<AdaPanel.Core.Dtos.Sites.SiteDto, CancellationToken, Task> beforeMutation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AdaPanel.Core.Dtos.Sites.SiteDto> EnableFrameworkRewriteAsync(string siteName, Func<AdaPanel.Core.Dtos.Sites.SiteDto, CancellationToken, Task> beforeMutation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AdaPanel.Core.Dtos.Sites.SitePermissionRepairResultDto> RepairPermissionsAsync(string siteName, Func<AdaPanel.Core.Dtos.Sites.SiteDto, CancellationToken, Task> beforeMutation, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AdaPanel.Core.Dtos.Sites.SiteDto> CreateAndProvisionAsync(AdaPanel.Core.Dtos.Sites.CreateSiteRequest request, Guid? actorUserId, string? ipAddress, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AdaPanel.Core.Dtos.Sites.SiteDto> SetDocumentRootAsync(string siteName, string physicalPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AdaPanel.Core.Dtos.Sites.SiteDto> EnableFrameworkRewriteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AdaPanel.Core.Dtos.Sites.SitePermissionRepairResultDto> RepairPermissionsAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class PatlayanSiteServisi : IIisSiteService
    {
        public Task<AdaPanel.Core.Dtos.Sites.SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<AdaPanel.Core.Dtos.Sites.SiteDto>> GetSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AdaPanel.Core.Dtos.Sites.SiteDto> CreateSiteAsync(AdaPanel.Core.Dtos.Sites.CreateSiteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AdaPanel.Core.Dtos.Sites.SiteDto> UpdatePhysicalPathAsync(string siteName, string physicalPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AdaPanel.Core.Dtos.Sites.SiteDto> StartSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AdaPanel.Core.Dtos.Sites.SiteDto> StopSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AdaPanel.Core.Dtos.Sites.SiteDto> AddBindingAsync(string siteName, AdaPanel.Core.Dtos.Sites.SiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AdaPanel.Core.Dtos.Sites.SiteDto> RemoveBindingAsync(string siteName, AdaPanel.Core.Dtos.Sites.RemoveSiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<AdaPanel.Core.Dtos.Sites.SiteDto>> ImportExistingSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
