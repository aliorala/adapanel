using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using AdaPanel.Core.Dtos.Mail;
using MailKit.Security;

namespace AdaPanel.Infrastructure.Mail;

internal static class MailTlsProbe
{
    internal static Task<MailTlsEndpointDto> ProbeAsync(string host, int port, string? expectedThumbprint,
        CancellationToken token) => ProbeCoreAsync(host, port, expectedThumbprint, port == 587, token);

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
        {
            if (certificate is null) return false;
            using var leaf = new X509Certificate2(certificate);
            result.Subject = leaf.Subject;
            result.Issuer = leaf.Issuer;
            result.Thumbprint = leaf.Thumbprint;
            result.NotAfterUtc = leaf.NotAfter.ToUniversalTime();
            var valid = errors == SslPolicyErrors.None && leaf.MatchesHostname(host, true, false)
                && leaf.NotBefore.ToUniversalTime() <= DateTime.UtcNow && result.NotAfterUtc > DateTime.UtcNow;
            var matches = string.IsNullOrWhiteSpace(expectedThumbprint)
                || string.Equals(expectedThumbprint, leaf.Thumbprint, StringComparison.OrdinalIgnoreCase);
            result.Status = valid && matches ? "ok" : "fail";
            result.Detail = !valid ? $"TLS validation: {errors}; {string.Join(", ", chain?.ChainStatus.Select(s => s.Status.ToString()) ?? [])}"
                : !matches ? "The listener presents a different certificate." : string.Empty;
            // Observe rejected certificates for diagnostics, but NEVER accept them.
            return valid && matches;
        }
        try
        {
            if (port == 443)
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(host, port, timeout.Token);
                using var tls = new SslStream(tcp.GetStream(), false, Validate);
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, timeout.Token);
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
