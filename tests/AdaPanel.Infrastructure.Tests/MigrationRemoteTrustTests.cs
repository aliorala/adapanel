using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Infrastructure.Migration;
using AdaPanel.Infrastructure.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Security;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// GÖÇÜN UZAK TARAFI: kimlik doğrulanıyor mu, doğrulanamayınca ne oluyor, istisna nasıl açılıyor.
/// </summary>
/// <remarks>
/// <para>
/// <b>Kapatılan açık.</b> Göç yolu üç ayrı noktada uzak sunucunun kimliğini hiç doğrulamıyordu ve
/// üçü de kullanıcının kimlik bilgisini taşıyordu: Plesk/cPanel keşfi (API anahtarı), FTPS dosya
/// çekme (FTP parolası), SFTP dosya çekme (SSH parolası). Araya giren biri sahte kimlikle bağlantıyı
/// karşılayıp bunları okuyabilirdi.
/// </para>
/// <para>
/// <b>Bu dosya KAYNAK TARAMAZ.</b> Her ölçüm ya gerçek bir TLS el sıkışması üzerinden
/// (<see cref="SahteTlsSunucu"/>), ya üretim yolunun kurduğu gerçek istemci nesnesinin durumu
/// üzerinden, ya da gerçek bir <see cref="HostKeyEventArgs"/> olayı ateşlenerek yapılır. Bir metnin
/// kaynakta yazıyor olması hiçbir şeyi kanıtlamaz; ölçülen şey DAVRANIŞTIR.
/// </para>
/// <para>
/// <b>FAZLA KAPATMA NÖBETÇİLERİ ayrıca var.</b> Doğrulamayı kayıtsız şartsız açmak göç özelliğini
/// gerçek dünyada kullanılamaz hâle getirirdi (kaynak sunucular neredeyse hep kendinden imzalı
/// sertifika taşır). Bu yüzden burada yalnız "reddediyor mu" değil, "yöneticinin bilerek kabul
/// ettiği istisna GERÇEKTEN çalışıyor mu" ve "geçerli sertifika sorunsuz geçiyor mu" da ölçülür.
/// </para>
/// </remarks>
public sealed class MigrationRemoteTrustTests
{
    private const string GecerliJson = """[{"name":"ornek.com","hosting_type":"virtual"}]""";

    // =====================================================================================
    // 1) KEŞİF YOLU — gerçek TLS el sıkışması üzerinden
    // =====================================================================================

    /// <summary>
    /// ASIL AÇIK: doğrulanamayan bir kaynak sunucuya API anahtarı ARTIK GÖNDERİLMİYOR.
    /// </summary>
    /// <remarks>
    /// Bu test kırmızıya düşerse, panel kendinden imzalı bir sunucuya sessizce bağlanıyor demektir —
    /// yani <c>X-API-Key</c> araya giren birine okutulabilir.
    /// </remarks>
    [Fact]
    public async Task Dogrulanamayan_Plesk_sertifikasi_ONAY_YOKKEN_REDDEDILIR()
    {
        using var sertifika = KendindenImzaliSertifika();
        using var sunucu = SahteTlsSunucu.Baslat(sertifika, GecerliJson);

        var hata = await Assert.ThrowsAsync<AppException>(() => GocServisi().DiscoverPleskAsync(
            new PleskDiscoverRequest { Host = "127.0.0.1", Port = sunucu.Port, ApiKey = "gizli-anahtar" }));

        Assert.Equal("migration.tls.untrusted", hata.MessageKey);

        // Yönetici NE'yi reddettiğimizi görmeli: parmak izi + sebep. Görmezse tek yapabileceği
        // şey doğrulamayı tümden kapatmak olurdu.
        Assert.StartsWith(TrustFingerprint.Prefix, hata.Details, StringComparison.Ordinal);
        Assert.Contains("sebep=", hata.Details, StringComparison.Ordinal);
        Assert.Matches("self_signed|untrusted_root|incomplete_chain", hata.Details);
    }

