using AdaPanel.Infrastructure.Mail;
using AdaPanel.Infrastructure.Notifications;
using MailKit.Security;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Posta sunucusuna gönderilen sertifika zinciri ve giden SMTP'nin şifreleme kipi.
/// <para>
/// İkisi de canlıda ısırdı: (1) Stalwart'a yalnız YAPRAK sertifika gidiyordu, ara sertifika
/// düşüyordu — OpenSSL tabanlı istemciler (PHP, Python, çoğu posta istemcisi) zinciri kuramayıp
/// bağlantıyı kesiyordu. Windows istemcileri eksik ara sertifikayı kendi indirdiği için sorun uzun
/// süre GÖRÜNMEDİ. (2) Panel port 465'e düz metin bağlanıyordu, çünkü kullanılan sınıf örtük TLS'i
/// desteklemiyordu.
/// </para>
/// </summary>
public sealed class MailCertificateChainTests
{
    private const string Leaf =
        "-----BEGIN CERTIFICATE-----\nWUFQUkFL\n-----END CERTIFICATE-----";

    private const string Intermediate =
        "-----BEGIN CERTIFICATE-----\nQVJB\n-----END CERTIFICATE-----";

    [Fact]
    public void ExtractCertificateBlocks_ReadsEveryBlockInOrder()
    {
        var blocks = MailService.ExtractCertificateBlocks($"{Leaf}\n{Intermediate}\n");

        Assert.Equal(2, blocks.Count);
        Assert.Contains("WUFQUkFL", blocks[0], StringComparison.Ordinal);
        Assert.Contains("QVJB", blocks[1], StringComparison.Ordinal);
    }

    // Asil hata buydu: -chain.pem yaprak DAHIL tam zincir oldugunda, "yaprak yinelenmesin" denetimi
    // zincirin tamamini atiyordu ve ara sertifika kayboluyordu.
    [Fact]
    public void ExtractCertificateBlocks_KeepsIntermediateWhenChainAlsoContainsTheLeaf()
    {
        var fullChain = $"{Leaf}\n{Intermediate}";
        var blocks = MailService.ExtractCertificateBlocks(fullChain);

        Assert.Equal(2, blocks.Count);
        Assert.Contains(blocks, block => block.Contains("QVJB", StringComparison.Ordinal));
    }

    // Ayni sertifika bir dosyada CRLF, digerinde LF ile yazilmis olabilir; ham metin karsilastirmasi
    // bunlari FARKLI sanar ve yaprak zincire ikinci kez eklenir.
    [Fact]
    public void ExtractCertificateBlocks_NormalisesLineEndingsSoDuplicatesCanBeDetected()
    {
        var crlf = MailService.ExtractCertificateBlocks(Leaf.Replace("\n", "\r\n"));
        var lf = MailService.ExtractCertificateBlocks(Leaf);

        Assert.Equal(lf[0], crlf[0]);
    }

    [Fact]
    public void ExtractCertificateBlocks_IgnoresTrailingGarbageAndUnterminatedBlocks()
    {
        var blocks = MailService.ExtractCertificateBlocks($"Bag Attributes: x\n{Leaf}\n-----BEGIN CERTIFICATE-----\nkopuk");

        Assert.Single(blocks);
    }

    // Port 465 ORTUK TLS ister: baglanti ilk bayttan sifrelidir, STARTTLS beklenmez.
    [Fact]
    public void ResolveSecureSocketOptions_UsesImplicitTlsOnPort465()
    {
        Assert.Equal(SecureSocketOptions.SslOnConnect, NotificationService.ResolveSecureSocketOptions(useSsl: true, port: 465));
    }

    [Theory]
    [InlineData(587)]
    [InlineData(25)]
    [InlineData(2525)]
    public void ResolveSecureSocketOptions_UsesStartTlsOnSubmissionPorts(int port)
    {
        // WhenAvailable DEGIL: sunucu STARTTLS duyurmuyorsa sessizce duz metne dusmek, kullanici
        // sifreleme istemisken parolayi aga acik gondermek olurdu.
        Assert.Equal(SecureSocketOptions.StartTls, NotificationService.ResolveSecureSocketOptions(useSsl: true, port));
    }

    [Fact]
    public void ResolveSecureSocketOptions_HonoursExplicitlyDisabledEncryption()
    {
        Assert.Equal(SecureSocketOptions.None, NotificationService.ResolveSecureSocketOptions(useSsl: false, port: 465));
    }
}
