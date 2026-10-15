namespace AdaPanel.Core.Dtos.Seo;

/// <summary>Evidence from the fetched HTML, not a ranking or AI citation score.</summary>
public sealed record SeoContentSignalsDto
{
    public string Canonical { get; init; } = string.Empty;
    public int CanonicalCount { get; init; }
    public string Language { get; init; } = string.Empty;
    public int TitleCount { get; init; }
    public int SectionHeadingCount { get; init; }
    public int TextCharacters { get; init; }
    public int JsonLdBlocks { get; init; }
    public int InvalidJsonLdBlocks { get; init; }
    public bool StructuredDataLimited { get; init; }
    public bool OtherStructuredData { get; init; }
    public IReadOnlyList<string> SchemaTypes { get; init; } = [];
    public bool NoSnippet { get; init; }
    public IReadOnlyList<string> RobotDirectives { get; init; } = [];
    public bool BodyTruncated { get; init; }
}

public sealed class SeoBotAccessDto
{
    public string Agent { get; init; } = string.Empty;
    /// <summary>search | training. Training permission is not search eligibility.</summary>
    public string Purpose { get; init; } = "search";
    /// <summary>allowed | restricted | blocked | unknown. robots.txt only; not a live bot probe.</summary>
    public string Status { get; init; } = "unknown";
    public string MatchedGroup { get; init; } = string.Empty;
}
