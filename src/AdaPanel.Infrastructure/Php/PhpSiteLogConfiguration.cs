using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Infrastructure.AspNet;
using Microsoft.Win32.SafeHandles;
using static AdaPanel.Infrastructure.AspNet.IisConfigurationAccess;

namespace AdaPanel.Infrastructure.Php;

public sealed record PhpSiteLogPlan(string? LogPath, string? DirectoryPath, bool Managed,
    string? ConfigurationError, string? Warning = null, long RollAtBytes = 50L * 1024 * 1024);

/// <summary>Site-local logging; never modifies a runtime's shared php.ini.</summary>
public sealed class PhpSiteLogConfiguration
{
    public const string DirectoryName = ".adapanel-logs";
    public const string FileName = "php-errors.log";
    internal const string ManagedMarker = ".adapanel-managed-php-log";
    public const string GrowthPolicy = "50 MiB üzerinde yönetilen günlük arşivlenir; arşivler otomatik silinmez. Özel günlükler uygulama tarafından yönetilir.";
    private readonly Func<SiteDto, string> poolIdentity;
    private readonly Func<string, string, bool, bool, string?> access;
    private readonly Func<SiteDto, string, bool, CancellationToken, Task<string?>> httpProtection;

    public PhpSiteLogConfiguration(Func<SiteDto, string, bool, CancellationToken, Task<string?>>? httpProtection = null)
        : this(ResolvePoolIdentity, SecureAccess, httpProtection ?? ((_, _, _, _) => Task.FromResult<string?>("PHP günlük HTTP koruması doğrulanamadı."))) { }

    internal PhpSiteLogConfiguration(Func<SiteDto, string> poolIdentity,
        Func<string, string, bool, bool, string?> access,
        Func<SiteDto, string, bool, CancellationToken, Task<string?>> httpProtection)
    { this.poolIdentity = poolIdentity; this.access = access; this.httpProtection = httpProtection; }

    public Task<PhpSiteLogPlan> PrepareAsync(SiteDto site, CancellationToken cancellationToken = default) => RunAsync(site, true, cancellationToken);
    public Task<PhpSiteLogPlan> InspectAsync(SiteDto site, CancellationToken cancellationToken = default) => RunAsync(site, false, cancellationToken);

    private async Task<PhpSiteLogPlan> RunAsync(SiteDto site, bool apply, CancellationToken token)
    {
        PhpSiteLogPlan plan = new(null, null, false, null, GrowthPolicy);
        try
        {
            token.ThrowIfCancellationRequested();
            var physical = NormalizePath(site.PhysicalPath);
            var root = NormalizePath(string.IsNullOrWhiteSpace(site.ApplicationRootPath) ? physical : site.ApplicationRootPath);
            if (!Same(root, physical) && !PathContainment.IsInsideRoot(root, physical))
                throw new IOException("Uygulama kökü belge kökünü kapsamıyor.");
            if (!Directory.Exists(root) || !Directory.Exists(physical)) throw new IOException("Site kökü mevcut değil.");
            var ini = Path.Combine(physical, ".user.ini");
            EnsureSafePath(ini);
            using var lease = await PhpIniFile.EnterAsync(ini, token);
            EnsureSafePath(ini);
            var lines = File.Exists(ini) ? (await File.ReadAllLinesAsync(ini, token)).ToList() : [];
            var originalLines = lines.ToArray();
            var custom = ReadDirective(lines, "error_log");
            var managedPath = Path.Combine(root, DirectoryName, FileName);
            var destination = custom is null ? managedPath : NormalizePath(custom);
            if (!PathContainment.IsInsideRoot(root, destination)) throw new IOException("Özel PHP günlüğü izolasyon kökünün dışında; ayar korunmuştur.");
            var directory = Path.GetDirectoryName(destination)!;
            var managed = Same(destination, managedPath);
            plan = new(destination, directory, managed, null, GrowthPolicy);
            EnsureSafePath(destination);
            var sid = poolIdentity(site);
            var error = access(destination, sid, managed, apply);
            if (error is not null) return plan with { ConfigurationError = error };
            if (managed && !File.Exists(Path.Combine(directory, ManagedMarker)))
                plan = plan with { Warning = "Mevcut günlük klasörünün yönetim işareti yok; içindeki günlük otomatik arşivlenmez. " + GrowthPolicy };
            EnsureSafePath(destination);
            error = await httpProtection(site, destination, apply, token);
            if (error is not null) return plan with { ConfigurationError = error };
            if (apply)
            {
                if (custom is null) lines.Add($"error_log = \"{destination.Replace('\\', '/')}\"");
                SetDirective(lines, "log_errors", "On");
                SetDirective(lines, "display_errors", "Off");
                EnsureSafePath(ini);
                EnsureSafePath(destination);
                if (!originalLines.SequenceEqual(lines)) await PhpIniFile.PublishLinesAsync(ini, lines, token);
            }
            else if (custom is null || !IsOn(ReadDirective(lines, "log_errors")) || !string.Equals(ReadDirective(lines, "display_errors"), "Off", StringComparison.OrdinalIgnoreCase))
                return plan with { ConfigurationError = "Site PHP günlük yönergeleri henüz uygulanmadı." };
            return plan;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or SystemException or AppException)
        { return plan with { ConfigurationError = error.Message }; }
    }

