using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Migration;
using AdaPanel.Infrastructure.Support;
using FluentFTP;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// G19 — FTP/FTPS yedek ve göç hedefleri: DNS rebinding (adı bir kez çöz, IP'ye bağlan), PASV
/// yanıtındaki IP'yi yok say, FTPS (açık TLS) ve sertifika adı doğrulaması.
/// </summary>
public sealed class BackupFtpHardeningTests
{
    // ------------------------------------------------------------------ PASV + IP sabitleme

    /// <summary>
    /// Sunucu PASV'de ulaşılamaz/iç bir adres bildirir; istemci yine KONTROL bağlantısının IP'sine
    /// bağlanmalı. (Ölçüm: .NET FtpWebRequest bildirilen adresi yok sayıyor — bu test o davranışı
    /// kilitler; değişirse FTP bounce/SSRF açılır.)
    /// </summary>
    [Fact]
    public async Task PASV_yanitindaki_baska_IP_YOK_SAYILIR()
    {
        using var iptal = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var sunucu = SahteFtp.Baslat(pasvAdresi: "192,0,2,1");
        var komutlar = new FtpWebRequestCommands();

        var sonuc = await komutlar.ListDirectoryAsync(
            new Uri($"ftp://127.0.0.1:{sunucu.Port}/yedek"), new NetworkCredential("u", "p"), iptal.Token);

        Assert.True(sonuc.Success, sonuc.Description);
        Assert.Equal("a.zip", Assert.Single(sonuc.Names));
    }

    /// <summary>
    /// Ad hiç çözülmüyor (".invalid"): bağlantı yine de kurulabiliyorsa, bağlanılan şey ad DEĞİL
    /// doğrulanmış IP'dir. Eski davranış adı <c>FtpWebRequest</c>'e verip ikinci kez çözdürüyordu.
    /// </summary>
    [Fact]
    public async Task Pinner_varken_bagLanti_ADA_degil_dogrulanmis_IPye_kurulur()
    {
        using var iptal = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var sunucu = SahteFtp.Baslat();
        var pinner = new SabitPinner(IPAddress.Loopback);
        var komutlar = new FtpWebRequestCommands(pinner);
        Assert.True(komutlar.IsPinned);

        var sonuc = await komutlar.ListDirectoryAsync(
            new Uri($"ftp://rebinding.invalid:{sunucu.Port}/yedek"), new NetworkCredential("u", "p"), iptal.Token);

        Assert.True(sonuc.Success, sonuc.Description);
        Assert.Equal(1, pinner.Calls);
    }

    [Fact]
    public async Task Engelli_adres_komutta_ISTISNA_olur_ve_SUNUCUYA_hic_baglanilmaz()
    {
        using var iptal = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var sunucu = SahteFtp.Baslat();
        var komutlar = new FtpWebRequestCommands(new SabitPinner(null));

        var hata = await Assert.ThrowsAsync<AppException>(
            () => komutlar.DeleteFileAsync(new Uri($"ftp://nas.example.test:{sunucu.Port}/x.zip"), new NetworkCredential("u", "p"), iptal.Token));

        Assert.Equal("backup.remote_target_forbidden", hata.MessageKey);
        Assert.Equal(0, sunucu.Baglanti);
    }

