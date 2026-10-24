using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AdaPanel.Core.Dtos.Mail;
using MailKit.Security;

namespace AdaPanel.Infrastructure.Mail;

internal static class MailTlsProbe
{
    internal static bool ValidateCertificate(MailTlsEndpointDto result, string host, string? expectedThumbprint,
        X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (certificate is null)
        {
            result.Status = "fail";
            result.Detail = "No certificate was presented.";
            return false;
        }
        using var leaf = new X509Certificate2(certificate);
        result.Subject = leaf.Subject;
        result.Issuer = leaf.Issuer;
        result.Thumbprint = leaf.Thumbprint;
        result.NotAfterUtc = leaf.NotAfter.ToUniversalTime();
        var failures = new List<string>();
        if (errors != SslPolicyErrors.None)
        {
            failures.Add($"TLS validation: {errors}.");
            var chainErrors = chain?.ChainStatus.Where(s => s.Status != X509ChainStatusFlags.NoError)
                .Select(s => s.Status.ToString()).Distinct().ToArray() ?? [];
            if (chainErrors.Length > 0) failures.Add($"Chain: {string.Join(", ", chainErrors)}.");
        }
        try
        {
            if (!leaf.MatchesHostname(host, true, false)) failures.Add("Certificate SAN does not match the requested host.");
        }
        catch (CryptographicException) { failures.Add("Certificate SAN could not be read."); }
        var now = DateTime.UtcNow;
        if (leaf.NotBefore.ToUniversalTime() > now) failures.Add("Certificate is not yet valid.");
        if (result.NotAfterUtc <= now) failures.Add("Certificate has expired.");
        if (!string.IsNullOrWhiteSpace(expectedThumbprint)
            && !string.Equals(expectedThumbprint, leaf.Thumbprint, StringComparison.OrdinalIgnoreCase))
            failures.Add("The listener presents a different certificate.");
        result.Status = failures.Count == 0 ? "ok" : "fail";
        result.Detail = string.Join(" ", failures);
        // Observe rejected certificates for diagnostics, but NEVER accept them.
        return failures.Count == 0;
    }

    internal static Task<MailTlsEndpointDto> ProbeAsync(string host, int port, string? expectedThumbprint,
        CancellationToken token) => ProbeCoreAsync(host, port, expectedThumbprint, port == 587, token);

    internal static async Task<MailTlsEndpointDto> ProbeClientAsync(string host, int port, string protocol,
        bool startTls, CancellationToken token)
    {
        var result = new MailTlsEndpointDto { Port = port, Protocol = protocol + (startTls ? "/STARTTLS" : "/TLS"), CheckedAtUtc = DateTime.UtcNow };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        using MailKit.MailService client = protocol switch
        {
            "imap" => new MailKit.Net.Imap.ImapClient(),
            "pop3" => new MailKit.Net.Pop3.Pop3Client(),
            "smtp" => new MailKit.Net.Smtp.SmtpClient(),
            _ => throw new ArgumentOutOfRangeException(nameof(protocol))
        };
        client.ServerCertificateValidationCallback = (_, certificate, chain, errors) =>
            ValidateCertificate(result, host, null, certificate, chain, errors);
        try
        {
            await client.ConnectAsync(host, port, startTls ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect, timeout.Token);
            await client.DisconnectAsync(false, timeout.Token);
            if (result.Status != "ok") result.Status = "fail";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or SocketException or System.Security.Authentication.AuthenticationException
            or MailKit.ProtocolException or MailKit.CommandException or MailKit.Security.SslHandshakeException or OperationCanceledException or NotSupportedException)
        {
            result.Status = "fail";
            if (result.Detail.Length == 0) result.Detail = error is OperationCanceledException ? "Connection timed out." : error.GetType().Name;
        }
        return result;
    }

    internal static async Task<MailTlsEndpointDto> ProbeCoreAsync(string host, int port, string? expectedThumbprint,
        bool startTls, CancellationToken token)
    {
        var result = new MailTlsEndpointDto
        {
            Port = port, Protocol = startTls ? "STARTTLS" : "TLS", CheckedAtUtc = DateTime.UtcNow
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
            => ValidateCertificate(result, host, expectedThumbprint, certificate, chain, errors);
        try
        {
            if (port == 443)
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(host, port, timeout.Token);
                using var tls = new SslStream(tcp.GetStream(), false, Validate);
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    CertificateRevocationCheckMode = X509RevocationMode.Online
                }, timeout.Token);
            }
            else
            {
                using var smtp = new MailKit.Net.Smtp.SmtpClient { ServerCertificateValidationCallback = Validate };
                await smtp.ConnectAsync(host, port, startTls ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect,
                    timeout.Token);
                await smtp.DisconnectAsync(false, timeout.Token);
            }
            if (result.Status != "ok") result.Status = "fail";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (MailKit.Net.Smtp.SmtpCommandException error)
        {
            result.Status = "fail";
            // A rejected greeting or STARTTLS command is an endpoint failure, not a
            // failure of the entire multi-port inspection. Do not expose server reply text.
            result.Detail = $"SMTP command rejected (status {(int)error.StatusCode}, {error.ErrorCode}).";
        }
        catch (Exception error) when (error is IOException or SocketException or System.Security.Authentication.AuthenticationException
            or MailKit.ProtocolException or MailKit.Security.SslHandshakeException or OperationCanceledException or NotSupportedException)
        {
            result.Status = "fail";
            if (result.Detail.Length == 0) result.Detail = error is OperationCanceledException ? "Connection timed out." : error.GetType().Name;
        }
        return result;
    }
}
