using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AdaPanel.Infrastructure.Mail;

internal static class SmtpGreetingProbe
{
    // A short, read-only health probe, not an SMTP delivery session. No mail is sent.
    internal static async Task<bool> MatchesAsync(string expectedHost, CancellationToken token,
        int port = 25, TimeSpan? timeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(6));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
            using var reader = new StreamReader(client.GetStream(), Encoding.ASCII);
            string? host = null;
            var buffer = new char[1];
            for (var lineIndex = 0; lineIndex < 20; lineIndex++)
            {
                var line = new StringBuilder();
                while (true)
                {
                    if (await reader.ReadAsync(buffer.AsMemory(), deadline.Token) == 0)
                        throw new IOException("SMTP greeting ended before a complete reply.");
                    if (buffer[0] == '\n') break;
                    if (line.Length >= 511) throw new IOException("SMTP greeting line is too long.");
                    line.Append(buffer[0]);
                }
                var text = line.ToString().TrimEnd('\r');
                if (!text.StartsWith("220", StringComparison.Ordinal) ||
                    (text.Length > 3 && text[3] is not (' ' or '-')))
                    throw new IOException("SMTP server did not return a valid service-ready greeting.");
                if (lineIndex == 0)
                {
                    if (text.Length <= 4) throw new IOException("SMTP greeting contains no server hostname.");
                    host = text[4..].Split(' ', 2)[0].TrimEnd('.');
                    if (string.IsNullOrWhiteSpace(host)) throw new IOException("SMTP greeting contains no server hostname.");
                }
                if (text.Length == 3 || text[3] == ' ')
                    return string.Equals(host, expectedHost.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);
            }
            throw new IOException("SMTP greeting has too many continuation lines.");
        }
        catch (OperationCanceledException exception) when (!token.IsCancellationRequested)
        {
            throw new IOException("SMTP greeting could not be measured within the health-check timeout.", exception);
        }
    }
}
