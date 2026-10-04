using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace AdaPanel.Infrastructure.Ftp;

/// <summary>Owns the original file object while one FTP permission grant is pending.
/// Recovery edits only the attempted grant's added bits, using a fresh descriptor.</summary>
internal sealed class FtpPermissionChange : IDisposable
{
    private readonly SafeFileHandle handle;
    private readonly FileIdentity fileIdentity;
    private readonly SecurityIdentifier account;
    private readonly bool directory;
    private readonly InheritanceFlags inheritance;
    private readonly int addedMask;
    internal string Path { get; }

    private FtpPermissionChange(string path, SafeFileHandle handle, FileIdentity fileIdentity,
        SecurityIdentifier account, bool directory, int addedMask)
    {
        Path = path; this.handle = handle; this.fileIdentity = fileIdentity;
        this.account = account; this.directory = directory; this.addedMask = addedMask;
        inheritance = directory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
    }

    internal static FtpPermissionChange Capture(string path, SecurityIdentifier account, bool directory)
    {
        var handle = Open(path, writeAccess: true);
        try
        {
            CheckAttributes(handle, directory);
            var security = new HandleSecurity(directory, handle);
            var inheritance = directory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
            var previousMask = security.GetAccessRules(true, false, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>().Where(rule => rule.IdentityReference.Equals(account)
                    && rule.AccessControlType == AccessControlType.Allow && rule.InheritanceFlags == inheritance
                    && rule.PropagationFlags == PropagationFlags.None)
                .Aggregate(0, (mask, rule) => mask | (int)rule.FileSystemRights);
            // Use the same allow-mask normalization as Windows/.NET (including Synchronize).
            var requested = new FileSystemAccessRule(account, FileSystemRights.Modify, inheritance,
                PropagationFlags.None, AccessControlType.Allow);
            return new FtpPermissionChange(path, handle, ReadIdentity(handle), account, directory,
                (int)requested.FileSystemRights & ~previousMask);
        }
        catch { handle.Dispose(); throw; }
    }

    internal void ValidateIdentity()
    {
        using var current = Open(Path, writeAccess: false);
        CheckAttributes(current, directory);
        var measured = ReadIdentity(current);
        if (measured.Volume != fileIdentity.Volume || measured.Low != fileIdentity.Low || measured.High != fileIdentity.High)
            throw new IOException("The FTP permission target was replaced while the operation was pending.");
    }

    internal void Restore()
    {
        ValidateIdentity();
        if (addedMask == 0) return;
        var security = new HandleSecurity(directory, handle);
        var changed = false;
        foreach (var rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray())
        {
            if (!rule.IdentityReference.Equals(account) || rule.AccessControlType != AccessControlType.Allow
                || rule.InheritanceFlags != inheritance || rule.PropagationFlags != PropagationFlags.None) continue;
            var previous = (int)rule.FileSystemRights;
            var remaining = previous & ~addedMask;
            if (remaining == previous) continue;
            security.ModifyAccessRule(AccessControlModification.RemoveSpecific, rule, out _);
            if (remaining != 0)
                security.ModifyAccessRule(AccessControlModification.Add,
                    security.AccessRuleFactory(account, remaining, false, rule.InheritanceFlags,
                        rule.PropagationFlags, AccessControlType.Allow), out _);
            changed = true;
        }
        // Never persist an old whole-descriptor snapshot. The fresh descriptor retains
        // other identities, deny/inherited rules and unrelated concurrent current-SID bits.
        // Handle persistence cannot be redirected to a same-name replacement object.
        if (changed) security.Save(handle);
    }

    public void Dispose() => handle.Dispose();

    private static SafeFileHandle Open(string path, bool writeAccess)
    {
        const uint ReadControl = 0x00020000, WriteDac = 0x00040000, ReadAttributes = 0x80;
        const uint OpenExisting = 3, OpenReparsePoint = 0x00200000, BackupSemantics = 0x02000000;
        var handle = CreateFile(path, ReadAttributes | (writeAccess ? ReadControl | WriteDac : 0), 7,
            IntPtr.Zero, OpenExisting, OpenReparsePoint | BackupSemantics, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error(); handle.Dispose();
        throw new IOException("The FTP permission target could not be opened.", new Win32Exception(error));
    }

    private static void CheckAttributes(SafeFileHandle handle, bool directory)
    {
        if (!GetAttributes(handle, 9, out var info, (uint)Marshal.SizeOf<AttributeTagInfo>()))
            throw new IOException("The FTP permission target could not be measured.", new Win32Exception(Marshal.GetLastWin32Error()));
        if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0
            || ((info.Attributes & (uint)FileAttributes.Directory) != 0) != directory)
            throw new IOException("The FTP permission target is no longer an ordinary entry of the expected type.");
    }

    private static FileIdentity ReadIdentity(SafeFileHandle handle)
    {
        if (!GetIdentity(handle, 18, out var identity, (uint)Marshal.SizeOf<FileIdentity>()))
            throw new IOException("The FTP permission target identity could not be measured.", new Win32Exception(Marshal.GetLastWin32Error()));
        return identity;
    }

    private sealed class HandleSecurity(bool directory, SafeFileHandle handle)
        : NativeObjectSecurity(directory, ResourceType.FileObject, handle, AccessControlSections.Access)
    {
        public override Type AccessRightType => typeof(FileSystemRights);
        public override Type AccessRuleType => typeof(FileSystemAccessRule);
        public override Type AuditRuleType => typeof(FileSystemAuditRule);
        public override AccessRule AccessRuleFactory(IdentityReference identityReference, int accessMask,
            bool isInherited, InheritanceFlags inheritanceFlags, PropagationFlags propagationFlags, AccessControlType type) =>
            new FileSecurity().AccessRuleFactory(identityReference, accessMask, isInherited, inheritanceFlags, propagationFlags, type);
        public override AuditRule AuditRuleFactory(IdentityReference identityReference, int accessMask,
            bool isInherited, InheritanceFlags inheritanceFlags, PropagationFlags propagationFlags, AuditFlags flags) =>
            new FileSecurity().AuditRuleFactory(identityReference, accessMask, isInherited, inheritanceFlags, propagationFlags, flags);
        internal void Save(SafeFileHandle handle) => Persist(handle, AccessControlSections.Access);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdentity { public ulong Volume; public ulong Low; public ulong High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTagInfo { public uint Attributes; public uint ReparseTag; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIdentity(SafeFileHandle handle, int informationClass, out FileIdentity identity, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetAttributes(SafeFileHandle handle, int informationClass, out AttributeTagInfo info, uint size);
}
