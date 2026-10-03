using System.Xml.Linq;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using AdaPanel.Infrastructure.Support;
using Microsoft.Win32.SafeHandles;

namespace AdaPanel.Infrastructure.Files;

internal static class WebConfigurationFile
{
    internal static void Save(string path, XDocument document, Func<Stream, Stream>? configureStream = null,
        CancellationToken cancellationToken = default, Action<string>? inheritNewFileAccess = null) =>
        Write(path, stream => document.Save(stream), configureStream, cancellationToken, inheritNewFileAccess);

    internal static void WriteBytes(string path, byte[] content, Func<Stream, Stream>? configureStream = null) =>
        Write(path, stream => stream.Write(content), configureStream, CancellationToken.None, null);

    private static void Write(string path, Action<Stream> write, Func<Stream, Stream>? configureStream,
        CancellationToken cancellationToken, Action<string>? inheritNewFileAccess)
    {
        path = Path.GetFullPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".adapanel-config-{Guid.NewGuid():N}.tmp");
        var createdTarget = false;
        FileFingerprint? writtenFile = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNoReparsePoints(path);
            // The protected DACL is installed at creation. A leftover after a process
            // crash must not expose environment variables through a site's web root.
            using (var file = SecureTempFile.Create(temporary))
            using (var output = configureStream?.Invoke(file) ?? file)
            {
                write(output);
                output.Flush();
                file.Flush(flushToDisk: true);
                writtenFile = Measure(file);
            }
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNoReparsePoints(path);
            EnsureNoReparsePoints(temporary);
            if (File.Exists(path))
            {
                // ReplaceFile preserves the existing configuration's Windows ACL.
                File.Replace(temporary, path, null);
            }
            else
            {
                // Do not grant web access to the temporary filename. Publish complete
                // XML first, then let only this newly created target inherit site access.
                File.Move(temporary, path);
                createdTarget = true;
                // The seam runs before acquiring the final file. Another writer may
                // have replaced or edited it while publication was finishing.
                inheritNewFileAccess?.Invoke(path);
                InheritNewFileAccess(path, writtenFile.Value);
            }
        }
        catch
        {
            if (createdTarget && writtenFile is { } expected) TryDeleteOwned(path, expected);
            throw;
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static void InheritNewFileAccess(string path, FileFingerprint expected)
    {
        using var file = OpenOwnedFile(path, expected);
        var security = file.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
        // FileStream applies the ACL to its handle. The handle denies write/delete
        // sharing, so revalidation and access publication refer to the same file.
        file.SetAccessControl(security);
    }

    private static FileStream OpenOwnedFile(string path, FileFingerprint expected)
    {
        EnsureNoReparsePoints(path);
        var file = new FileInfo(path).Create(FileMode.Open,
            FileSystemRights.ReadData | FileSystemRights.ReadPermissions | FileSystemRights.ChangePermissions |
            FileSystemRights.Delete | FileSystemRights.Synchronize,
            FileShare.Read, 4096, FileOptions.None, fileSecurity: null);
        try
        {
            if (Measure(file) != expected)
                throw new IOException("The published web.config was changed by another writer.");
            return file;
        }
        catch { file.Dispose(); throw; }
    }

    private static void TryDeleteOwned(string path, FileFingerprint expected)
    {
        try
        {
            using var file = OpenOwnedFile(path, expected);
            // Mark the measured, locked file for deletion by handle. A path-based
            // File.Delete after closing it would introduce a second replacement race.
            var disposition = new FileDisposition { DeleteFile = true };
            if (!SetFileInformationByHandle(file.SafeFileHandle, 4, ref disposition, (uint)Marshal.SizeOf<FileDisposition>()))
                throw NativeFailure("Unable to remove the unpublished configuration.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { /* Keep foreign/unmeasured files and preserve the original publication error. */ }
    }

    private static FileFingerprint Measure(FileStream file)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Web configuration publication requires Windows file identity.");
        if (!GetFileInformationByHandleEx(file.SafeFileHandle, 18, out var identity, (uint)Marshal.SizeOf<FileIdentity>()))
            throw NativeFailure("Unable to measure configuration file identity.");
        if (identity.Low == 0 && identity.High == 0)
            throw new IOException("Configuration file identity is unavailable.");
        file.Position = 0;
        var hash = Convert.ToHexString(SHA256.HashData(file));
        return new FileFingerprint(identity.Volume, identity.Low, identity.High, hash);
    }

    private static IOException NativeFailure(string message) => new(message, new Win32Exception(Marshal.GetLastWin32Error()));
    private readonly record struct FileFingerprint(ulong Volume, ulong Low, ulong High, string Hash);
    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdentity { public ulong Volume; public ulong Low; public ulong High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileDisposition { [MarshalAs(UnmanagedType.U1)] public bool DeleteFile; }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass,
        out FileIdentity information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass,
        ref FileDisposition information, uint size);

    private static void TryDelete(string path)
    {
        try { EnsureNoReparsePoints(path); File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { /* Cleanup must not hide the original write/publication failure. */ }
    }

    private static void EnsureNoReparsePoints(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("A linked path cannot publish web.config.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
