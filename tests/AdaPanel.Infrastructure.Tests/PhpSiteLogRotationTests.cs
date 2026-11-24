using AdaPanel.Infrastructure.Php;
using System.Security.AccessControl;
using System.Security.Principal;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class PhpSiteLogRotationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-php-roll-" + Guid.NewGuid().ToString("N"));
    public PhpSiteLogRotationTests() => Directory.CreateDirectory(root);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Custom_or_unmarked_existing_log_is_never_moved(bool managed)
    {
        var path = Path.Combine(root, PhpSiteLogConfiguration.FileName);
        await File.WriteAllTextAsync(path, "existing application diagnostic content");
        var plan = new PhpSiteLogPlan(path, root, managed, null, RollAtBytes: 1);
        Assert.Null(await PhpSiteLogRotation.ArchiveIfNeededAsync(plan));
        Assert.Equal("existing application diagnostic content", await File.ReadAllTextAsync(path));
        Assert.Single(Directory.GetFiles(root));
    }

    [Fact]
    public async Task Forged_management_marker_does_not_authorize_archiving()
    {
        var path = Path.Combine(root, PhpSiteLogConfiguration.FileName);
        await File.WriteAllTextAsync(path, "retained");
        await File.WriteAllTextAsync(Path.Combine(root, PhpSiteLogConfiguration.ManagedMarker), "");
        var plan = new PhpSiteLogPlan(path, root, true, null, RollAtBytes: 1);
        await Assert.ThrowsAsync<IOException>(() => PhpSiteLogRotation.ArchiveIfNeededAsync(plan));
        Assert.Equal("retained", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Trusted_marker_after_recursive_acl_grant_is_repaired_before_archiving()
    {
        var directory = Path.Combine(root, PhpSiteLogConfiguration.DirectoryName);
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, PhpSiteLogConfiguration.ManagedMarker);
        await File.WriteAllBytesAsync(marker, []);
        var acl = new FileSecurity();
        acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        // Simulate a recursive site repair granting another pool access to the empty marker.
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-82-1-2-3-4-5"), FileSystemRights.Modify, AccessControlType.Allow));
        new FileInfo(marker).SetAccessControl(acl);
        var path = Path.Combine(directory, PhpSiteLogConfiguration.FileName);
        await File.WriteAllTextAsync(path, "archive me without deleting");
        var plan = new PhpSiteLogPlan(path, directory, true, null, RollAtBytes: 1);
        await Assert.ThrowsAsync<IOException>(() => PhpSiteLogRotation.ArchiveIfNeededAsync(plan));
        Assert.True(PhpSiteLogConfiguration.RestoreManagedMarkerAfterAclRepair(root));
        var archive = await PhpSiteLogRotation.ArchiveIfNeededAsync(plan);
        Assert.NotNull(archive); Assert.Equal("archive me without deleting", await File.ReadAllTextAsync(archive));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Foreign_owned_or_nonempty_marker_is_not_adopted()
    {
        var directory = Path.Combine(root, PhpSiteLogConfiguration.DirectoryName);
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, PhpSiteLogConfiguration.ManagedMarker);
        await File.WriteAllTextAsync(marker, "user data");
        var before = new FileInfo(marker).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        Assert.False(PhpSiteLogConfiguration.RestoreManagedMarkerAfterAclRepair(root));
        Assert.Equal(before, new FileInfo(marker).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        Assert.Equal("user data", await File.ReadAllTextAsync(marker));
    }

    public void Dispose() => Directory.Delete(root, true);
}
