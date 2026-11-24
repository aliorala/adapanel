using System.Security.AccessControl;
using System.Security.Principal;

namespace AdaPanel.Infrastructure.Php;

/// <summary>Archives only explicitly marked managed logs; no retention deletion.</summary>
public static class PhpSiteLogRotation
{
    internal static bool HasTrustedMarker(PhpSiteLogPlan plan)
    {
        if (!plan.Managed || plan.DirectoryPath is null) return false;
        try
        {
            var marker = Path.Combine(plan.DirectoryPath, PhpSiteLogConfiguration.ManagedMarker);
            PhpSiteLogConfiguration.EnsureSafePath(marker);
            if (!File.Exists(marker) || new FileInfo(marker).Length != 0) return false;
            var acl = new FileInfo(marker).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            var trusted = new[] { "S-1-5-18", "S-1-5-32-544" };
            return acl.AreAccessRulesProtected && trusted.Contains(acl.GetOwner(typeof(SecurityIdentifier))?.Value) &&
                acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                    .All(rule => rule.AccessControlType != AccessControlType.Allow || trusted.Contains(rule.IdentityReference.Value));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SystemException) { return false; }
    }

    public static async Task<string?> ArchiveIfNeededAsync(PhpSiteLogPlan plan, CancellationToken cancellationToken = default)
    {
        if (!plan.Managed || plan.ConfigurationError is not null || plan.LogPath is null || plan.DirectoryPath is null) return null;
        var path = Path.GetFullPath(plan.LogPath);
        if (!string.Equals(Path.GetFileName(path), PhpSiteLogConfiguration.FileName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(path), Path.GetFullPath(plan.DirectoryPath), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Yönetilen PHP günlük yolu doğrulanamadı.");
        var marker = Path.Combine(plan.DirectoryPath, PhpSiteLogConfiguration.ManagedMarker);
        PhpSiteLogConfiguration.EnsureSafePath(path);
        PhpSiteLogConfiguration.EnsureSafePath(marker);
        if (!File.Exists(marker) || !File.Exists(path)) return null;
        var markerAcl = new FileInfo(marker).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        var trusted = new[] { "S-1-5-18", "S-1-5-32-544" };
        if (!markerAcl.AreAccessRulesProtected || !trusted.Contains(markerAcl.GetOwner(typeof(SecurityIdentifier))?.Value))
            throw new IOException("PHP günlük yönetim işareti güvenilir değil.");
        foreach (FileSystemAccessRule rule in markerAcl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && !trusted.Contains(rule.IdentityReference.Value))
                throw new IOException("PHP günlük yönetim işaretinin izinleri güvenilir değil.");
        using var lease = await PhpIniFile.EnterAsync(path, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        PhpSiteLogConfiguration.EnsureSafePath(path);
        // PHP opens/closes its error log per write. Refuse rotation while held by another writer.
        // Keep this exclusive handle through the rename, sharing delete only for our Move.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Delete);
        if (file.Length < plan.RollAtBytes) return null;
        var archive = Path.Combine(plan.DirectoryPath, $"php-errors.{DateTime.UtcNow:yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid():N}.log");
        PhpSiteLogConfiguration.EnsureSafePath(archive);
        File.Move(path, archive, overwrite: false);
        return archive;
    }
}
