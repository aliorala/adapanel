using System.Globalization;
using AdaPanel.Core.Common;

namespace AdaPanel.Api.Security;

internal static class WebmailRequestValidation
{
    internal static string Domain(string? value)
    {
        var domain = (value ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        try { domain = new IdnMapping().GetAscii(domain); }
        catch (ArgumentException) { throw Invalid(); }
        if (!domain.Contains('.') || domain.StartsWith("webmail.", StringComparison.Ordinal) || domain.Length > 245 ||
            domain.Split('.').Any(x => x.Length is 0 or > 63 || x[0] == '-' || x[^1] == '-' || x.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
            throw Invalid();
        return domain;
    }
    private static AppException Invalid() => new("mail.webmail_domain_invalid", 400, 400);
}
