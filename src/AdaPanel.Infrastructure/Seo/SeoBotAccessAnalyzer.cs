using AdaPanel.Core.Dtos.Seo;

namespace AdaPanel.Infrastructure.Seo;

/// <summary>Conservative, site-wide robots policy summary, not a URL permission evaluator.</summary>
public static class SeoBotAccessAnalyzer
{
    private static readonly (string Agent, string Purpose)[] Agents =
        [("Googlebot", "search"), ("Bingbot", "search"), ("OAI-SearchBot", "search"), ("GPTBot", "training")];

    public static IReadOnlyList<SeoBotAccessDto> Analyze(string? text, int statusCode = 200, bool incomplete = false)
    {
        var groups = Parse(text ?? "");
        return Agents.Select(agent =>
        {
            if (incomplete || statusCode is not (200 or 404 or 410))
                return new SeoBotAccessDto { Agent = agent.Agent, Purpose = agent.Purpose };
            var selected = groups.Where(group => group.Agents.Contains(agent.Agent, StringComparer.OrdinalIgnoreCase)).ToArray();
            var specific = selected.Length > 0;
            if (!specific) selected = groups.Where(group => group.Agents.Contains("*")).ToArray();
            return new SeoBotAccessDto
            {
                Agent = agent.Agent, Purpose = agent.Purpose,
                Status = statusCode == 200 ? Summarize(selected) : "allowed",
                MatchedGroup = selected.Length == 0 ? "" : specific ? agent.Agent : "*"
            };
        }).ToArray();
    }

    public static bool BlocksWildcard(string text) => Summarize(Parse(text).Where(group => group.Agents.Contains("*"))) == "blocked";

    private static string Summarize(IEnumerable<Group> groups)
    {
        var rules = groups.SelectMany(group => group.Rules).Where(rule => rule.Value.Length > 0).ToArray();
        var denies = rules.Where(rule => !rule.Allow).ToArray();
        if (denies.Length == 0) return "allowed";
        var allows = rules.Where(rule => rule.Allow).ToArray();
        if (denies.Any(rule => rule.Value is "/" or "/*") && allows.Length == 0) return "blocked";
        // Any path exceptions/complex patterns require URL-specific interpretation.
        // Only report unrestricted when identical allow rules neutralize every disallow.
        if (denies.All(deny => allows.Any(allow => allow.Value == deny.Value))) return "allowed";
        return "restricted";
    }

    private static List<Group> Parse(string text)
    {
        var groups = new List<Group>();
        Group? current = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Split('#', 2)[0].Trim();
            var separator = line.IndexOf(':');
            if (separator <= 0) continue;
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key.Equals("user-agent", StringComparison.OrdinalIgnoreCase))
            {
                if (current is null || current.Rules.Count > 0)
                {
                    current = new Group();
                    groups.Add(current);
                }
                current.Agents.Add(value);
            }
            else if (current is not null && (key.Equals("allow", StringComparison.OrdinalIgnoreCase) || key.Equals("disallow", StringComparison.OrdinalIgnoreCase)))
            {
                current.Rules.Add((key.Equals("allow", StringComparison.OrdinalIgnoreCase), value));
            }
        }
        return groups;
    }

    private sealed class Group
    {
        public List<string> Agents { get; } = [];
        public List<(bool Allow, string Value)> Rules { get; } = [];
    }
}