    /// <summary>
    /// FAZLA KAPATMA NÖBETÇİSİ: yöneticinin BİLEREK kabul ettiği parmak iziyle göç keşfi ÇALIŞIR.
    /// </summary>
    /// <remarks>
    /// Bu test olmadan "sadece doğrulamayı açtım" çözümü yeşil görünürdü — oysa kendinden imzalı
    /// sertifika taşıyan kaynak sunucular göçün NORMAL hâlidir; onları kapatmak kapatılan riskten
    /// daha çok zarar verirdi.
    /// </remarks>
    [Fact]
    public async Task Yoneticinin_kabul_ettigi_parmak_izi_ile_kesif_CALISIR()
    {
        using var sertifika = KendindenImzaliSertifika();
        using var sunucu = SahteTlsSunucu.Baslat(sertifika, GecerliJson);

        var sonuc = await GocServisi().DiscoverPleskAsync(new PleskDiscoverRequest
        {
            Host = "127.0.0.1",
            Port = sunucu.Port,
            ApiKey = "gizli-anahtar",
            AcceptCertificateFingerprint = TrustFingerprint.FromCertificate(sertifika)
        });

        Assert.Equal("ornek.com", Assert.Single(sonuc.Domains).Name);
    }

    /// <summary>
    /// SABİTLEME GERÇEKTEN SABİTLİYOR: başka bir sunucunun parmak izi bu sunucuyu AÇMAZ.
    /// </summary>
    /// <remarks>
    /// Kabul "bir kez onayladım, artık her şey geçsin" olsaydı, istisna küresel bir kapatma
    /// anahtarından farksız olurdu. Ölçülen şey tam olarak budur.
    /// </remarks>
    [Fact]
    public async Task BASKA_bir_sunucunun_parmak_izi_kesfi_ACMAZ()
    {
        using var sertifika = KendindenImzaliSertifika();
        using var baskaSertifika = KendindenImzaliSertifika();
        using var sunucu = SahteTlsSunucu.Baslat(sertifika, GecerliJson);

        var hata = await Assert.ThrowsAsync<AppException>(() => GocServisi().DiscoverPleskAsync(
            new PleskDiscoverRequest
            {
                Host = "127.0.0.1",
                Port = sunucu.Port,
                ApiKey = "gizli-anahtar",
                AcceptCertificateFingerprint = TrustFingerprint.FromCertificate(baskaSertifika)
            }));

        Assert.Equal("migration.tls.untrusted", hata.MessageKey);
    }

    /// <summary>cPanel/WHM keşfi AYNI kapıdan geçer — ikinci nüsha bırakılmadı.</summary>
    [Fact]
    public async Task cPanel_kesfi_de_dogrulanamayan_sertifikayi_REDDEDER()
    {
        using var sertifika = KendindenImzaliSertifika();
        using var sunucu = SahteTlsSunucu.Baslat(sertifika, """{"data":{"acct":[{"domain":"ornek.com","user":"ali"}]}}""");

        var hata = await Assert.ThrowsAsync<AppException>(() => GocServisi().DiscoverCpanelAsync(
            new CpanelDiscoverRequest { Host = "127.0.0.1", Port = sunucu.Port, UserName = "root", ApiToken = "whm-jetonu" }));

        Assert.Equal("migration.tls.untrusted", hata.MessageKey);
    }

    /// <summary>FAZLA KAPATMA NÖBETÇİSİ: cPanel keşfi de onaylı parmak izinde ÇALIŞIR.</summary>
    [Fact]
    public async Task cPanel_kesfi_kabul_edilen_parmak_izi_ile_CALISIR()
    {
        using var sertifika = KendindenImzaliSertifika();
        using var sunucu = SahteTlsSunucu.Baslat(sertifika, """{"data":{"acct":[{"domain":"ornek.com","user":"ali"}]}}""");

        var sonuc = await GocServisi().DiscoverCpanelAsync(new CpanelDiscoverRequest
        {
            Host = "127.0.0.1",
            Port = sunucu.Port,
            UserName = "root",
            ApiToken = "whm-jetonu",
            AcceptCertificateFingerprint = TrustFingerprint.FromCertificate(sertifika)
        });

        Assert.Equal("ali", Assert.Single(sonuc.Domains).AccountUser);
    }

    // =====================================================================================
    // 2) KARAR VERİCİNİN KENDİSİ
    // =====================================================================================

