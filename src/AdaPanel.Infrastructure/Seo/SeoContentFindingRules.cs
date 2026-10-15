using AdaPanel.Core.Dtos.Seo;

namespace AdaPanel.Infrastructure.Seo;

public static class SeoContentFindingRules
{
    public static IEnumerable<SeoFindingDto> Evaluate(SeoPageDto page)
    {
        if (page.ContentSignals is not { } signals) yield break;
        if (signals.BodyTruncated)
        {
            yield return Finding("html_truncated", "info");
            yield break;
        }
        if (signals.TitleCount > 1) yield return Finding("multiple_titles", "warn", signals.TitleCount.ToString());
        if (signals.Language.Length == 0) yield return Finding("missing_language", "info");
        if (signals.CanonicalCount > 1) yield return Finding("multiple_canonicals", "warn", signals.CanonicalCount.ToString());
        if (signals.Canonical.Length == 0) yield return Finding("missing_canonical", "info");
        else if (!Uri.TryCreate(signals.Canonical, UriKind.Absolute, out var canonical) || canonical.Scheme is not ("https" or "http"))
            yield return Finding("canonical_not_absolute", "info", signals.Canonical);
        if (signals.NoSnippet) yield return Finding("nosnippet", "info");
        if (signals.RobotDirectives.Any(value => value.Contains("googlebot:", StringComparison.OrdinalIgnoreCase)
            || value.Contains("bingbot:", StringComparison.OrdinalIgnoreCase)))
            yield return Finding("agent_directives", "info", string.Join("; ", signals.RobotDirectives));
        if (signals.InvalidJsonLdBlocks > 0) yield return Finding("invalid_jsonld", "warn", signals.InvalidJsonLdBlocks.ToString());
        if (signals.StructuredDataLimited) yield return Finding("jsonld_limited", "info");
        if (signals.JsonLdBlocks == 0 && !signals.OtherStructuredData) yield return Finding("structured_data_review", "info");
        if (signals.TextCharacters == 0) yield return Finding("no_html_text", "warn");

        SeoFindingDto Finding(string key, string severity, string detail = "") => new() { Key = key, Severity = severity, Url = page.Path, Detail = detail };
    }
}
