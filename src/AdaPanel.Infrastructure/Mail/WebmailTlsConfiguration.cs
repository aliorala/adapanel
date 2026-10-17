using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Support;
using MailKit.Security;

namespace AdaPanel.Infrastructure.Mail;

internal static class WebmailTlsConfiguration
{
    internal const string Marker = "Managed by AdaPanel";

    internal static string Options(string host, string caPath)
    {
        if (Uri.CheckHostName(host) != UriHostNameType.Dns || host.Contains('\''))
            throw new AppException("mail.certificate_not_activated", 409, 409, "Webmail requires the configured mail certificate hostname.");
        return "['ssl' => ['verify_peer' => true, 'verify_peer_name' => true, 'allow_self_signed' => false, "
            + "'peer_name' => '" + Php(host) + "', 'SNI_enabled' => true, 'cafile' => '" + Php(caPath.Replace('\\', '/')) + "']]";
    }

    private static string Php(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");

    // Replace only connection settings; preserve the session encryption key, database and custom options.
    internal static string Harden(string config, string options)
    {
        if (!config.Contains(Marker, StringComparison.OrdinalIgnoreCase)) return config;
        foreach (var key in new[] { "imap_conn_options", "smtp_conn_options", "managesieve_conn_options" })
        {
            var pattern = @"(?m)^\s*\$config\['" + key + @"'\]\s*=.*?;[ \t]*\r?$";
            var assignment = "$config['" + key + "'] = " + options + ";";
            if (Regex.IsMatch(config, pattern)) config = Regex.Replace(config, pattern, _ => assignment);
            else config += Environment.NewLine + assignment;
        }
        return Regex.Replace(config, @"(?m)^\s*\$config\['managesieve_usetls'\]\s*=.*?;[ \t]*\r?$",
            _ => "$config['managesieve_usetls'] = true;");
    }

    internal static async Task<string> PrepareAsync(JsonFileStore store, string installRoot, CancellationToken token)
    {
        var state = await store.ReadAsync<CertificateHost?>(MailCertificateStoreReference.StatePath, null, token);
        var host = state?.HostName?.Trim() ?? "";
        var caPath = Path.Combine(installRoot, "config", "adapanel-trusted-roots.pem");
        var options = Options(host, caPath);
        // Validate both actual services before migrating an existing managed config.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await Task.WhenAll(ProbeAsync(host, 143, true, timeout.Token), ProbeAsync(host, 587, false, timeout.Token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not AppException)
        {
            throw new AppException("mail.certificate_not_activated", 502, 502,
                "Webmail IMAP/SMTP STARTTLS validation failed (" + exception.GetType().Name + ").");
        }
        // PHP/OpenSSL does not automatically use the Windows machine trust store.
        // Export public trust anchors only; never leaf/private key material.
        using var roots = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
        roots.Open(OpenFlags.ReadOnly);
        var certificates = roots.Certificates;
        string bundle;
        try { bundle = string.Join(Environment.NewLine, certificates.Cast<X509Certificate2>().Select(c => c.ExportCertificatePem())); }
        finally { foreach (var certificate in certificates) certificate.Dispose(); }
        if (bundle.Length == 0) throw new AppException("mail.certificate_not_activated", 502, 502, "Windows machine trust store is empty.");
        Directory.CreateDirectory(Path.GetDirectoryName(caPath)!);
        if (!File.Exists(caPath) || await File.ReadAllTextAsync(caPath, token) != bundle)
            await File.WriteAllTextAsync(caPath, bundle, token);
        return options;
    }

    internal static async Task ProbeAsync(string host, int port, bool imap, CancellationToken token)
    {
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(IPAddress.Loopback, port, token);
        using MailKit.IMailService client = imap ? new MailKit.Net.Imap.ImapClient() : new MailKit.Net.Smtp.SmtpClient();
        // The host controls SNI and name validation; the already-connected socket stays local.
        await client.ConnectAsync(socket, host, port, SecureSocketOptions.StartTls, token);
        await client.DisconnectAsync(false, token);
    }

    internal sealed class CertificateHost { public string? HostName { get; set; } }
}
