using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AdaPanel.Core.Dtos.Seo;

namespace AdaPanel.Infrastructure.Seo;

/// <summary>Bounded, static HTML evidence. JSON syntax checks do not validate rich-result eligibility.</summary>
public static partial class SeoContentAnalyzer
{
    public const int MaxJsonLdBlocks = 32;
    public const int MaxJsonLdLength = 262144;

    public static SeoContentSignalsDto Analyze(string html)
    {
        var source = Comments().Replace(html, " ");
        var blocks = 0;
        var invalid = 0;
        var limited = false;
        var types = new HashSet<string>(StringComparer.Ordinal);
        // Inspect JSON-LD before stripping scripts; never execute or resolve external contexts.
        foreach (Match script in Scripts().Matches(source))
        {
            var attributes = SeoHtmlAnalyzer.ReadAttributes(script.Groups[1].Value);
            if (!attributes.TryGetValue("type", out var type) || !type.Trim().Equals("application/ld+json", StringComparison.OrdinalIgnoreCase)) continue;
            blocks++;
            if (blocks > MaxJsonLdBlocks || script.Groups[2].Length > MaxJsonLdLength)
            {
                limited = true;
                continue;
            }
            try
            {
                using var document = JsonDocument.Parse(script.Groups[2].Value, new JsonDocumentOptions { MaxDepth = 32 });
                if (document.RootElement.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object)) invalid++;
                else CollectTypes(document.RootElement, types);
            }
            catch (JsonException) { invalid++; }
        }

        var clean = NonContent().Replace(source, " ");
        var canonical = "";
        var canonicalCount = 0;
        var language = "";
        var otherData = false;
        var directives = new List<string>();
        var noSnippet = false;
        foreach (Match tag in Tags().Matches(clean))
        {
            var name = tag.Groups[1].Value;
            var attrs = SeoHtmlAnalyzer.ReadAttributes(tag.Value);
            otherData |= attrs.ContainsKey("itemtype") || attrs.ContainsKey("typeof");
            if (name.Equals("html", StringComparison.OrdinalIgnoreCase) && attrs.TryGetValue("lang", out var lang)) language = Clip(lang, 80);
            if (name.Equals("link", StringComparison.OrdinalIgnoreCase) && attrs.TryGetValue("rel", out var rel)
                && rel.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains("canonical", StringComparer.OrdinalIgnoreCase))
            {
                canonicalCount++;
                if (canonicalCount == 1 && attrs.TryGetValue("href", out var href)) canonical = Clip(href, 2048);
            }
            if (name.Equals("meta", StringComparison.OrdinalIgnoreCase) && attrs.TryGetValue("name", out var metaName)
                && new[] { "robots", "googlebot", "bingbot" }.Contains(metaName.Trim(), StringComparer.OrdinalIgnoreCase)
                && attrs.TryGetValue("content", out var value))
            {
                if (directives.Count < 20) directives.Add(Clip($"{metaName}: {value}", 300));
                if (metaName.Equals("robots", StringComparison.OrdinalIgnoreCase)) noSnippet |= HasDirective(value, "nosnippet") || ZeroSnippet().IsMatch(value);
            }
        }
        var body = Head().Replace(clean, " ");
        var text = Space().Replace(WebUtility.HtmlDecode(Tags().Replace(body, " ")), " ").Trim();
        return new SeoContentSignalsDto
        {
            Canonical = canonical, CanonicalCount = canonicalCount, Language = language,
            TitleCount = Titles().Matches(clean).Count, SectionHeadingCount = Sections().Matches(clean).Count,
            TextCharacters = text.Length, JsonLdBlocks = blocks, InvalidJsonLdBlocks = invalid,
            SchemaTypes = types.Order(StringComparer.Ordinal).ToArray(), StructuredDataLimited = limited,
            OtherStructuredData = otherData, NoSnippet = noSnippet, RobotDirectives = directives
        };
    }

    public static bool HasDirective(string value, string directive) => value.Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Contains(directive, StringComparer.OrdinalIgnoreCase);

    public static bool HasZeroSnippet(string value) => ZeroSnippet().IsMatch(value);

    // Agent-specific header scopes persist until the next agent prefix in the same header.
    public static string GenericHeaderDirectives(IEnumerable<string> headers)
    {
        var result = new List<string>();
        foreach (var header in headers)
        {
            var scoped = false;
            foreach (var part in header.Split(','))
            {
                var token = part.Trim();
                var colon = token.IndexOf(':');
                if (colon >= 0 && !new[] { "max-snippet", "max-image-preview", "max-video-preview", "unavailable_after" }
                    .Contains(token[..colon].Trim(), StringComparer.OrdinalIgnoreCase)) scoped = true;
                if (!scoped) result.Add(token);
            }
        }
        return string.Join(", ", result);
    }

    private static void CollectTypes(JsonElement element, HashSet<string> types)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray()) CollectTypes(child, types);
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("@type"))
                {
                    if (property.Value.ValueKind == JsonValueKind.String) AddType(property.Value.GetString());
                    else if (property.Value.ValueKind == JsonValueKind.Array)
                        foreach (var value in property.Value.EnumerateArray())
                            if (value.ValueKind == JsonValueKind.String) AddType(value.GetString());
                }
                CollectTypes(property.Value, types);
            }
        }
        void AddType(string? value) { if (!string.IsNullOrWhiteSpace(value) && types.Count < 40) types.Add(Clip(value, 120)); }
    }

    private static string Clip(string value, int length) => value.Trim().Length <= length ? value.Trim() : value.Trim()[..length];
    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline, 1000)] private static partial Regex Comments();
    [GeneratedRegex("<script\\b([^>]*)>(.*?)</script\\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)] private static partial Regex Scripts();
    [GeneratedRegex("<(script|style|template)\\b[^>]*>.*?</\\1\\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)] private static partial Regex NonContent();
    [GeneratedRegex("</?([a-zA-Z][a-zA-Z0-9:-]*)\\b[^>]*>", RegexOptions.Singleline, 1000)] private static partial Regex Tags();
    [GeneratedRegex("<head\\b[^>]*>.*?</head\\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)] private static partial Regex Head();
    [GeneratedRegex("<title\\b[^>]*>", RegexOptions.IgnoreCase, 1000)] private static partial Regex Titles();
    [GeneratedRegex("<h[2-6]\\b[^>]*>", RegexOptions.IgnoreCase, 1000)] private static partial Regex Sections();
    [GeneratedRegex("\\s+", RegexOptions.None, 1000)] private static partial Regex Space();
    [GeneratedRegex("(?:^|[,\\s])max-snippet\\s*:\\s*0(?:$|[,\\s])", RegexOptions.IgnoreCase, 1000)] private static partial Regex ZeroSnippet();
}
