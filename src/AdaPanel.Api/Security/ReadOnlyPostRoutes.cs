namespace AdaPanel.Api.Security;

// These endpoints carry read parameters in a body and publish no live resource changes.
// This is only a permission-policy exception: POST still requires normal cookie CSRF checks.
internal static class ReadOnlyPostRoutes
{
    internal static bool IsMatch(string? method, string? path)
    {
        if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)) return false;
        var normalized = path?.TrimEnd('/');
        return string.Equals(normalized, "/api/v1/backup/download-items", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "/api/v1/files/search", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "/api/v1/files/size", StringComparison.OrdinalIgnoreCase);
    }
}