    private static bool IsOn(string? value) => value is not null && (value.Equals("On", StringComparison.OrdinalIgnoreCase) || value is "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));
    private static bool Same(string a, string b) => string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    private static string NormalizePath(string value)
    {
        if (!Path.IsPathFullyQualified(value) || value.StartsWith("\\\\", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal) ||
            value.Any(c => char.IsControl(c) || c is '"' or ';' or '[' or ']') || value.IndexOf(':', 2) >= 0)
            throw new IOException("PHP günlük yolu yerel, mutlak ve geçerli bir dosya yolu olmalıdır; göreli yol/syslog desteklenmiyor.");
        return Path.GetFullPath(value);
    }

    internal static string? ReadDirective(IEnumerable<string> lines, string name)
    {
        string? result = null;
        foreach (var line in lines)
        {
            var text = line.TrimStart();
            if (text.StartsWith(';') || text.StartsWith('#')) continue;
            var equals = text.IndexOf('=');
            if (equals < 0 || !text[..equals].Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            var value = text[(equals + 1)..].Trim();
            if (value.StartsWith('"') || value.StartsWith('\''))
            {
                var end = value.IndexOf(value[0], 1);
                if (end < 0) throw new IOException("PHP günlük yönergesinin tırnağı kapanmamış.");
                result = value[1..end];
            }
            else result = value.Split(';', 2)[0].Trim();
        }
        return result;
    }

    private static void SetDirective(List<string> lines, string name, string value)
    {
        var first = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var text = lines[i].TrimStart();
            var equals = text.IndexOf('=');
            if (equals < 0 || !text[..equals].Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (first >= 0) { lines.RemoveAt(i--); continue; }
            first = i;
            var comment = text.IndexOf(';', equals + 1);
            lines[i] = $"{name} = {value}" + (comment >= 0 ? " " + text[comment..] : "");
        }
        if (first < 0) lines.Add($"{name} = {value}");
    }

    /// <summary>Reject linked ancestors and files before touching any site-controlled path.</summary>
    public static void EnsureSafePath(string path)
    {
        path = NormalizePath(path);
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("PHP günlük yolunda bağlantı/junction kullanılamaz.");
            if ((attributes & FileAttributes.Directory) == 0)
            {
                using var file = new FileStream(current, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (!GetFileInformationByHandle(file.SafeFileHandle, out var info)) throw new IOException("Dosya bağlantı sayısı okunamadı.", new Win32Exception(Marshal.GetLastWin32Error()));
                if (info.NumberOfLinks != 1) throw new IOException("PHP günlük yolunda hardlink kullanılamaz.");
            }
        }
    }

    /// <summary>Repairs only a trusted, empty management marker after the site's recursive ACL repair.</summary>
    public static bool RestoreManagedMarkerAfterAclRepair(string applicationRoot)
    {
        try
        {
            var root = NormalizePath(applicationRoot);
            var marker = Path.Combine(root, DirectoryName, ManagedMarker);
            EnsureSafePath(marker);
            if (!File.Exists(marker)) return false;
            using var file = new FileInfo(marker).Create(FileMode.Open,
                FileSystemRights.ReadData | FileSystemRights.ReadPermissions | FileSystemRights.ChangePermissions | FileSystemRights.Synchronize,
                FileShare.None, 1, FileOptions.None, fileSecurity: null);
            if (!GetFileInformationByHandle(file.SafeFileHandle, out var identity) || identity.NumberOfLinks != 1 || file.Length != 0) return false;
            var owner = file.GetAccessControl().GetOwner(typeof(SecurityIdentifier))?.Value;
            if (owner is not ("S-1-5-18" or "S-1-5-32-544")) return false;
            var acl = new FileSecurity();
            acl.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544" })
                acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl, AccessControlType.Allow));
            file.SetAccessControl(acl);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SystemException) { return false; }
    }

    private static string ResolvePoolIdentity(SiteDto site)
    {
        if (string.IsNullOrWhiteSpace(site.ApplicationPoolName)) throw new IOException("Uygulama havuzu kimliği bilinmiyor.");
        using var manager = CreateServerManager();
        var pool = Enumerate(GetPropertyObject(manager, "ApplicationPools")).SingleOrDefault(p => GetStringProperty(p, "Name") == site.ApplicationPoolName)
            ?? throw new IOException("Gerçek IIS uygulama havuzu bulunamadı.");
        if (GetStringProperty(GetPropertyObject(pool, "ProcessModel"), "IdentityType") != "ApplicationPoolIdentity")
            throw new IOException("Paylaşılan servis kimliği günlük izolasyonunu doğrulayamaz.");
        var ownerFound = false;
        foreach (var other in Enumerate(GetPropertyObject(manager, "Sites")))
        foreach (var app in Enumerate(GetPropertyObject(other, "Applications")))
        {
            var usesPool = GetStringProperty(app, "ApplicationPoolName") == site.ApplicationPoolName;
            var sameSite = GetStringProperty(other, "Name") == site.Name;
            if (usesPool && !sameSite) throw new IOException("Uygulama havuzu başka siteyle ortak; günlük izole değil.");
            if (sameSite && !usesPool) throw new IOException("Site birden fazla havuz kullanıyor; günlük kimliği belirsiz.");
            ownerFound |= usesPool && sameSite;
        }
        if (!ownerFound) throw new IOException("Site-havuz ilişkisi doğrulanamadı.");
        return ((SecurityIdentifier)new NTAccount("IIS AppPool", site.ApplicationPoolName).Translate(typeof(SecurityIdentifier))).Value;
    }

