using System.Security.AccessControl;
using System.Security.Principal;
using AdaPanel.Infrastructure.SiteBuilder;

namespace AdaPanel.Infrastructure.Migration.Packages;

/// <summary>Private migration artifacts only. Callers must supply owned paths and the configured IIS public roots.</summary>
public static class PortableMigrationStorage
{
    public static void Guard(string path) => SiteBuilderPathGuard.EnsureNoReparsePointOnPath(Path.GetPathRoot(Path.GetFullPath(path))!, Path.GetFullPath(path));

    public static void CreatePrivateDirectory(string path, IEnumerable<string>? publicWebRoots = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("migration.package.windows_required");
        path = Path.GetFullPath(path);
        var conventional = new[] { Path.Combine(AppContext.BaseDirectory, "wwwroot"), Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "inetpub", "wwwroot") };
        foreach (var publicRoot in (publicWebRoots ?? []).Concat(conventional))
        {
            var web = Path.GetFullPath(publicRoot).TrimEnd(Path.DirectorySeparatorChar);
            if (path.Equals(web, StringComparison.OrdinalIgnoreCase) || path.StartsWith(web + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("migration.package.public_root");
        }
        Guard(path);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var identity in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);
        new DirectoryInfo(path).SetAccessControl(security);
        Guard(path);
    }

    public static void DeleteOwnedDirectory(string path)
    {
        path = Path.GetFullPath(path);
        Guard(path);
        // No recursive shell/framework deletion and no following links.
        foreach (var child in Directory.EnumerateFileSystemEntries(path))
        {
            Guard(child);
            var attributes = File.GetAttributes(child);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("migration.package.reparse_point");
            if ((attributes & FileAttributes.Directory) != 0) DeleteOwnedDirectory(child);
            else File.Delete(child);
        }
        Directory.Delete(path);
    }
}
