using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AdaPanel.Core.Common;

namespace AdaPanel.Infrastructure.Mail;

internal static class MailCertificatePemFiles
{
    internal static (string CertificatePem, string PrivateKeyPem) Read(string directory, Action? afterSnapshot = null)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw FilesUnreadable();
        try
        {
            string SelectKey() => Directory.EnumerateFiles(directory, "*-key.pem")
                .OrderByDescending(File.GetLastWriteTimeUtc).ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault() ?? throw FilesUnreadable();
            var keyFile = SelectKey();
            // win-acme's four PEM files share one prefix, which can be a custom name.
            // Selecting each file independently by timestamp can combine different renewals.
            var prefix = keyFile[..^"-key.pem".Length];
            Dictionary<string, string> Snapshot()
            {
                var files = new[] { prefix + "-crt.pem", prefix + "-chain.pem", prefix + "-chain-only.pem" }
                    .Where(File.Exists).ToArray();
                if (files.Length == 0) throw FilesUnreadable();
                var contents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in new[] { keyFile }.Concat(files))
                {
                    // Do not make win-acme fail a renewal by denying its write/replace access.
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream, Encoding.UTF8, true);
                    contents.Add(file, reader.ReadToEnd());
                }
                return contents;
            }
            var first = Snapshot();
            afterSnapshot?.Invoke(); // Test-only boundary; the live path supplies no callback.
            var second = Snapshot();
            if (!string.Equals(keyFile, SelectKey(), StringComparison.OrdinalIgnoreCase) || first.Count != second.Count ||
                first.Any(item => !second.TryGetValue(item.Key, out var value) || value != item.Value)) throw FilesUnreadable();
            // Matching snapshots detect concurrent changes; key/chain validation below and
            // before activation also reject inconsistent material left between writer steps.
            var privateKey = first[keyFile];
            var blocks = new List<string>();
            foreach (var file in first.Keys.Where(path => !string.Equals(path, keyFile, StringComparison.OrdinalIgnoreCase)))
            {
                var text = first[file];
                var extracted = MailService.ExtractCertificateBlocks(text);
                var started = text.Split("-----BEGIN CERTIFICATE-----", StringSplitOptions.None).Length - 1;
                if (started != extracted.Count || (extracted.Count == 0 &&
                    (!file.EndsWith("-chain-only.pem", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(text))))
                    throw FilesUnreadable();
                foreach (var block in extracted)
                    if (!blocks.Contains(block, StringComparer.Ordinal)) blocks.Add(block);
            }
            if (blocks.Count == 0) throw FilesUnreadable();
            var pem = string.Join("\n", blocks) + "\n";
            var parsed = new X509Certificate2Collection();
            try
            {
                parsed.ImportFromPem(pem);
                // Detect incomplete renewals before the hash shortcut or any provider command.
                // System trust, SAN, expiry and full-chain checks still run before activation.
                using var leaf = X509Certificate2.CreateFromPem(pem, privateKey);
                return (pem, privateKey);
            }
            finally { foreach (var certificate in parsed) certificate.Dispose(); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw FilesUnreadable();
        }
        catch (Exception error) when (error is CryptographicException or ArgumentException)
        {
            throw new AppException("mail.certificate_invalid", 409, 409);
        }
    }

    private static AppException FilesUnreadable() => new("mail.certificate_files_unreadable", 409, 409);
}