    internal static string? SecureAccess(string path, string poolSid, bool managed, bool apply)
    {
        var pool = new SecurityIdentifier(poolSid);
        var directory = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory))
        {
            if (!apply || !managed) return "PHP günlük klasörü mevcut değil.";
            EnsureSafePath(directory);
            var acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(true, false);
            foreach (var (sid, rights) in Principals(pool)) acl.AddAccessRule(new FileSystemAccessRule(sid, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(directory).Create(acl);
            // Marker authorizes non-destructive rotation only for folders created by us.
            var markerAcl = new FileSecurity();
            markerAcl.SetAccessRuleProtection(true, false);
            markerAcl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            foreach (var (sid, rights) in Principals(pool).Where(p => !p.Sid.Equals(pool)))
                markerAcl.AddAccessRule(new FileSystemAccessRule(sid, rights, AccessControlType.Allow));
            using var marker = new FileInfo(Path.Combine(directory, ManagedMarker)).Create(FileMode.CreateNew,
                FileSystemRights.ReadData | FileSystemRights.WriteData | FileSystemRights.Synchronize,
                FileShare.Read, 1, FileOptions.None, markerAcl);
        }
        EnsureSafePath(path);
        var directoryAcl = new DirectoryInfo(directory).GetAccessControl();
        var problem = VerifyAcl(directoryAcl, pool, requireModify: false);
        if (problem is not null) return problem;
        if (apply && VerifyAcl(directoryAcl, pool) is not null)
        {
            directoryAcl.AddAccessRule(new FileSystemAccessRule(pool, FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(directoryAcl);
        }
        problem = VerifyAcl(new DirectoryInfo(directory).GetAccessControl(), pool);
        if (problem is not null) return problem;
        if (!File.Exists(path))
        {
            if (!apply) return "PHP günlük dosyası henüz oluşmamış.";
            var fileAcl = new FileSecurity();
            fileAcl.SetAccessRuleProtection(true, false);
            foreach (var (sid, rights) in Principals(pool)) fileAcl.AddAccessRule(new FileSystemAccessRule(sid, rights, AccessControlType.Allow));
            using var file = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.ReadData | FileSystemRights.WriteData | FileSystemRights.ReadPermissions | FileSystemRights.Synchronize, FileShare.Read, 4096, FileOptions.None, fileAcl);
        }
        var existingAcl = new FileInfo(path).GetAccessControl();
        problem = VerifyAcl(existingAcl, pool, requireModify: false);
        if (problem is not null) return problem;
        if (apply && VerifyAcl(existingAcl, pool) is not null)
        {
            existingAcl.AddAccessRule(new FileSystemAccessRule(pool, FileSystemRights.Modify, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(existingAcl);
        }
        return VerifyAcl(new FileInfo(path).GetAccessControl(), pool);
    }

    private static IEnumerable<(SecurityIdentifier Sid, FileSystemRights Rights)> Principals(SecurityIdentifier pool)
    {
        yield return (new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl);
        yield return (new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl);
        yield return (pool, FileSystemRights.Modify);
    }
    private static string? VerifyAcl(FileSystemSecurity acl, SecurityIdentifier pool, bool requireModify = true)
    {
        var allowed = Principals(pool).Select(p => p.Sid.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var owner = acl.GetOwner(typeof(SecurityIdentifier))?.Value;
        if (owner is null || !allowed.Contains(owner)) return "PHP günlük nesnesinin sahibi başka kimlik; izolasyon doğrulanamadı.";
        var rights = (FileSystemRights)0;
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow && !allowed.Contains(rule.IdentityReference.Value)) return "PHP günlük DACL'i başka kimliklere açık; mevcut izinler değiştirilmedi.";
            if ((rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
            if (rule.IdentityReference.Equals(pool))
            {
                if (rule.AccessControlType == AccessControlType.Deny && (rule.FileSystemRights & FileSystemRights.Modify) != 0) return "Havuzun PHP günlüğü yazma hakkı reddedilmiş.";
                if (rule.AccessControlType == AccessControlType.Allow) rights |= rule.FileSystemRights;
            }
        }
        return !requireModify || (rights & FileSystemRights.Modify) == FileSystemRights.Modify ? null : "Havuzun PHP günlüğü Modify hakkı doğrulanamadı.";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation info);
}
