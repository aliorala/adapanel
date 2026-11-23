namespace AdaPanel.Infrastructure.Git;

/// <summary>
/// G6: git dizininin yerel <c>config</c> dosyası için İZİN LİSTESİ. Listede olmayan her ayar (filter.*,
/// core.worktree, core.fsmonitor, http.*, include, url.*, credential.* ...) bir kod çalıştırma ya da
/// keyfî dosya yazma yoludur; bu yüzden sessizce yok sayılmaz, dağıtım REDDEDİLİR.
/// </summary>
internal static class GitConfigAllowList
{
    private static readonly HashSet<string> Core = new(StringComparer.OrdinalIgnoreCase)
    {
        "repositoryformatversion", "bare", "filemode", "logallrefupdates", "symlinks", "ignorecase"
    };

    private static readonly HashSet<string> Remote = new(StringComparer.OrdinalIgnoreCase) { "url", "fetch" };

    private static readonly HashSet<string> Branch = new(StringComparer.OrdinalIgnoreCase) { "remote", "merge" };

    /// <summary>İlk ihlalin açıklaması; temizse <c>null</c>.</summary>
    public static string? FindViolation(string configText)
    {
        string? section = null;
        string? subsection = null;
        var originUrls = 0;

        foreach (var raw in configText.Replace("\r", string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }

            if (line.EndsWith('\\'))
            {
                return "multi-line value is not allowed";
            }

            if (line[0] == '[')
            {
                var close = line.IndexOf(']');
                if (close < 0)
                {
                    return $"malformed section: {line}";
                }

                // Aynı satırda bölüm + anahtar ("[core] fsmonitor = x") biçimi de gerçek git'te geçerlidir.
                var header = line[1..close].Trim();
                var rest = line[(close + 1)..].Trim();
                var quote = header.IndexOf('"');
                if (quote >= 0)
                {
                    section = header[..quote].Trim().ToLowerInvariant();
                    subsection = header[(quote + 1)..].TrimEnd('"');
                }
                else
                {
                    section = header.ToLowerInvariant();
                    subsection = null;
                }

                if (!SectionAllowed(section, subsection))
                {
                    return $"section not allowed: [{header}]";
                }

                if (rest.Length > 0 && rest[0] is not '#' and not ';')
                {
                    var inline = KeyViolation(section, subsection, rest, ref originUrls);
                    if (inline is not null) return inline;
                }

                continue;
            }

            if (section is null)
            {
                return $"key outside any section: {line}";
            }

            var violation = KeyViolation(section, subsection, line, ref originUrls);
            if (violation is not null) return violation;
        }

        return null;
    }

    private static bool SectionAllowed(string section, string? subsection) => section switch
    {
        "core" => subsection is null,
        "remote" => string.Equals(subsection, "origin", StringComparison.Ordinal),
        "branch" => !string.IsNullOrEmpty(subsection),
        _ => false
    };

    private static string? KeyViolation(string section, string? subsection, string line, ref int originUrls)
    {
        var equals = line.IndexOf('=');
        var key = (equals < 0 ? line : line[..equals]).Trim();
        var allowed = section switch
        {
            "core" => Core,
            "remote" => Remote,
            _ => Branch
        };

        if (!allowed.Contains(key))
        {
            return $"setting not allowed: {section}{(subsection is null ? string.Empty : "." + subsection)}.{key}";
        }

        if (section == "remote" && key.Equals("url", StringComparison.OrdinalIgnoreCase) && ++originUrls > 1)
        {
            return "remote.origin.url is set more than once";
        }

        return null;
    }
}
