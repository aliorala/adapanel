using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Infrastructure.Php;
using System.Security.AccessControl;
using System.Security.Principal;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class PhpSiteLogConfigurationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-php-log-" + Guid.NewGuid().ToString("N"));
    private string Ini => Path.Combine(root, ".user.ini");
    public PhpSiteLogConfigurationTests() => Directory.CreateDirectory(root);
    private SiteDto Site(string? appRoot = null) => new() { Name = "fixture", PhysicalPath = root, ApplicationRootPath = appRoot ?? root, ApplicationPoolName = "fixture" };
    private static PhpSiteLogConfiguration Config(string? aclError = null, string? httpError = null) => new(_ => "fixture-sid", (_, _, _, _) => aclError, (_, _, _, _) => Task.FromResult(httpError));

    [Fact]
    public async Task Default_is_site_local_and_publication_is_idempotent()
    {
        await File.WriteAllLinesAsync(Ini, ["; retained", "session.name = CUSTOM", "display_errors = On ; keep comment", "display_errors = On"]);
        var first = await Config().PrepareAsync(Site());
        Assert.Null(first.ConfigurationError); Assert.True(first.Managed);
        Assert.Equal(Path.Combine(root, ".adapanel-logs", "php-errors.log"), first.LogPath);
        var content = await File.ReadAllTextAsync(Ini);
        Assert.Contains("; retained", content); Assert.Contains("session.name = CUSTOM", content);
        Assert.Equal("Off", PhpSiteLogConfiguration.ReadDirective(await File.ReadAllLinesAsync(Ini), "display_errors"));
        Assert.Single(await File.ReadAllLinesAsync(Ini), l => l.StartsWith("display_errors"));
        Assert.Contains("; keep comment", content);
        var stamp = File.GetLastWriteTimeUtc(Ini);
        await Config().PrepareAsync(Site());
        Assert.Equal(content, await File.ReadAllTextAsync(Ini));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Ini));
    }

    [Fact]
    public async Task Absolute_custom_log_and_inline_comments_survive_unchanged()
    {
        var customLine = $"error_log = \"{root.Replace('\\', '/')}/lib/gunluk/errors.log\" ; application managed";
        await File.WriteAllLinesAsync(Ini, ["; Travian", customLine, "session.name = TRAVIAN"]);
        var result = await Config().PrepareAsync(Site());
        Assert.Null(result.ConfigurationError); Assert.False(result.Managed);
        Assert.Contains(customLine, await File.ReadAllLinesAsync(Ini));
    }

    [Theory]
    [InlineData("syslog")]
    [InlineData("lib/gunluk/errors.log")]
    [InlineData("C:/other-tenant/php-errors.log")]
    public async Task Unsupported_destination_reports_error_without_rewriting(string value)
    {
        var original = $"; custom\nerror_log = \"{value}\"\nsession.name = KEEP\n";
        await File.WriteAllTextAsync(Ini, original);
        Assert.NotNull((await Config().PrepareAsync(Site())).ConfigurationError);
        Assert.Equal(original, await File.ReadAllTextAsync(Ini));
    }

    [Fact]
    public async Task Application_root_must_include_document_root()
    {
        var other = Path.Combine(root, "other");
        Directory.CreateDirectory(other);
        Assert.NotNull((await Config().PrepareAsync(Site(other))).ConfigurationError);
        Assert.False(File.Exists(Ini));
    }

    [Theory]
    [InlineData("permission denied", null)]
    [InlineData(null, "HTTP protection missing")]
    public async Task Permission_or_http_failure_never_publishes_success(string? acl, string? http)
    {
        await File.WriteAllTextAsync(Ini, "; unchanged");
        var result = await Config(acl, http).PrepareAsync(Site());
        Assert.Equal(acl ?? http, result.ConfigurationError);
        Assert.Equal("; unchanged", await File.ReadAllTextAsync(Ini));
    }

    [Fact]
    public async Task Inspection_is_read_only_and_reports_unconfigured_logging()
    {
        var result = await Config().InspectAsync(Site());
        Assert.NotNull(result.ConfigurationError); Assert.False(File.Exists(Ini));
        Assert.False(Directory.Exists(Path.Combine(root, ".adapanel-logs")));
    }

    [Fact]
    public async Task Pool_identity_failure_is_visible_without_ini_changes()
    {
        var config = new PhpSiteLogConfiguration(_ => throw new IOException("shared pool"), (_, _, _, _) => null, (_, _, _, _) => Task.FromResult<string?>(null));
        Assert.Contains("shared pool", (await config.PrepareAsync(Site())).ConfigurationError);
        Assert.False(File.Exists(Ini));
    }

    [Fact]
    public void Broad_custom_directory_acl_is_rejected_without_permission_changes()
    {
        var custom = Path.Combine(root, "custom");
        Directory.CreateDirectory(custom);
        var acl = new DirectoryInfo(custom).GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(custom).SetAccessControl(acl);
        var before = new DirectoryInfo(custom).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        var error = PhpSiteLogConfiguration.SecureAccess(Path.Combine(custom, "custom.log"), WindowsIdentity.GetCurrent().User!.Value, false, true);
        Assert.NotNull(error);
        Assert.Equal(before, new DirectoryInfo(custom).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        Assert.False(File.Exists(Path.Combine(custom, "custom.log")));
    }

    [Fact]
    public async Task Hardlinked_ini_is_rejected_without_editing_either_name()
    {
        var original = Path.Combine(root, "original.ini");
        await File.WriteAllTextAsync(original, "; original");
        Assert.True(CreateHardLink(Ini, original, IntPtr.Zero));
        Assert.NotNull((await Config().PrepareAsync(Site())).ConfigurationError);
        Assert.Equal("; original", await File.ReadAllTextAsync(original));
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newName, string existingName, IntPtr security);
    public void Dispose() => Directory.Delete(root, true);
}
