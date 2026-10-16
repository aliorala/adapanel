using System.Text.Json;

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
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return Usage.Unknown;
            string? Field(string name)
            {
                var fields = document.RootElement.EnumerateObject()
                    .Where(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
                return fields.Length == 1 && fields[0].Value.ValueKind == JsonValueKind.String
                    ? fields[0].Value.GetString() : null;
            }
            var source = Field("source");
            if (string.Equals(source, "letsencrypt", StringComparison.OrdinalIgnoreCase)
                || string.Equals(source, "self-signed", StringComparison.OrdinalIgnoreCase)) return Usage.None;
            if (!string.Equals(source, "windows-store", StringComparison.OrdinalIgnoreCase)) return Usage.Unknown;
            var savedThumbprint = Field("thumbprint");
            if (string.IsNullOrWhiteSpace(savedThumbprint)) return Usage.Unknown;
            return string.Equals(savedThumbprint, thumbprint, StringComparison.OrdinalIgnoreCase) ? Usage.InUse : Usage.None;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return Usage.None; }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException) { return Usage.Unknown; }
    }
}
