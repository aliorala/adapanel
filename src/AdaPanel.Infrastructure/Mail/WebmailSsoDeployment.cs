using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace AdaPanel.Infrastructure.Mail;

public sealed class WebmailSsoDeployment(JsonFileStore files, IConfiguration configuration, IOptions<PanelRuntimeOptions> options)
{
    private sealed class State { public string EntryRoot { get; set; } = ""; }
    private async Task<string> RootAsync(CancellationToken token)
    {
        var state = await files.ReadAsync<State?>("config/webmail.json", null, token);
        if (state is null || !Path.IsPathFullyQualified(state.EntryRoot) || Path.GetFileName(state.EntryRoot) != "public_html") throw Unavailable();
        var root = Path.GetDirectoryName(state.EntryRoot)!;
        if (!Directory.Exists(Path.Combine(root, "program"))) throw Unavailable();
        return root;
    }

    public async Task PrepareAsync(CancellationToken token)
    {
        var root = await RootAsync(token);
        var configPath = Path.Combine(root, "config", "config.inc.php");
        using var lease = await MailCertificateLifecycle.EnterAsync(configPath, token);
        var config = await File.ReadAllTextAsync(configPath, token);
        if (!config.Contains(WebmailTlsConfiguration.Marker, StringComparison.OrdinalIgnoreCase)) throw Unavailable();
        var keyFile = Path.Combine(root, "config", "adapanel-sso-broker.key");
        if (!File.Exists(keyFile))
        {
            var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).Replace('+', '-').Replace('/', '_');
            await PublishAsync(keyFile, key, token);
        }
        var keyText = await File.ReadAllTextAsync(keyFile, token);
        if (!Regex.IsMatch(keyText, "\\A[A-Za-z0-9_-]{64}\\z")) throw Unavailable();
        var pluginPath = Path.Combine(root, "plugins", "adapanel_sso");
        Directory.CreateDirectory(pluginPath);
        await using (var resource = typeof(WebmailSsoDeployment).Assembly.GetManifestResourceStream("AdaPanel.Infrastructure.Mail.Managed.adapanel_sso.php") ?? throw Unavailable())
        using (var reader = new StreamReader(resource))
            await PublishAsync(Path.Combine(pluginPath, "adapanel_sso.php"), await reader.ReadToEndAsync(token), token);
        var caFile = Path.Combine(root, "config", "adapanel-trusted-roots.pem");
        // The same public Windows trust anchors validate localhost's panel certificate in PHP.
        if (!File.Exists(caFile))
        {
            using var roots = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            roots.Open(OpenFlags.ReadOnly);
            var certificates = roots.Certificates;
            try { await File.WriteAllTextAsync(caFile, string.Join(Environment.NewLine, certificates.Cast<X509Certificate2>().Select(c => c.ExportCertificatePem())), token); }
            finally { foreach (var certificate in certificates) certificate.Dispose(); }
        }
        const string marker = "// AdaPanel SSO settings";
        const string endMarker = "// End AdaPanel SSO settings";
        config = Regex.Replace(config, Regex.Escape(marker) + ".*?" + Regex.Escape(endMarker) + @"\r?\n?", "", RegexOptions.Singleline);
        var broker = PanelBackendUrl.Resolve(configuration, options.Value) + "/api/internal/webmail-sso";
        config += "\n" + marker + "\n$config['plugins'] = array_values(array_unique(array_merge($config['plugins'] ?? [], ['adapanel_sso'])));\n"
            + "$config['session_domain'] = '';\nini_set('session.cookie_domain', '');\n"
            + "// A top-level cross-site HTTPS form must send the previous host-only session for replacement.\n"
            + "$config['session_samesite'] = in_array(strtolower($_SERVER['HTTPS'] ?? ''), ['on', '1'], true) ? 'None' : 'Lax';\n$config['session_storage'] = 'db';\n"
            + "$config['adapanel_sso'] = ['key_file' => '" + Php(keyFile) + "', 'broker_url' => '" + Php(broker) + "', 'ca_file' => '" + Php(caFile) + "'];\n" + endMarker + "\n";
        await PublishAsync(configPath, config, token);
    }

    public async Task<bool> AuthenticateBrokerAsync(string supplied, CancellationToken token)
    {
        if (supplied.Length != 64) return false;
        var root = await RootAsync(token);
        var path = Path.Combine(root, "config", "adapanel-sso-broker.key");
        if (!File.Exists(path)) return false;
        var expected = await File.ReadAllTextAsync(path, token);
        return expected.Length == 64 && CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(supplied), System.Text.Encoding.ASCII.GetBytes(expected));
    }
    private static string Php(string value) => value.Replace('\\', '/').Replace("'", "\\'");
    private static async Task PublishAsync(string path, string contents, CancellationToken token)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, contents, token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static AppException Unavailable() => new("mail.webmail_sso_unavailable", 503, 503);
}