    /// <summary>
    /// ÜRETİM kurulumu: <c>BackupService.FtpCommands</c> varsayılanı pinner'lıdır ve iç adrese çözülen
    /// ad komut anında REDDEDİLİR. (Pinner'sız varsayılan, eski davranışa sessiz dönüş olurdu.)
    /// </summary>
    [Fact]
    public async Task BackupService_varsayilan_FTP_komutlari_PINLIdir_ve_ic_adresi_reddeder()
    {
        using var alan = new BulutAlani();
        var servis = new BackupService(
            new SahteSiteServisi(alan.SiteKoku), null!, null!, new SahteTercihServisi(Path.Combine(alan.Kok, "backups")), null!,
            alan.DosyaDeposu, new SahteIstemciFabrikasi(new SahteBulut()),
            outboundResolver: (_, _) => Task.FromResult(new[] { IPAddress.Parse("10.0.0.5") }));

        var yonlendirici = Assert.IsType<RoutingBackupFtpCommands>(servis.FtpCommands);
        Assert.True(Assert.IsType<FtpWebRequestCommands>(yonlendirici.Plain).IsPinned);

        foreach (var adres in new[] { "ftp://nas.example.test/x.zip", "ftps://nas.example.test/x.zip" })
        {
            var hata = await Assert.ThrowsAsync<AppException>(
                () => servis.FtpCommands.DeleteFileAsync(new Uri(adres), new NetworkCredential("u", "p"), alan.Iptal));
            Assert.Equal("backup.remote_target_forbidden", hata.MessageKey);
        }
    }

    [Fact]
    public void PinToAddress_yolu_ve_portu_korur_adi_IPye_cevirir()
    {
        var pinli = BackupFtpScheme.PinToAddress(new Uri("ftps://nas.example.test/a/b%20c/d.zip"), IPAddress.Parse("93.184.216.34"));
        Assert.Equal("ftp", pinli.Scheme);
        Assert.Equal("93.184.216.34", pinli.Host);
        Assert.Equal(21, pinli.Port);
        Assert.Equal("/a/b%20c/d.zip", pinli.AbsolutePath);

        var v6 = BackupFtpScheme.PinToAddress(new Uri("ftp://nas.example.test:2121/x"), IPAddress.Parse("2606:4700:4700::1111"));
        Assert.Equal(2121, v6.Port);
        Assert.Equal("[2606:4700:4700::1111]", v6.Host);
    }

    [Fact]
    public void ftps_de_FTP_hedefidir_ve_varsayilan_port_21()
    {
        Assert.True(BackupFtpScheme.Is(new Uri("ftp://h/x")));
        Assert.True(BackupFtpScheme.Is(new Uri("ftps://h/x")));
        Assert.False(BackupFtpScheme.Is(new Uri("http://h/x")));
        Assert.False(BackupFtpScheme.Is(new Uri("sftp://h/x")));
        Assert.Equal(21, BackupFtpScheme.Port(new Uri("ftps://h/x")));
        Assert.Equal(2121, BackupFtpScheme.Port(new Uri("ftps://h:2121/x")));
        Assert.True(BackupFtpScheme.IsSecure(new Uri("ftps://h/x")));
        Assert.False(BackupFtpScheme.IsSecure(new Uri("ftp://h/x")));
    }

