using System.Text.Json;
using System.Text.Json.Nodes;

namespace AdaPanel.Infrastructure.Mail;

internal static class MailCertificateStoreReference
{
    internal const string StatePath = "config/mail-cert.json";
    internal enum Usage { None, InUse, Unknown }

    internal static async Task<Usage> ReadAsync(string path, string thumbprint, CancellationToken token)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var document = await JsonSerializer.DeserializeAsync<JsonObject>(stream, cancellationToken: token);
            if (document is null) return Usage.Unknown;
            var records = MailCertificateRegistry.Parse(document).Records;
            // Older references could contain only source/thumbprint, without a host field.
            if (records.Count == 0) records = [document];
            var unknown = false;
            foreach (var record in records)
            {
                var source = MailCertificateRegistry.Field(record, "source");
                if (source.Equals("letsencrypt", StringComparison.OrdinalIgnoreCase)
                    || source.Equals("self-signed", StringComparison.OrdinalIgnoreCase)) continue;
                var savedThumbprint = MailCertificateRegistry.Field(record, "thumbprint");
                if (!source.Equals("windows-store", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(savedThumbprint))
                { unknown = true; continue; }
                if (savedThumbprint.Equals(thumbprint, StringComparison.OrdinalIgnoreCase)) return Usage.InUse;
            }
            return unknown ? Usage.Unknown : Usage.None;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return Usage.None; }
        catch (Exception error) when (error is JsonException or IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { return Usage.Unknown; }
    }
}
