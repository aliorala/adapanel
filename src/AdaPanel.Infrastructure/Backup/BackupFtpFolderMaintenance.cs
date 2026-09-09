using System.Globalization;
using System.Net;
using System.Text.Json;
using AdaPanel.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;

namespace AdaPanel.Infrastructure.Backup;

/// <summary>Local administrator maintenance for empty folders left by older panel versions.</summary>
public static class BackupFtpFolderMaintenance
{
    public sealed record Report(int Removed, int Unavailable, int Visited);

    public static async Task<Report> RunAsync(string dataRoot, CancellationToken cancellationToken)
    {
        using var state = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(dataRoot, "config", "automation.json"), cancellationToken));
        var root = state.RootElement;
        string Read(string name) => root.GetProperty(name).GetString() ?? string.Empty;
        var days = root.GetProperty("backupRetentionDays").GetInt32();
        var uri = new Uri(Read("backupRemoteUrl").TrimEnd('/') + "/");
        if (Read("backupTargetType") != "ftp" || uri.Scheme != "ftp" || days <= 0
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("An FTP directory target and positive retention are required.");

        var keyDirectory = new DirectoryInfo(Path.Combine(dataRoot, "keys"));
        if (!keyDirectory.Exists) throw new InvalidOperationException("Panel key directory is missing.");
        var provider = DataProtectionProvider.Create(keyDirectory, builder => builder.SetApplicationName("AdaPanel"));
        var password = new SecretProtector(provider).Unprotect(Read("backupRemotePasswordProtected"));
        return await SweepAsync(new FtpWebRequestCommands(), uri, new NetworkCredential(Read("backupRemoteUserName"), password),
            DateTime.Today.AddDays(-days), cancellationToken);
    }

    internal static async Task<Report> SweepAsync(IBackupFtpCommands ftp, Uri root, NetworkCredential credential,
        DateTime cutoff, CancellationToken cancellationToken)
    {
        var removed = 0;
        var unavailable = 0;
        var visited = 0;
        var listing = await ftp.ListDirectoryAsync(root, credential, cancellationToken);
        if (!listing.Success) throw new InvalidOperationException($"FTP root listing failed: {listing.Code}");
        foreach (var name in Names(listing.Names, root))
        {
            if (!DateTime.TryParseExact(name, BackupLayout.DayFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var day) || day >= cutoff.Date) continue;
            var dayRoot = new Uri(root, Uri.EscapeDataString(name) + "/");
            await VisitAsync(dayRoot, dayRoot);
        }
        return new Report(removed, unavailable, visited);

        async Task VisitAsync(Uri directory, Uri dayRoot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++visited > 2000) throw new InvalidOperationException("FTP folder scan limit reached.");
            var children = await ftp.ListDirectoryAsync(directory, credential, cancellationToken);
            if (!children.Success && children.Code != FtpCommandResult.FileUnavailable)
            {
                unavailable++;
                return;
            }
            if (children.Success)
            {
                foreach (var name in Names(children.Names, directory))
                {
                    var child = new Uri(directory, Uri.EscapeDataString(name) + "/");
                    // Only generated category/source levels, never arbitrary recursive paths.
                    var candidates = BackupFtpCleanup.DirectoriesFor(new Uri(child, "maintenance-placeholder.zip"));
                    if (candidates.Count > 0 && candidates[0] == child && candidates[^1] == dayRoot)
                        await VisitAsync(child, dayRoot);
                }
                children = await ftp.ListDirectoryAsync(directory, credential, cancellationToken);
                if (children.Success && Names(children.Names, directory).Any()) return;
                if (!children.Success && children.Code != FtpCommandResult.FileUnavailable)
                {
                    unavailable++;
                    return;
                }
            }
            // RMD cannot delete files or a nonempty directory, even if NLST returned 550.
            var result = await ftp.RemoveDirectoryAsync(directory, credential, cancellationToken);
            if (result.Success) removed++;
            else unavailable++;
        }
    }

    private static IEnumerable<string> Names(IEnumerable<string> entries, Uri parent)
    {
        var prefix = Uri.UnescapeDataString(parent.AbsolutePath);
        foreach (var entry in entries)
        {
            var name = entry.TrimEnd('/');
            if (name.StartsWith(prefix, StringComparison.Ordinal)) name = name[prefix.Length..];
            if (name.Length > 0 && name is not "." and not ".." && !name.Contains('/') && !name.Contains('\\')) yield return name;
        }
    }
}
