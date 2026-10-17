using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Mail;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class WebmailTlsConfigurationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_local_starttls_rejects_untrusted_certificate_even_with_matching_name(bool imap)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=mail.example.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("mail.example.test");
        request.CertificateExtensions.Add(san.Build());
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync(timeout.Token);
            var stream = tcp.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
            using var writer = new StreamWriter(stream, Encoding.ASCII, 1024, true) { AutoFlush = true, NewLine = "\r\n" };
            if (imap)
            {
                await writer.WriteLineAsync("* OK [CAPABILITY IMAP4rev1 STARTTLS] ready");
                var command = await reader.ReadLineAsync(timeout.Token);
                Assert.EndsWith(" STARTTLS", command);
                await writer.WriteLineAsync(command!.Split(' ')[0] + " OK ready");
            }
            else
            {
                await writer.WriteLineAsync("220 localhost ESMTP");
                Assert.StartsWith("EHLO", await reader.ReadLineAsync(timeout.Token));
                await writer.WriteLineAsync("250-localhost\r\n250 STARTTLS");
                Assert.Equal("STARTTLS", await reader.ReadLineAsync(timeout.Token));
                await writer.WriteLineAsync("220 Ready");
            }
            using var tls = new SslStream(stream);
            try { await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = cert }, timeout.Token); }
            catch (Exception error) when (error is IOException or AuthenticationException) { }
        }, timeout.Token);
        await Assert.ThrowsAsync<MailKit.Security.SslHandshakeException>(() =>
            WebmailTlsConfiguration.ProbeAsync("mail.example.test", port, imap, timeout.Token));
        await server;
    }

    [Fact]
    public void Migration_enables_validation_and_preserves_existing_session_key_and_custom_settings()
    {
        const string source = """
            <?php
            // Managed by AdaPanel
            $config['des_key'] = 'existing-session-key';
            $config['smtp_host'] = 'tls://localhost:587';
            $config['imap_conn_options'] = ['ssl' => ['verify_peer' => false]];
            $config['smtp_conn_options'] = ['ssl' => ['allow_self_signed' => true]];
            $config['managesieve_conn_options'] = ['ssl' => ['verify_peer_name' => false]];
            $config['managesieve_usetls'] = false;
            $config['custom'] = 'keep';
            """;
        var options = WebmailTlsConfiguration.Options("mail.example.test", "C:\\trusted\\roots.pem");
        var result = WebmailTlsConfiguration.Harden(source, options);
        Assert.Contains("'peer_name' => 'mail.example.test'", result);
        Assert.Contains("'cafile' => 'C:/trusted/roots.pem'", result);
        Assert.Contains("$config['des_key'] = 'existing-session-key';", result);
        Assert.Contains("$config['custom'] = 'keep';", result);
        Assert.Contains("$config['smtp_host'] = 'tls://localhost:587';", result);
        Assert.Contains("$config['managesieve_usetls'] = true;", result);
        Assert.DoesNotContain("'verify_peer' => false", result);
        Assert.DoesNotContain("'verify_peer_name' => false", result);
        Assert.DoesNotContain("'allow_self_signed' => true", result);
        Assert.Equal(result, WebmailTlsConfiguration.Harden(result, options));
    }

    [Fact]
    public void Custom_file_is_unchanged()
    {
        const string source = "<?php $config['custom'] = 'owned by operator';";
        Assert.Equal(source, WebmailTlsConfiguration.Harden(source, WebmailTlsConfiguration.Options("mail.example.test", "C:/roots.pem")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("127.0.0.1")]
    [InlineData("mail.example.test'; phpinfo();//")]
    [InlineData("https://mail.example.test")]
    public void Missing_or_invalid_certificate_hostname_is_rejected(string host)
        => Assert.Throws<AppException>(() => WebmailTlsConfiguration.Options(host, "C:/roots.pem"));

    [Fact]
    public void Php_path_is_escaped()
        => Assert.Contains("C:/operator\\'s/roots.pem", WebmailTlsConfiguration.Options("mail.example.test", "C:/operator's/roots.pem"));
}
