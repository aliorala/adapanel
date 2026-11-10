using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AdaPanel.Core.Dtos.Migration;

namespace AdaPanel.Infrastructure.Migration.Sources;

internal static class CpanelCronReader
{
    internal static PortableMigrationTask Read(string line, int number, string rawArtifact,
        Func<string, List<string>> resolveDomain, Func<string, (List<string> SiteIds, List<string> ArtifactPaths)> resolveScript,
        Action<string, string, string> issue)
    {
        var id = "task:cron:" + number;
        var command = line;
        var scheduleType = "unknown";
        var scheduleValue = "";
        var interval = 1;
        var time = "";
        var scheduleKnown = false;
        var kind = "shell";
        var args = "";
        var url = "";
        var workingDirectory = "";
        var artifacts = new List<string> { rawArtifact };
        var siteIds = new List<string>();
        try
        {
            if (line.StartsWith('@'))
            {
                var split = line.IndexOfAny([' ', '\t']);
                if (split < 0) throw new FormatException();
                var macro = line[..split];
                if (macro is not ("@daily" or "@midnight" or "@hourly" or "@weekly" or "@monthly" or "@yearly" or "@annually" or "@reboot")) throw new FormatException();
                scheduleType = "cron"; scheduleValue = macro;
                command = line[(split + 1)..].TrimStart();
                if (macro is "@daily" or "@midnight") { scheduleType = "daily"; time = "00:00"; scheduleKnown = true; }
            }
            else
            {
                var match = Regex.Match(line, @"^(\S+)\s+(\S+)\s+(\S+)\s+(\S+)\s+(\S+)\s+(.+)$", RegexOptions.CultureInvariant);
                if (!match.Success) throw new FormatException();
                command = match.Groups[6].Value;
                var fields = Enumerable.Range(1, 5).Select(i => match.Groups[i].Value).ToArray();
                if (fields.Any(field => !Regex.IsMatch(field, @"^[A-Za-z0-9*/?,\-]+$", RegexOptions.CultureInvariant))) throw new FormatException();
                scheduleType = "cron";
                scheduleValue = line[..match.Groups[6].Index].TrimEnd();
                // Task Scheduler's repeating trigger starts relative to import time. Cron is
                // aligned to wall-clock minute/hour boundaries, including the hourly reset
                // of */N. Preserve that expression for explicit mapping instead of drifting it.
                if (fields.Skip(2).All(f => f == "*") && int.TryParse(fields[0], out var minute) && minute is >= 0 and <= 59
                    && int.TryParse(fields[1], out var hour) && hour is >= 0 and <= 23)
                { scheduleType = "daily"; time = $"{hour:00}:{minute:00}"; scheduleKnown = true; }
            }
            var words = ShellWords(command);
            if (words.Count == 0) throw new FormatException();
            var executable = words[0].Split('/').Last();
            if (executable is "curl" or "wget")
            {
                var candidates = words.Skip(1).Where(w => Uri.TryCreate(w, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https").ToArray();
                var allowed = executable == "curl" ? new[] { "-f", "-s", "-S", "-fsS", "-sS", "--silent", "--fail", "--show-error" } : new[] { "-q", "--quiet", "-O", "/dev/null" };
                if (candidates.Length != 1 || words.Skip(1).Any(w => w != candidates[0] && !allowed.Contains(w, StringComparer.Ordinal))) throw new FormatException();
                var uri = new Uri(candidates[0]);
                if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0) throw new FormatException();
                kind = "url"; url = uri.AbsoluteUri; args = ""; command = url;
                siteIds.AddRange(resolveDomain(uri.Host));
            }
            else if (Regex.IsMatch(executable, @"^(?:php(?:[0-9](?:\.[0-9])?)?|ea-php[0-9]{2})$", RegexOptions.CultureInvariant))
            {
                var index = 1;
                while (index < words.Count && words[index] is "-q" or "-f") index++;
                if (index >= words.Count || !words[index].StartsWith('/') || !words[index].EndsWith(".php", StringComparison.OrdinalIgnoreCase)) throw new FormatException();
                // Arbitrary source arguments may carry passwords/tokens; retain them only in the raw private artifact.
                if (index + 1 != words.Count) throw new FormatException();
                command = words[index]; args = "";
                workingDirectory = command[..command.LastIndexOf('/')]; kind = "php";
                var mapping = resolveScript(command);
                siteIds.AddRange(mapping.SiteIds); artifacts.AddRange(mapping.ArtifactPaths);
                if (siteIds.Count == 0) issue("cpanel.task.site_mapping_required", id, "The PHP script could not be associated with a proven document root.");
            }
            else throw new FormatException();
        }
        catch (FormatException)
        {
            scheduleKnown = false;
            issue("cpanel.task.command_unsupported", id, "The cron command or environment requires explicit manual mapping; its raw source is a private artifact.");
            // Unknown shell commands may embed credentials. Preserve them only in the private raw artifact.
            command = ""; args = ""; url = ""; workingDirectory = ""; kind = "shell"; siteIds.Clear(); artifacts = [rawArtifact];
        }
        if (!scheduleKnown) issue("cpanel.task.schedule_unknown", id, "The cron schedule requires explicit manual mapping; no target job was created.");
        return new() { Id = id, Name = "cPanel cron " + number, Kind = kind, Command = command, Arguments = args, Url = url,
            WorkingDirectory = workingDirectory, ScheduleType = scheduleType, ScheduleValue = scheduleValue, Interval = interval,
            TimeOfDay = time, SourcePlatform = "linux", ScheduleUnknown = !scheduleKnown, SiteIds = siteIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), ArtifactPaths = artifacts.Distinct(StringComparer.Ordinal).ToList() };
    }
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";
    private static List<string> ShellWords(string value)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        char quote = '\0';
        var active = false;
        foreach (var c in value)
        {
            if (c is ';' or '|' or '&' or '<' or '>' or '`' or '$' or '\\' or '\n' or '\r' or '%') throw new FormatException();
            if (c is '\'' or '"')
            {
                if (quote == c) quote = '\0'; else if (quote == '\0') quote = c; else word.Append(c);
                active = true; continue;
            }
            if (char.IsWhiteSpace(c) && quote == '\0')
            {
                if (active) { words.Add(word.ToString()); word.Clear(); active = false; }
            }
            else { word.Append(c); active = true; }
        }
        if (quote != '\0') throw new FormatException();
        if (active) words.Add(word.ToString());
        return words;
    }
}
