using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AdaPanel.Core.Dtos.Ftp;

namespace AdaPanel.Infrastructure.Ftp;

internal static class FtpTlsProbe
{
    internal static async Task<FtpCertificateDto> ProbeAsync(string host, string thumbprint, CancellationToken token)
    {
        var result = new FtpCertificateDto { HostName = host, CertificateThumbprint = thumbprint, CertificateVerified = false };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var tcp = new TcpClient();
            // Measure this installed FTP service, without DNS/hairpin NAT selecting another server.
            await tcp.ConnectAsync("127.0.0.1", 21, timeout.Token);
            using var stream = tcp.GetStream();
            await ReadReplyAsync(stream, 220, timeout.Token);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("AUTH TLS\r\n"), timeout.Token);
            await ReadReplyAsync(stream, 234, timeout.Token);
            using var tls = new SslStream(stream, leaveInnerStreamOpen: true, (_, certificate, chain, errors) =>
            {
                if (certificate is null) return false;
                using var leaf = new X509Certificate2(certificate);
                result.CertificateSubject = leaf.Subject;
                result.CertificateNotAfterUtc = leaf.NotAfter.ToUniversalTime();
                var valid = errors == SslPolicyErrors.None && leaf.MatchesHostname(host, true, false)
                    && leaf.NotBefore.ToUniversalTime() <= DateTime.UtcNow && leaf.NotAfter.ToUniversalTime() > DateTime.UtcNow
                    && string.Equals(leaf.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase);
                if (!valid) result.CertificateDetail = $"FTPS certificate rejected: {errors}; certificate/name/date/expected-thumbprint validation failed.";
                return valid;
            });
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            { TargetHost = host, CertificateRevocationCheckMode = X509RevocationMode.Online }, timeout.Token);
            result.CertificateVerified = true;
            result.CertificateDetail = "Local port 21 AUTH TLS: host, certificate lifetime, trust chain and expected thumbprint verified. Authenticated data transfers were not tested.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or SocketException or System.Security.Authentication.AuthenticationException or OperationCanceledException)
        { result.CertificateDetail ??= error is OperationCanceledException ? "FTPS connection timed out." : error.Message; }
        return result;
    }

    internal static async Task ReadReplyAsync(Stream stream, int expectedCode, CancellationToken token)
    {
        var total = 0;
        string? multiline = null;
        for (var lines = 0; lines < 100; lines++)
        {
            var line = new StringBuilder();
            var single = new byte[1];
            while (true)
            {
                if (++total > 16384) throw new IOException("FTP response exceeds the allowed size.");
                if (await stream.ReadAsync(single, token) == 0) throw new IOException("FTP closed the connection.");
                if (single[0] == 10) break;
                if (single[0] != 13) line.Append((char)single[0]);
            }
            var text = line.ToString();
            if (multiline is not null && !text.StartsWith(multiline + " ", StringComparison.Ordinal)) continue;
            if (text.Length < 4 || !int.TryParse(text.AsSpan(0, 3), out var code)) throw new IOException("Invalid FTP response.");
            if (code != expectedCode) throw new IOException($"FTP command rejected (status {code}); no credentials were sent.");
            if (text[3] == '-') { multiline ??= text[..3]; continue; }
            if (text[3] != ' ') throw new IOException("Invalid FTP response terminator.");
            return;
        }
        throw new IOException("Too many FTP response lines.");
    }
}