    /// <summary>
    /// FAZLA KAPATMA NÖBETÇİSİ: GEÇERLİ sertifikada sabitleme HİÇ ARANMAZ.
    /// </summary>
    /// <remarks>
    /// Aranaydı, kaynak sunucu sertifikasını meşru biçimde yenilediğinde eski parmak izini taşıyan
    /// bir göç kırılırdı. Sabitlemenin görevi geçerli bir zinciri daraltmak değil, GEÇERSİZ bir
    /// zincire tek seferlik istisna açmaktır.
    /// </remarks>
    [Fact]
    public void Gecerli_sertifikada_yanlis_sabitleme_bile_ENGEL_DEGILDIR()
    {
        using var sertifika = KendindenImzaliSertifika();
        var kapi = new RemoteCertificateTrustGate("SHA256:00:11:22:33");

        Assert.True(kapi.Evaluate(sertifika, chain: null, SslPolicyErrors.None));
        Assert.Null(kapi.Failure);
    }

    /// <summary>Zincir ayrıntısı okunamasa bile bir SEBEP söylenir: konu = veren ise kendinden imzalı.</summary>
    [Fact]
    public void Kendinden_imzali_sertifikanin_sebebi_ADLANDIRILIR()
    {
        using var sertifika = KendindenImzaliSertifika();
        var kapi = new RemoteCertificateTrustGate(pinnedFingerprint: null);

        Assert.False(kapi.Evaluate(sertifika, chain: null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.Contains("self_signed", kapi.Failure!.Reasons);
    }

    /// <summary>Ad uyuşmazlığı, zincir hatasından AYRI bir sebeptir (yönetici farklı karar verir).</summary>
    [Fact]
    public void Ad_uyusmazligi_ayri_sebep_olarak_RAPORLANIR()
    {
        using var sertifika = KendindenImzaliSertifika();
        var kapi = new RemoteCertificateTrustGate(pinnedFingerprint: null);

        Assert.False(kapi.Evaluate(sertifika, chain: null, SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.Contains("name_mismatch", kapi.Failure!.Reasons);
    }

    /// <summary>Sertifika hiç sunulmadıysa da karar REDDETMEKTİR (boş parmak izi hiçbir sabitlemeyle eşleşmez).</summary>
    [Fact]
    public void Sertifika_SUNULMAZSA_reddedilir()
    {
        var kapi = new RemoteCertificateTrustGate("SHA256:00:11");

        Assert.False(kapi.Evaluate(certificate: null, chain: null, SslPolicyErrors.RemoteCertificateNotAvailable));
        Assert.Contains("no_certificate", kapi.Failure!.Reasons);
    }

    /// <summary>
    /// Parmak izi yazım farkları TOLERE edilir — yoksa yönetici doğrulamayı büsbütün kapatmaya itilir.
    /// </summary>
    /// <remarks>
    /// Yönetici parmak izini tarayıcıdan (<c>1a:2b:…</c>), <c>openssl</c>'den (<c>1A2B…</c>) ya da
    /// önekli hâliyle kopyalar. Üçü de aynı şeyi söylerken birini "eşleşmedi" saymak güvenliği
    /// artırmaz, yalnız kullanıcıyı kapatmaya iter.
    /// </remarks>
    [Fact]
    public void Ayni_parmak_izinin_FARKLI_YAZIMLARI_esitlenir()
    {
        using var sertifika = KendindenImzaliSertifika();
        var kanonik = TrustFingerprint.FromCertificate(sertifika);
        var onaltilik = kanonik[TrustFingerprint.Prefix.Length..];

        foreach (var yazim in new[]
                 {
                     kanonik,
                     kanonik.ToLowerInvariant(),
                     onaltilik,
                     onaltilik.Replace(":", string.Empty, StringComparison.Ordinal).ToLowerInvariant(),
                     " " + kanonik + " "
                 })
        {
            var kapi = new RemoteCertificateTrustGate(yazim);
            Assert.True(kapi.Evaluate(sertifika, chain: null, SslPolicyErrors.RemoteCertificateChainErrors), yazim);
            Assert.Equal(kanonik, kapi.AcceptedByPin);
        }
    }

    /// <summary>BENZER ama farklı bir parmak izi kabul EDİLMEZ (normalleştirme fazla toleranslı olmasın).</summary>
    [Fact]
    public void Tek_karakteri_farkli_parmak_izi_KABUL_EDILMEZ()
    {
        using var sertifika = KendindenImzaliSertifika();
        var kanonik = TrustFingerprint.FromCertificate(sertifika);
        var bozuk = kanonik[..^1] + (kanonik[^1] == '0' ? '1' : '0');

        var kapi = new RemoteCertificateTrustGate(bozuk);

        Assert.False(kapi.Evaluate(sertifika, chain: null, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    /// <summary>
    /// Parmak izi SIR MASKELEYİCİSİNDEN sağ çıkar — yoksa "kabul et" düğmesi neyi kabul edeceğini bilemezdi.
    /// </summary>
    /// <remarks>
    /// <see cref="AppException.Details"/> kurucuda <c>SecretRedactor</c>'dan geçer. Parmak izi sır
    /// değildir (herkese açık özet) ama uzun bir onaltılık dizedir; maskeye takılsaydı arayüzdeki
    /// onay akışı SESSİZCE çalışmaz hâle gelirdi.
    /// </remarks>
    [Fact]
    public void Parmak_izi_Details_kanalinda_MASKELENMEZ()
    {
        using var sertifika = KendindenImzaliSertifika();
        var kapi = new RemoteCertificateTrustGate(pinnedFingerprint: null);
        kapi.Evaluate(sertifika, chain: null, SslPolicyErrors.RemoteCertificateChainErrors);

        var hata = new AppException(kapi.Failure!.MessageKey, 502, 502, kapi.Failure.ToDetails());

        Assert.StartsWith(TrustFingerprint.FromCertificate(sertifika), hata.Details, StringComparison.Ordinal);
    }

    // =====================================================================================
    // 3) FTPS — üretim yolunun kurduğu GERÇEK istemci nesnesi
    // =====================================================================================

    /// <summary>
    /// FTPS istemcisi artık "her sertifikayı kabul et" DEĞİL ve kararı gerçekten SORUYOR.
    /// </summary>
    /// <remarks>
    /// İki ölçüm birlikte anlamlıdır: <c>ValidateAnyCertificate</c> açık kalsaydı FluentFTP olayı
    /// hiç sormazdı (kapı bağlı olsa bile ölü olurdu); olay bağlı olmasaydı da karar verecek kimse
    /// olmazdı. İkisi de gerçek nesnenin durumundan okunuyor.
    /// </remarks>
    [Fact]
    public void FTPS_istemcisi_her_sertifikayi_kabul_ETMEZ()
    {
        var kapi = new RemoteCertificateTrustGate(pinnedFingerprint: null);
        using var istemci = FileMigrationService.FtpIstemcisiOlustur(DosyaIstegi("ftps"), secure: true, kapi);

        Assert.False(istemci.Config.ValidateAnyCertificate);
        Assert.True(istemci.ValidateCertificateHandlerExists);
    }

    // =====================================================================================
    // 4) SFTP — gerçek host key olayı ateşlenerek
    // =====================================================================================

    /// <summary>
    /// SFTP host key'i ONAY YOKKEN reddedilir.
    /// </summary>
    /// <remarks>
    /// <b>ÖLÇÜLDÜ:</b> önceki hâlde <c>HostKeyReceived</c>'e hiç abone olunmuyordu ve SSH.NET'te
    /// abone yoksa <c>HostKeyEventArgs.CanTrust</c> varsayılanı <c>true</c>'dur — yani panel her
    /// host key'i kabul ediyordu. Bu test o varsayılanı da birlikte ölçer: olay <c>CanTrust = true</c>
    /// ile gelir, üretim yolunun bağladığı karar verici onu <c>false</c>'a çevirmelidir.
    /// </remarks>
    [Fact]
    public void SFTP_host_key_ONAY_YOKKEN_reddedilir()
    {
        var kapi = new RemoteHostKeyTrustGate(pinnedFingerprint: null);
        using var istemci = FileMigrationService.SftpIstemcisiOlustur(DosyaIstegi("sftp"), kapi);
        var olay = HostKeyOlayi();

        Assert.True(olay.CanTrust); // SSH.NET varsayılanı: doğrulamasız kabul.
        BagliHostKeyKararini(istemci)(istemci, olay);

        Assert.False(olay.CanTrust);
        Assert.Equal("migration.hostkey.untrusted", kapi.Failure!.MessageKey);
        Assert.Contains("unknown_host_key", kapi.Failure.Reasons);
        Assert.StartsWith(TrustFingerprint.Prefix, kapi.Failure.Fingerprint, StringComparison.Ordinal);
    }

    /// <summary>
    /// FAZLA KAPATMA NÖBETÇİSİ: yöneticinin teyit ettiği host key ile SFTP göçü ÇALIŞIR.
    /// </summary>
    /// <remarks>
    /// Parmak izi <c>ssh-keygen -l</c> yazımıyla birebir aynıdır (<c>SHA256:base64</c>), yani
    /// yönetici teyidi gerçekten yapılabilir bir iştir.
    /// </remarks>
    [Fact]
    public void SFTP_teyit_edilen_host_key_ile_BAGLANIR()
    {
        var olay = HostKeyOlayi();
        var kapi = new RemoteHostKeyTrustGate(TrustFingerprint.FromSshHostKey(olay.FingerPrintSHA256));
        using var istemci = FileMigrationService.SftpIstemcisiOlustur(DosyaIstegi("sftp"), kapi);

        olay.CanTrust = false;
        BagliHostKeyKararini(istemci)(istemci, olay);

        Assert.True(olay.CanTrust);
        Assert.Null(kapi.Failure);
        Assert.Equal(TrustFingerprint.FromSshHostKey(olay.FingerPrintSHA256), kapi.AcceptedByPin);
    }

    /// <summary>BAŞKA bir sunucunun host key'i bu sunucuyu AÇMAZ.</summary>
    [Fact]
    public void SFTP_BASKA_bir_host_key_parmak_izi_KABUL_EDILMEZ()
    {
        var olay = HostKeyOlayi();
        var baskaOlay = HostKeyOlayi();
        var kapi = new RemoteHostKeyTrustGate(TrustFingerprint.FromSshHostKey(baskaOlay.FingerPrintSHA256));
        using var istemci = FileMigrationService.SftpIstemcisiOlustur(DosyaIstegi("sftp"), kapi);

        BagliHostKeyKararini(istemci)(istemci, olay);

        Assert.False(olay.CanTrust);
    }

    // =====================================================================================
    // Yardımcılar
    // =====================================================================================

    private static RemoteFileMigrationRequest DosyaIstegi(string protokol) => new()
    {
        SiteName = "ornek.com",
        Protocol = protokol,
        Host = "kaynak.example.com",
        Port = protokol == "sftp" ? 22 : 21,
        UserName = "kullanici",
        Password = "parola",
        RemotePath = "/httpdocs"
    };

    /// <summary>
    /// İstemciye BAĞLANMIŞ host key kararını çıkarır ve testin ateşleyebileceği hâle getirir.
    /// </summary>
    /// <remarks>
    /// Olay <c>BaseClient</c> tarafından yayımlandığı için dışarıdan tetiklenemez; abonelik alanı
    /// okunur. Ölçülen şey yine DAVRANIŞTIR — alanın dolu olması "kapı bağlı", çağrının sonucu ise
    /// "kapı ne karar veriyor" sorusunu yanıtlar. Abonelik kaldırılırsa bu satır <c>null</c> döner
    /// ve testler kırmızıya düşer.
    /// </remarks>
    private static EventHandler<HostKeyEventArgs> BagliHostKeyKararini(SftpClient client)
    {
        var alan = typeof(BaseClient).GetField("HostKeyReceived", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(alan);

        var karar = alan!.GetValue(client) as EventHandler<HostKeyEventArgs>;
        Assert.NotNull(karar);
        return karar!;
    }

    /// <summary>Gerçek bir RSA host key'inden gerçek bir SSH.NET olayı üretir.</summary>
    private static HostKeyEventArgs HostKeyOlayi()
    {
        using var rsa = RSA.Create(2048);
        var p = rsa.ExportParameters(includePrivateParameters: true);

        static BigInteger Buyuk(byte[] bigEndian)
            => new([.. bigEndian.Reverse(), 0]);

        var anahtar = new RsaKey(
            Buyuk(p.Modulus!),
            Buyuk(p.Exponent!),
            Buyuk(p.D!),
            Buyuk(p.P!),
            Buyuk(p.Q!),
            Buyuk(p.InverseQ!));

        return new HostKeyEventArgs(new KeyHostAlgorithm("ssh-rsa", anahtar));
    }

    /// <summary>
    /// Göç servisi — gerçek yol, sahte DNS. Birim testi gerçek ağa çıkmaz; loopback hedefine izin
    /// verilir çünkü sahte sunucu orada dinliyor.
    /// </summary>
    private static SiteMigrationService GocServisi()
        => new(
            null!,
            null!,
            null!,
            null!,
            NullLogger<SiteMigrationService>.Instance,
            userIniScanEntryBudget: null,
            runtimeOptions: Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions
            {
                DataRoot = Path.Combine(Path.GetTempPath(), "adapanel-guven-testi", Guid.NewGuid().ToString("N")),
                AllowInternalOutboundTargets = true
            }),
            outboundResolver: (host, _) => Task.FromResult(new[] { IPAddress.Parse(host) }));

    /// <summary>
    /// Testin KENDİ ürettiği kendinden imzalı sertifika. Makinedeki sertifika deposuna HİÇBİR ŞEY
    /// yazılmaz; PFX'e çevirme adımı Windows'ta <c>SslStream</c>'in geçici anahtarı kabul etmesi içindir.
    /// </summary>
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
    /// Loopback'te TLS konuşan en küçük HTTP sunucusu: bir istek okur, sabit bir JSON döner.
    /// </summary>
    /// <remarks>
    /// <c>HttpListener</c> yerine <see cref="TcpListener"/>: <c>HttpListener</c> Windows'ta URL
    /// ayırması (urlacl) isteyebilir ve testin yükseltilmiş yetkiyle koşup koşmadığına göre davranışı
    /// DEĞİŞİR — kararsız bir test, ilerideki gerçek arızanın "yine o test" diye elenmesine yol açar.
    /// </remarks>
    private sealed class SahteTlsSunucu : IDisposable
    {
        private readonly TcpListener dinleyici;
        private readonly X509Certificate2 sertifika;
        private readonly string govde;
        private readonly CancellationTokenSource iptal = new();

        private SahteTlsSunucu(TcpListener dinleyici, X509Certificate2 sertifika, string govde)
        {
            this.dinleyici = dinleyici;
            this.sertifika = sertifika;
            this.govde = govde;
            Port = ((IPEndPoint)dinleyici.LocalEndpoint).Port;
            _ = Task.Run(DinleAsync);
        }

        public int Port { get; }

        public static SahteTlsSunucu Baslat(X509Certificate2 sertifika, string govde)
        {
            var dinleyici = new TcpListener(IPAddress.Loopback, 0);
            dinleyici.Start();
            return new SahteTlsSunucu(dinleyici, sertifika, govde);
        }

        private async Task DinleAsync()
        {
            try
            {
                while (!iptal.IsCancellationRequested)
                {
                    using var istemci = await dinleyici.AcceptTcpClientAsync(iptal.Token);
                    try
                    {
                        await using var akis = new SslStream(istemci.GetStream(), leaveInnerStreamOpen: false);
                        await akis.AuthenticateAsServerAsync(sertifika, false, checkCertificateRevocation: false);

                        var tampon = new byte[4096];
                        if (await akis.ReadAsync(tampon, iptal.Token) <= 0)
                        {
                            continue;
                        }

                        var govdeBaytlari = Encoding.UTF8.GetBytes(govde);
                        var baslik = Encoding.ASCII.GetBytes(
                            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n"
                            + $"Content-Length: {govdeBaytlari.Length}\r\nConnection: close\r\n\r\n");

                        await akis.WriteAsync(baslik, iptal.Token);
                        await akis.WriteAsync(govdeBaytlari, iptal.Token);
                        await akis.FlushAsync(iptal.Token);
                    }
                    catch (Exception) when (!iptal.IsCancellationRequested)
                    {
                        // İstemci el sıkışmayı REDDETTİĞİNDE burası patlar — testin beklediği durum budur.
                    }
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

        public void Dispose()
        {
            iptal.Cancel();
            dinleyici.Stop();
            iptal.Dispose();
        }
    }
}
