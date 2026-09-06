using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Migration;
using FluentFTP;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// FTPS DÜŞÜRME SALDIRISI: "ftps" seçildiğinde bağlantı GERÇEKTEN şifreleniyor mu.
/// </summary>
/// <remarks>
/// <para>
/// <b>ÖLÇÜLEN AÇIK.</b> <c>FtpConfig.EncryptionMode</c> <c>Auto</c> idi. Bu dosyadaki sahte FTP
/// sunucusuyla ölçüldü: sunucu <c>AUTH TLS</c>e "500" dediğinde FluentFTP bağlantıyı KESMİYOR,
/// düz FTP'ye düşüp <c>USER</c>/<c>PASS</c>i ŞİFRESİZ gönderiyordu. Yani araya giren aktif bir
/// saldırgan tek bir yanıtı bastırarak FTP parolasını açık metin okuyabilirdi ve bir önceki turda
/// eklenen sertifika doğrulaması (<see cref="RemoteCertificateTrustGate"/>) hiç devreye girmiyordu —
/// TLS el sıkışması hiç başlamadığı için sorulacak sertifika da yoktu.
/// </para>
/// <para>
/// <b>BU DOSYA KAYNAK TARAMAZ.</b> Ölçüm noktası, gerçek bir TCP soketinin sunucu ucudur: parola
/// TLS'siz akıştan mı geldi, TLS'li akıştan mı, yoksa hiç gelmedi mi. Bir sabitin kaynakta
/// <c>Explicit</c> yazması, telde ne olduğunu söylemez.
/// </para>
/// <para>
/// <b>FAZLA KAPATMA NÖBETÇİLERİ.</b> "Her zaman TLS zorla" demek kullanıcının BİLİNÇLİ
/// <c>ftp</c> seçimini de öldürürdü — kendi eski sunucusundan çeken kullanıcı için o sunucu TLS
/// sunmuyor olabilir ve bu onun kararıdır. Bu yüzden burada iki şey ayrıca ölçülür: (1) kullanıcı
/// <c>ftp</c> seçtiğinde göç HÂLÂ çalışır, (2) geçerli bir FTPS sunucusuna bağlantı
/// <c>Explicit</c> zorlamasıyla da kurulur ve parola TLS'in İÇİNDEN gider.
/// </para>
/// </remarks>
public sealed class MigrationFtpsDowngradeTests
{
    // =====================================================================================
    // 1) ASIL AÇIK — sunucu TLS sunmuyorsa parola HİÇ gönderilmez
    // =====================================================================================

