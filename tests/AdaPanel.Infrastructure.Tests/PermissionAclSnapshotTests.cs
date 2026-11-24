using System.Security.AccessControl;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

// Windows may reorder adjacent explicit allow ACEs when an ACL is persisted.
// Those grants commute; neither masks nor multiplicity may be discarded.
internal static class PermissionAclSnapshot
{
    internal static string Read(FileSystemSecurity security) =>
        Normalize(security.GetSecurityDescriptorSddlForm(AccessControlSections.Access));

    internal static string Normalize(string sddl)
    {
        var descriptor = new RawSecurityDescriptor(sddl);
        if (descriptor.DiscretionaryAcl is not { } acl) return sddl;
        for (var start = 0; start < acl.Count;)
        {
            if (!CommutativeAllow(acl[start])) { start++; continue; }
            var end = start + 1;
            while (end < acl.Count && CommutativeAllow(acl[end])) end++;
            var sorted = Enumerable.Range(start, end - start)
                .Select(index => acl[index])
                .OrderBy(ace =>
                {
                    var bytes = new byte[ace.BinaryLength];
                    ace.GetBinaryForm(bytes, 0);
                    return Convert.ToHexString(bytes);
                }, StringComparer.Ordinal).ToArray();
            for (var index = start; index < end; index++) acl[index] = sorted[index - start];
            start = end;
        }
        return descriptor.GetSddlForm(AccessControlSections.Access);
    }

    private static bool CommutativeAllow(GenericAce ace) => ace is CommonAce
    { AceQualifier: AceQualifier.AccessAllowed, IsInherited: false, IsCallback: false };
}

public sealed class PermissionAclSnapshotTests
{
    private const string Original = "D:P(A;OICIIO;GA;;;CO)(A;;FA;;;SY)";

    [Fact]
    public void Only_order_of_adjacent_explicit_allows_is_ignored() =>
        Assert.Equal(PermissionAclSnapshot.Normalize(Original),
            PermissionAclSnapshot.Normalize("D:P(A;;FA;;;SY)(A;OICIIO;GA;;;CO)"));

    [Theory]
    [InlineData("D:P(A;OICIIO;GR;;;CO)(A;;FA;;;SY)")]
    [InlineData("D:P(A;OICIIO;GA;;;BA)(A;;FA;;;SY)")]
    [InlineData("D:P(A;OICI;GA;;;CO)(A;;FA;;;SY)")]
    [InlineData("D:P(A;OICIIO;GA;;;CO)(A;;FA;;;SY)(A;;FA;;;SY)")]
    [InlineData("D:(A;OICIIO;GA;;;CO)(A;;FA;;;SY)")]
    public void Rights_identity_flags_duplicates_and_protection_remain_observable(string changed) =>
        Assert.NotEqual(PermissionAclSnapshot.Normalize(Original), PermissionAclSnapshot.Normalize(changed));

    [Fact]
    public void Allows_are_never_sorted_across_a_deny() =>
        Assert.NotEqual(PermissionAclSnapshot.Normalize("D:(A;;FR;;;SY)(D;;FW;;;SY)(A;;FA;;;BA)"),
            PermissionAclSnapshot.Normalize("D:(A;;FA;;;BA)(D;;FW;;;SY)(A;;FR;;;SY)"));

    [Fact]
    public void Inherited_ace_order_is_preserved() =>
        Assert.NotEqual(PermissionAclSnapshot.Normalize("D:(A;ID;FR;;;SY)(A;ID;FA;;;BA)"),
            PermissionAclSnapshot.Normalize("D:(A;ID;FA;;;BA)(A;ID;FR;;;SY)"));
}