    [Fact]
    public async Task Kurtarma_komutu_ftps_adresini_ACIK_hatayla_reddeder()
    {
        var kok = Path.Combine(Path.GetTempPath(), "adapanel-bakim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(kok, "config"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(kok, "config", "automation.json"),
                "{\"backupTargetType\":\"ftp\",\"backupRemoteUrl\":\"ftps://nas.example.test/yedek\",\"backupRetentionDays\":7}");
            var hata = await Assert.ThrowsAsync<InvalidOperationException>(() => BackupFtpFolderMaintenance.RunAsync(kok, CancellationToken.None));
            Assert.Contains("FTPS", hata.Message);
        }
        finally
        {
            Directory.Delete(kok, true);
        }
    }

    // ------------------------------------------------------------------ FTPS yapılandırması

    [Fact]
    public async Task FTPS_istemcisi_acik_TLS_zorunlu_IPye_baglanir_ve_PASV_adresini_yok_sayar()
    {
        await using var istemci = BackupFtpsClient.Create(
            new Uri("ftps://nas.example.test/yedek"), IPAddress.Parse("93.184.216.34"), new NetworkCredential("u", "p"), TimeSpan.FromMinutes(5));

        Assert.Equal("93.184.216.34", istemci.Host);
        Assert.Equal(21, istemci.Port);
        Assert.Equal(FtpEncryptionMode.Explicit, istemci.Config.EncryptionMode);
        Assert.True(istemci.Config.DataConnectionEncryption);
        Assert.False(istemci.Config.ValidateAnyCertificate);
        Assert.Equal(FtpDataConnectionType.PASVEX, istemci.Config.DataConnectionType);
    }

    [Fact]
    public void FTPS_sertifika_karari_ozgun_adi_arar_zincir_hatasini_KURTARMAZ()
    {
        using var sertifika = SertifikaUret("ftp.example.test", "*.nas.example.test");

        // Ad uyuşmazlığı TEK hata ve sertifika özgün adı kapsıyor -> kabul (IP'ye bağlandık).
        Assert.True(BackupFtpsClient.AcceptCertificate(sertifika, SslPolicyErrors.RemoteCertificateNameMismatch, "ftp.example.test"));
        Assert.True(BackupFtpsClient.AcceptCertificate(sertifika, SslPolicyErrors.RemoteCertificateNameMismatch, "yedek.nas.example.test"));
        Assert.True(BackupFtpsClient.AcceptCertificate(sertifika, SslPolicyErrors.None, "herhangi"));

        // Başka ad: ret. Joker iki seviye kapsamaz. Zincir hatası ad eşleşse bile ret.
        Assert.False(BackupFtpsClient.AcceptCertificate(sertifika, SslPolicyErrors.RemoteCertificateNameMismatch, "baska.example.test"));
        Assert.False(BackupFtpsClient.AcceptCertificate(sertifika, SslPolicyErrors.RemoteCertificateNameMismatch, "a.b.nas.example.test"));
        Assert.False(BackupFtpsClient.AcceptCertificate(
            sertifika, SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors, "ftp.example.test"));
        Assert.False(BackupFtpsClient.AcceptCertificate(sertifika, SslPolicyErrors.RemoteCertificateChainErrors, "ftp.example.test"));
        Assert.False(BackupFtpsClient.AcceptCertificate(null, SslPolicyErrors.None, "ftp.example.test"));
    }

    // ------------------------------------------------------------------ Göç (FluentFTP / SFTP)

    [Fact]
    public async Task Goc_FTP_istemcisi_dogrulanmis_IPye_baglanir_PASV_yok_sayar_sertifika_adini_ozgun_adla_arar()
    {
        var istek = new RemoteFileMigrationRequest { Host = "kaynak.example.test", UserName = "u", Password = "p", Port = 21 };
        var kapi = new RemoteCertificateTrustGate(null, "kaynak.example.test");

        await using var istemci = FileMigrationService.FtpIstemcisiOlustur(istek, secure: true, kapi, connectHost: "93.184.216.34");

        Assert.Equal("93.184.216.34", istemci.Host);
        Assert.Equal(FtpDataConnectionType.PASVEX, istemci.Config.DataConnectionType);
        Assert.Equal(FtpEncryptionMode.Explicit, istemci.Config.EncryptionMode);
        Assert.False(istemci.Config.ValidateAnyCertificate);

        using var sertifika = SertifikaUret("kaynak.example.test");
        Assert.True(kapi.Evaluate(sertifika, null, SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.Null(kapi.Failure);

        var yabanci = new RemoteCertificateTrustGate(null, "baska.example.test");
        Assert.False(yabanci.Evaluate(sertifika, null, SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.NotNull(yabanci.Failure);
    }

    [Fact]
    public void Goc_SFTP_istemcisi_dogrulanmis_IPye_baglanir()
    {
        var istek = new RemoteFileMigrationRequest { Host = "kaynak.example.test", UserName = "u", Password = "p", Protocol = "sftp" };

        using var istemci = FileMigrationService.SftpIstemcisiOlustur(istek, new RemoteHostKeyTrustGate(null), "93.184.216.34");

        Assert.Equal("93.184.216.34", istemci.ConnectionInfo.Host);
        Assert.Equal(22, istemci.ConnectionInfo.Port);
    }

    // ------------------------------------------------------------------ yardımcılar

    private static X509Certificate2 SertifikaUret(params string[] adlar)
    {
        using var anahtar = RSA.Create(2048);
        var istek = new CertificateRequest($"CN={adlar[0]}", anahtar, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var ad in adlar)
        {
            san.AddDnsName(ad);
        }

        istek.CertificateExtensions.Add(san.Build());
        return istek.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    private sealed class SabitPinner(IPAddress? adres) : IBackupFtpAddressPinner
    {
        public int Calls { get; private set; }

        public Task<IPAddress> ResolveAsync(Uri uri, CancellationToken cancellationToken)
        {
            Calls++;
            return adres is null
                ? throw new AppException("backup.remote_target_forbidden", 403, 403, uri.Host)
                : Task.FromResult(adres);
        }
    }

    /// <summary>Düz FTP sahte sunucusu: NLST için tek dosya döner; PASV'de istenen adresi bildirir.</summary>
    private sealed class SahteFtp : IAsyncDisposable
    {
        private readonly TcpListener kontrol = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource iptal = new();
        private readonly string pasvAdresi;
        private readonly Task dongu;
        private int baglanti;

        public int Port => ((IPEndPoint)kontrol.LocalEndpoint).Port;

        public int Baglanti => Volatile.Read(ref baglanti);

        private SahteFtp(string pasvAdresi)
        {
            this.pasvAdresi = pasvAdresi;
            kontrol.Start();
            dongu = Task.Run(Kabul);
        }

        public static SahteFtp Baslat(string pasvAdresi = "127,0,0,1") => new(pasvAdresi);

        private async Task Kabul()
        {
            while (!iptal.IsCancellationRequested)
            {
                try
                {
                    var istemci = await kontrol.AcceptTcpClientAsync(iptal.Token);
                    Interlocked.Increment(ref baglanti);
                    _ = Task.Run(() => Oturum(istemci));
                }
                catch
                {
                    return;
                }
            }
        }

        private async Task Oturum(TcpClient istemci)
        {
            using var _ = istemci;
            using var veri = new TcpListener(IPAddress.Loopback, 0);
            veri.Start();
            try
            {
                await using var akis = istemci.GetStream();
                using var okuyucu = new StreamReader(akis, Encoding.ASCII);
                await using var yazici = new StreamWriter(akis, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };
                await yazici.WriteLineAsync("220 hazir");
                while (await okuyucu.ReadLineAsync(iptal.Token) is { } satir)
                {
                    switch (satir.Split(' ')[0].ToUpperInvariant())
                    {
                        case "USER": await yazici.WriteLineAsync("331 parola"); break;
                        case "PASS": await yazici.WriteLineAsync("230 giris"); break;
                        case "PWD": await yazici.WriteLineAsync("257 \"/\" dizin"); break;
                        case "PASV":
                            var port = ((IPEndPoint)veri.LocalEndpoint).Port;
                            await yazici.WriteLineAsync($"227 Pasif ({pasvAdresi},{port / 256},{port % 256})");
                            break;
                        case "NLST":
                            await yazici.WriteLineAsync("150 liste");
                            using (var d = await veri.AcceptTcpClientAsync(iptal.Token))
                            {
                                await d.GetStream().WriteAsync("a.zip\r\n"u8.ToArray(), iptal.Token);
                            }

                            await yazici.WriteLineAsync("226 tamam");
                            break;
                        case "DELE": case "RMD": await yazici.WriteLineAsync("250 tamam"); break;
                        case "QUIT": await yazici.WriteLineAsync("221 hoscakal"); return;
                        default: await yazici.WriteLineAsync("200 tamam"); break;
                    }
                }
            }
            catch
            {
                // İstemci kapattı.
            }
        }

        public async ValueTask DisposeAsync()
        {
            iptal.Cancel();
            kontrol.Stop();
            await dongu;
        }
    }
}