    /// <summary>
    /// <c>ftps</c> seçiliyken <c>AUTH TLS</c> reddedilirse PAROLA DÜZ METİNDEN GÖNDERİLMEZ.
    /// </summary>
    /// <remarks>
    /// MUTASYON: <c>EncryptionMode</c> <c>Explicit</c> yerine tekrar <c>Auto</c> yapılırsa bu test
    /// düşer — çünkü FluentFTP düz FTP'ye düşüp <c>USER</c>/<c>PASS</c>i şifresiz yollar ve sunucu
    /// tarafındaki düz-metin kaydında parola görünür.
    /// </remarks>
    [Fact]
    public async Task FTPS_secildiginde_sunucu_TLS_sunmuyorsa_PAROLA_DUZ_METINDEN_GITMEZ()
    {
        // Sertifikasız sunucu = "AUTH TLS" komutuna 500 döner: TLS sunmayan (ya da yanıtı bastırılmış) sunucu.
        using var sunucu = SahteFtpSunucu.Baslat(tlsSertifikasi: null);

        var hata = await GocuCalistirAsync(sunucu.Port, "ftps");

        // 1) Göç sessizce "başarılı" olmadı.
        Assert.NotNull(hata);

        // 2) ASIL ÖLÇÜM: şifresiz akışta kimlik bilgisi HİÇ görünmedi.
        Assert.DoesNotContain(
            sunucu.DuzMetinKomutlari,
            komut => komut.StartsWith("PASS", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            sunucu.DuzMetinKomutlari,
            komut => komut.StartsWith("USER", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(Parola, string.Join("\n", sunucu.DuzMetinKomutlari), StringComparison.Ordinal);

        // 3) Denemenin kendisi yapılmış olmalı: sunucu AUTH TLS'i GÖRDÜ ve reddetti.
        //    (Bu satır olmadan test, "hiç bağlanamadı" gibi ilgisiz bir sebeple de yeşil kalırdı.)
        Assert.Contains(
            sunucu.DuzMetinKomutlari,
            komut => komut.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase));
    }

    // =====================================================================================
    // 2) FAZLA KAPATMA NÖBETÇİSİ — geçerli FTPS sunucusu ÇALIŞMAYA DEVAM EDER
    // =====================================================================================

    /// <summary>
    /// FAZLA KAPATMA NÖBETÇİSİ: TLS sunan bir sunucuya FTPS göçü ÇALIŞIR ve parola TLS'in
    /// İÇİNDEN gider.
    /// </summary>
    /// <remarks>
    /// <c>Explicit</c> zorlaması FTPS'i kırsaydı bu test düşerdi. Sunucu kendinden imzalı
    /// sertifika taşır (göç kaynaklarının neredeyse tamamı öyledir); yönetici parmak izini
    /// sabitlediği için güven kapısı geçer — yani iki koruma birlikte çalışıyor.
    /// </remarks>
    [Fact]
    public async Task FTPS_gecerli_TLS_sunucusuna_baglanir_ve_parola_SIFRELI_KANALDAN_gider()
    {
        using var sertifika = KendindenImzaliSertifika();
        using var sunucu = SahteFtpSunucu.Baslat(sertifika);

        await GocuCalistirAsync(sunucu.Port, "ftps", TrustFingerprint.FromCertificate(sertifika));

        // Parola TLS'ten SONRA geldi: sunucu onu ancak şifreli akıştan okudu.
        Assert.Contains(
            sunucu.SifreliKomutlari,
            komut => komut.StartsWith("PASS", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(Parola, string.Join("\n", sunucu.DuzMetinKomutlari), StringComparison.Ordinal);
    }

    // =====================================================================================
    // 3) FAZLA KAPATMA NÖBETÇİSİ — kullanıcının BİLİNÇLİ düz FTP tercihi engellenmez
    // =====================================================================================

    /// <summary>
    /// FAZLA KAPATMA NÖBETÇİSİ: kullanıcı arayüzde <c>FTP</c>yi seçtiyse göç ÇALIŞIR.
    /// </summary>
    /// <remarks>
    /// Bu, panelin sessiz düşürmesinden farklı bir şeydir: kullanıcı TLS istemediğini AÇIKÇA
    /// söylemiştir (kendi eski sunucusu TLS sunmuyor olabilir). Kapatılması gereken şey istenen
    /// korumanın sağlanmaması, kullanıcının kararı değildir. Şifrelemeyi burada da zorlayan bir
    /// "düzeltme" bu testi kırar.
    /// </remarks>
    [Fact]
    public async Task Kullanici_DUZ_FTP_sectiginde_baglanti_KURULUR()
    {
        using var sunucu = SahteFtpSunucu.Baslat(tlsSertifikasi: null);

        await GocuCalistirAsync(sunucu.Port, "ftp");

        // Oturum açıldı: TLS zorlaması bu yolu KAPATMADI.
        Assert.Contains(
            sunucu.DuzMetinKomutlari,
            komut => komut.StartsWith("PASS", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// FAZLA KAPATMA NÖBETÇİSİ: 990 portundaki ÖRTÜK (implicit) FTPS sunucusuna göç ÇALIŞIR.
    /// </summary>
    /// <remarks>
    /// <c>Explicit</c> her koşulda dayatılsaydı bu yol kırılırdı: örtük sunucu <c>AUTH TLS</c>
    /// beklemez, bağlantı açılır açılmaz TLS el sıkışması bekler. Örtük kipte düşürme mümkün
    /// değildir (el sıkışma ilk iştir), o yüzden burada güvenlikten ödün verilmiyor — testin
    /// ölçtüğü de bu: parola yine yalnız ŞİFRELİ akıştan geçiyor.
    /// </remarks>
    [Fact]
    public async Task Ortuk_FTPS_990_portunda_CALISIR_ve_parola_SIFRELI_KANALDAN_gider()
    {
        using var sertifika = KendindenImzaliSertifika();
        using var sunucu = SahteFtpSunucu.Baslat(sertifika, ortukTls: true);

        // İstemci ÜRETİM YOLUNDAN, 990 portuyla kurulur — kip kararını veren şey odur. Bağlantı
        // sonra sahte sunucunun portuna çevrilir: testin 990'ı gerçekten dinlemesi (ayrıcalık +
        // makinedeki başka bir servisle çakışma) ölçülen şeye hiçbir şey katmazdı.
        var kapi = new RemoteCertificateTrustGate(TrustFingerprint.FromCertificate(sertifika));
        await using var istemci = FileMigrationService.FtpIstemcisiOlustur(Istek("ftps", 990), secure: true, kapi);
        Assert.Equal(FtpEncryptionMode.Implicit, istemci.Config.EncryptionMode);

        istemci.Port = sunucu.Port;
        try
        {
            await istemci.Connect();
        }
        catch (Exception)
        {
            // Sahte sunucu tam bir FTP sunucusu değil; ölçülen şey bağlantının sonucu değil,
            // parolanın HANGİ akıştan geçtiğidir.
        }

        Assert.Contains(
            sunucu.SifreliKomutlari,
            komut => komut.StartsWith("PASS", StringComparison.OrdinalIgnoreCase));

        // Örtük kipte TLS'ten ÖNCE tek bir bayt bile komut olarak gitmez.
        Assert.Empty(sunucu.DuzMetinKomutlari);
    }

    // =====================================================================================
    // 4) İstemci nesnesinin GERÇEK durumu
    // =====================================================================================

    /// <summary>
    /// Üretim yolunun kurduğu istemci: <c>ftps</c> için <c>Explicit</c>, veri kanalı da şifreli.
    /// </summary>
    /// <remarks>
    /// Veri kanalı ayrıca ölçülür: komut kanalı TLS olup veri kanalı düz kalsaydı dosya İÇERİĞİ
    /// açık metin giderdi. FluentFTP varsayılanı bugün <c>true</c>; karar açıkça yazıldığı için
    /// kütüphane varsayılanı değişse bile bu satır yerinde kalır.
    /// </remarks>
    [Fact]
    public void FTPS_istemcisi_EXPLICIT_kipte_ve_veri_kanali_da_sifreli_kurulur()
    {
        var kapi = new RemoteCertificateTrustGate(pinnedFingerprint: null);
        using var istemci = FileMigrationService.FtpIstemcisiOlustur(Istek("ftps", 21), secure: true, kapi);

        Assert.Equal(FtpEncryptionMode.Explicit, istemci.Config.EncryptionMode);
        Assert.True(istemci.Config.DataConnectionEncryption);
    }

    /// <summary>
    /// Kullanıcı <c>ftp</c> seçtiyse istemci ŞİFRESİZ kurulur — tercih olduğu gibi uygulanır.
    /// </summary>
    [Fact]
    public void Duz_FTP_secildiginde_istemci_SIFRELEMESIZ_kurulur()
    {
        var kapi = new RemoteCertificateTrustGate(pinnedFingerprint: null);
        using var istemci = FileMigrationService.FtpIstemcisiOlustur(Istek("ftp", 21), secure: false, kapi);

        Assert.Equal(FtpEncryptionMode.None, istemci.Config.EncryptionMode);
    }

    // =====================================================================================
    // Yardımcılar
    // =====================================================================================

    private const string Parola = "CokGizliFtpParolasi123!";

    /// <summary>
    /// Göçü ÜRETİM YOLUNDAN çalıştırır (<see cref="FileMigrationService.PullAsync"/>) ve varsa
    /// istisnayı döndürür.
    /// </summary>
    /// <remarks>
    /// Sahte sunucu dosya listeleme yapmadığı için göç her hâlükârda bir <see cref="AppException"/>
    /// ile biter; ölçülen şey sonucun kendisi değil, sunucunun TELDE NE GÖRDÜĞÜdür.
    /// </remarks>
    private static async Task<AppException?> GocuCalistirAsync(int port, string protokol, string parmakIzi = "")
    {
        var hedefKok = Path.Combine(Path.GetTempPath(), "ada-ftps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(hedefKok);

        try
        {
            var servis = new FileMigrationService(new SahteSiteServisi(hedefKok), runtimeOptions: IcAgaIzinli);
            var istek = Istek(protokol, port);
            istek.AcceptHostFingerprint = parmakIzi;

            try
            {
                await servis.PullAsync(istek);
                return null;
            }
            catch (AppException hata)
            {
                return hata;
            }
        }
        finally
        {
            // NOT: geçici hedef klasör BIRAKILIYOR; testin ölçtüğü şey sunucu tarafındaki tel
            // kaydıdır ve işletim sistemi TEMP'i kendisi devreder.
        }
    }

    private static RemoteFileMigrationRequest Istek(string protokol, int port) => new()
    {
        SiteName = "ornek.test",
        Protocol = protokol,
        Host = "127.0.0.1",
        Port = port,
        UserName = "gocKullanicisi",
        Password = Parola,
        RemotePath = "/"
    };

    /// <summary>
    /// Loopback'e bağlanmak için gerekli açık kapı — gerekçesi <c>MigrationObservabilityTests</c>'te.
    /// </summary>
    private static readonly Microsoft.Extensions.Options.IOptions<AdaPanel.Infrastructure.Options.PanelRuntimeOptions> IcAgaIzinli =
        Microsoft.Extensions.Options.Options.Create(new AdaPanel.Infrastructure.Options.PanelRuntimeOptions
        {
            AllowInternalOutboundTargets = true
        });

    private static X509Certificate2 KendindenImzaliSertifika()
    {
        using var rsa = RSA.Create(2048);
        var istek = new CertificateRequest("CN=127.0.0.1", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var adlar = new SubjectAlternativeNameBuilder();
        adlar.AddIpAddress(IPAddress.Loopback);
        istek.CertificateExtensions.Add(adlar.Build());

        using var uretilen = istek.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return new X509Certificate2(uretilen.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
    }

    /// <summary>
    /// Konuşan sahte FTP sunucusu: komutları TLS ÖNCESİ ve TLS SONRASI olarak AYRI kaydeder.
    /// </summary>
    /// <remarks>
    /// Ayrım testin bütün anlamıdır: "parola geldi" değil, "parola ŞİFRESİZ mi geldi" ölçülür.
    /// Sertifika verilmezse sunucu <c>AUTH</c>a "500" der — yani TLS sunmayan (ya da yanıtı
    /// bastırılmış) bir sunucuyu taklit eder.
    /// </remarks>
    private sealed class SahteFtpSunucu : IDisposable
    {
        private readonly TcpListener dinleyici;
        private readonly X509Certificate2? tlsSertifikasi;
        private readonly bool ortukTls;
        private readonly CancellationTokenSource iptal = new();
        private readonly List<string> duzMetin = [];
        private readonly List<string> sifreli = [];

        private SahteFtpSunucu(TcpListener dinleyici, X509Certificate2? tlsSertifikasi, bool ortukTls)
        {
            this.dinleyici = dinleyici;
            this.tlsSertifikasi = tlsSertifikasi;
            this.ortukTls = ortukTls;
            Port = ((IPEndPoint)dinleyici.LocalEndpoint).Port;
            _ = Task.Run(DinleAsync);
        }

        public int Port { get; }

        public IReadOnlyList<string> DuzMetinKomutlari
        {
            get { lock (duzMetin) { return [.. duzMetin]; } }
        }

        public IReadOnlyList<string> SifreliKomutlari
        {
            get { lock (sifreli) { return [.. sifreli]; } }
        }

        /// <summary>
        /// <paramref name="ortukTls"/>: bağlantı açılır açılmaz TLS el sıkışması yapan (990 tarzı)
        /// sunucu — <c>AUTH TLS</c> beklemez.
        /// </summary>
        public static SahteFtpSunucu Baslat(X509Certificate2? tlsSertifikasi, bool ortukTls = false)
        {
            var dinleyici = new TcpListener(IPAddress.Loopback, 0);
            dinleyici.Start();
            return new SahteFtpSunucu(dinleyici, tlsSertifikasi, ortukTls);
        }

        private async Task DinleAsync()
        {
            try
            {
                while (!iptal.IsCancellationRequested)
                {
                    var istemci = await dinleyici.AcceptTcpClientAsync(iptal.Token);

                    // FluentFTP `Auto` kipinde ikinci bir bağlantı (implicit deneme) açabildiği için
                    // sunucu tek bağlantıyla yetinmez; her oturum ayrı işlenir.
                    _ = Task.Run(() => OturumuIsleAsync(istemci));
                }
            }
            catch (OperationCanceledException)
            {
                // Kapanış.
            }
            catch (SocketException)
            {
                // Dinleyici kapandı.
            }
        }

        private async Task OturumuIsleAsync(TcpClient istemci)
        {
            using (istemci)
            {
                try
                {
                    Stream akis = istemci.GetStream();
                    var tlsAcik = false;

                    // ÖRTÜK FTPS: karşılama bile TLS'in içinden gider.
                    if (ortukTls && tlsSertifikasi is not null)
                    {
                        var ortukAkis = new SslStream(akis, leaveInnerStreamOpen: false);
                        await ortukAkis.AuthenticateAsServerAsync(tlsSertifikasi, false, checkCertificateRevocation: false);
                        akis = ortukAkis;
                        tlsAcik = true;
                    }

                    var okuyucu = new StreamReader(akis, Encoding.ASCII, false, 1024, leaveOpen: true);
                    var yazici = new StreamWriter(akis, Encoding.ASCII, 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };

                    await yazici.WriteLineAsync("220 SahteFTP hazir");

                    while (!iptal.IsCancellationRequested)
                    {
                        var satir = await okuyucu.ReadLineAsync(iptal.Token);
                        if (satir is null)
                        {
                            return;
                        }

                        Kaydet(satir, tlsAcik);
                        var komut = satir.Trim().ToUpperInvariant();

                        if (komut.StartsWith("AUTH", StringComparison.Ordinal))
                        {
                            if (tlsSertifikasi is null)
                            {
                                // TLS SUNMAYAN SUNUCU. `Auto` kipi burada düz FTP'ye düşüyordu.
                                await yazici.WriteLineAsync("500 AUTH not understood");
                                continue;
                            }

                            await yazici.WriteLineAsync("234 AUTH TLS OK");
                            var guvenli = new SslStream(akis, leaveInnerStreamOpen: false);
                            await guvenli.AuthenticateAsServerAsync(tlsSertifikasi, false, checkCertificateRevocation: false);
                            akis = guvenli;
                            tlsAcik = true;
                            okuyucu = new StreamReader(akis, Encoding.ASCII, false, 1024, leaveOpen: true);
                            yazici = new StreamWriter(akis, Encoding.ASCII, 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };
                            continue;
                        }

                        var yanit = komut switch
                        {
                            _ when komut.StartsWith("USER", StringComparison.Ordinal) => "331 Parola gerekli",
                            _ when komut.StartsWith("PASS", StringComparison.Ordinal) => "230 Giris yapildi",
                            _ when komut.StartsWith("PBSZ", StringComparison.Ordinal) => "200 PBSZ ok",
                            _ when komut.StartsWith("PROT", StringComparison.Ordinal) => "200 PROT ok",
                            _ when komut.StartsWith("SYST", StringComparison.Ordinal) => "215 UNIX Type: L8",
                            _ when komut.StartsWith("PWD", StringComparison.Ordinal) => "257 \"/\" gecerli dizin",
                            _ when komut.StartsWith("TYPE", StringComparison.Ordinal) => "200 TYPE ok",
                            _ when komut.StartsWith("OPTS", StringComparison.Ordinal) => "200 OPTS ok",
                            _ when komut.StartsWith("CLNT", StringComparison.Ordinal) => "200 CLNT ok",
                            _ when komut.StartsWith("FEAT", StringComparison.Ordinal) => "211 End",
                            _ when komut.StartsWith("QUIT", StringComparison.Ordinal) => "221 Hoscakal",
                            _ => "500 Bilinmeyen komut"
                        };

                        await yazici.WriteLineAsync(yanit);

                        if (komut.StartsWith("QUIT", StringComparison.Ordinal))
                        {
                            return;
                        }
                    }
                }
                catch (Exception)
                {
                    // İstemci el sıkışmayı reddedip bağlantıyı kestiğinde burası patlar — testin
                    // beklediği durumlardan biri budur.
                }
            }
        }

        private void Kaydet(string satir, bool tlsAcik)
        {
            var hedef = tlsAcik ? sifreli : duzMetin;
            lock (hedef)
            {
                hedef.Add(satir.Trim());
            }
        }

        public void Dispose()
        {
            iptal.Cancel();
            dinleyici.Stop();
            iptal.Dispose();
        }
    }

    private sealed class SahteSiteServisi(string fizikselYol) : IIisSiteService
    {
        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SiteDto { Name = siteName, PhysicalPath = fizikselYol });

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
