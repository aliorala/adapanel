using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using AdaPanel.Infrastructure.Files;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class WebConfigurationFileTests
{
    private static readonly byte[] Original = Encoding.UTF8.GetBytes("<configuration><!-- exact previous bytes --></configuration>\r\n");
    private static XDocument NewDocument() => XDocument.Parse("<configuration><appSettings><add key=\"password\" value=\"private-value\" /></appSettings></configuration>");
    private static readonly SecurityIdentifier SiteUser = new(WellKnownSidType.BuiltinUsersSid, null);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StagedSecretsRemainPrivateAndTheOldTargetIsUntouchedUntilPublication(bool exists)
    {
        using var f = new Fixture(exists); f.GrantInheritedSiteRead();
        string? staged = null; var observed = false;

        WebConfigurationFile.Save(f.Configuration, NewDocument(), stream =>
        {
            Assert.IsType<FileStream>(stream);
            staged = Assert.Single(Directory.GetFiles(f.SiteRoot, ".adapanel-config-*"));
            Assert.NotEqual(f.Configuration, staged);
            return new ObservedXmlWriteStream(stream, () =>
            {
                var security = new FileInfo(staged).GetAccessControl();
                Assert.True(security.AreAccessRulesProtected);
                Assert.False(CanSiteRead(security));
                using var reading = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(reading);
                Assert.Contains("private-value", reader.ReadToEnd());
                if (exists) Assert.Equal(Original, File.ReadAllBytes(f.Configuration));
                else Assert.False(File.Exists(f.Configuration));
                observed = true;
            });
        });

        Assert.True(observed);
        Assert.NotNull(staged); Assert.False(File.Exists(staged));
        Assert.Equal("private-value", XDocument.Load(f.Configuration).Descendants("add").Single().Attribute("value")!.Value);
        f.AssertNoTemporaryFiles();
    }

    [Fact]
    public void ReplacingAnExistingConfigurationPreservesItsProtectedAccessRules()
    {
        using var f = new Fixture(true);
        var file = new FileInfo(f.Configuration);
        var security = file.GetAccessControl();
        security.SetAccessRuleProtection(true, true);
        security.AddAccessRule(new FileSystemAccessRule(SiteUser, FileSystemRights.Read, AccessControlType.Allow));
        file.SetAccessControl(security);
        var before = Rules(file.GetAccessControl());

        WebConfigurationFile.Save(f.Configuration, NewDocument());

        var after = file.GetAccessControl();
        Assert.True(after.AreAccessRulesProtected);
        Assert.Equal(before, Rules(after));
        Assert.True(CanSiteRead(after));
        f.AssertNoTemporaryFiles();
    }

    [Fact]
    public void ANewPublishedConfigurationInheritsTheSiteReadGrant()
    {
        using var f = new Fixture(false); f.GrantInheritedSiteRead();

        WebConfigurationFile.Save(f.Configuration, NewDocument());

        var security = new FileInfo(f.Configuration).GetAccessControl();
        Assert.False(security.AreAccessRulesProtected);
        Assert.True(CanSiteRead(security));
        Assert.Contains(security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
            rule => rule.IsInherited && rule.IdentityReference.Equals(SiteUser) && (rule.FileSystemRights & FileSystemRights.ReadData) != 0);
        f.AssertNoTemporaryFiles();
    }

    [Fact]
    public void FailedNewFileAccessPublicationReturnsTheNewTargetToAbsence()
    {
        using var f = new Fixture(false); f.GrantInheritedSiteRead();

        Assert.Throws<UnauthorizedAccessException>(() => WebConfigurationFile.Save(f.Configuration, NewDocument(),
            inheritNewFileAccess: path =>
            {
                Assert.Equal(f.Configuration, path);
                Assert.NotNull(XDocument.Load(path).Root);
                Assert.True(new FileInfo(path).GetAccessControl().AreAccessRulesProtected);
                throw new UnauthorizedAccessException("fixture ACL publication rejected");
            }));

        Assert.False(File.Exists(f.Configuration));
        f.AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CancellationAfterSerializationDoesNotPublishAFile(bool exists)
    {
        using var f = new Fixture(exists); using var canceled = new CancellationTokenSource();

        Assert.ThrowsAny<OperationCanceledException>(() => WebConfigurationFile.Save(f.Configuration, NewDocument(),
            stream => new ObservedXmlWriteStream(stream, canceled.Cancel), canceled.Token));

        if (exists) Assert.Equal(Original, File.ReadAllBytes(f.Configuration));
        else Assert.False(File.Exists(f.Configuration));
        f.AssertNoTemporaryFiles();
    }

    [Fact]
    public void FailedAtomicReplacementPreservesTheLockedExistingConfiguration()
    {
        using var f = new Fixture(true);
        using (var locked = new FileStream(f.Configuration, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.ThrowsAny<IOException>(() => WebConfigurationFile.Save(f.Configuration, NewDocument()));

        Assert.Equal(Original, File.ReadAllBytes(f.Configuration));
        f.AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LinkedTargetOrParentCannotWriteToAnotherLocation(bool parentLink)
    {
        using var f = new Fixture(false);
        var foreign = Path.Combine(f.OutsideRoot, "web.config");
        File.WriteAllBytes(foreign, Original);
        if (parentLink)
        {
            Directory.Delete(f.SiteRoot);
            Directory.CreateSymbolicLink(f.SiteRoot, f.OutsideRoot);
        }
        else File.CreateSymbolicLink(f.Configuration, foreign);

        Assert.ThrowsAny<IOException>(() => WebConfigurationFile.Save(f.Configuration, NewDocument()));

        Assert.Equal(Original, File.ReadAllBytes(foreign));
        Assert.Empty(Directory.GetFiles(f.OutsideRoot, ".adapanel-config-*"));
    }

    [Fact]
    public void TargetIsCheckedForLinksAgainAfterSerialization()
    {
        using var f = new Fixture(false);
        var foreign = Path.Combine(f.OutsideRoot, "web.config"); File.WriteAllBytes(foreign, Original);
        var linked = false;

        Assert.ThrowsAny<IOException>(() => WebConfigurationFile.Save(f.Configuration, NewDocument(),
            stream => new ObservedXmlWriteStream(stream, () =>
            {
                if (!linked) { File.CreateSymbolicLink(f.Configuration, foreign); linked = true; }
            })));

        Assert.True(linked);
        Assert.Equal(Original, File.ReadAllBytes(foreign));
        f.AssertNoTemporaryFiles();
    }

    private static bool CanSiteRead(FileSecurity security) => security.GetAccessRules(true, true, typeof(SecurityIdentifier))
        .Cast<FileSystemAccessRule>().Any(rule => rule.IdentityReference.Equals(SiteUser)
            && rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & FileSystemRights.ReadData) != 0);

    [Theory]
    [InlineData("replacement", true)]
    [InlineData("replacement", false)]
    [InlineData("same-content-replacement", true)]
    [InlineData("same-content-replacement", false)]
    [InlineData("inplace-write", true)]
    [InlineData("inplace-write", false)]
    public void NewPublicationNeverDeletesOrGrantsAccessToAForeignFile(string change, bool accessFails)
    {
        using var f = new Fixture(false); f.GrantInheritedSiteRead();
        byte[] foreign = Encoding.UTF8.GetBytes("<configuration><!-- foreign writer --></configuration>");
        string[]? accessBefore = null;

        var error = Record.Exception(() => WebConfigurationFile.Save(f.Configuration, NewDocument(),
            inheritNewFileAccess: path =>
            {
                if (change == "same-content-replacement") foreign = File.ReadAllBytes(path);
                if (change != "inplace-write")
                {
                    File.Move(path, Path.Combine(f.OutsideRoot, "original-publication.config"));
                    File.WriteAllBytes(path, foreign);
                    new FileInfo(path).SetAccessControl(AdaPanel.Infrastructure.Support.SecureTempFile.BuildSecurity());
                }
                else File.WriteAllBytes(path, foreign);
                accessBefore = Rules(new FileInfo(path).GetAccessControl());
                if (accessFails) throw new IOException("fixture access publication failed after a foreign write");
            }));

        Assert.IsAssignableFrom<IOException>(error);
        Assert.Equal(foreign, File.ReadAllBytes(f.Configuration));
        Assert.Equal(accessBefore, Rules(new FileInfo(f.Configuration).GetAccessControl()));
        Assert.False(CanSiteRead(new FileInfo(f.Configuration).GetAccessControl()));
        f.AssertNoTemporaryFiles();
    }
    private static string[] Rules(FileSecurity security) => security.GetAccessRules(true, true, typeof(SecurityIdentifier))
        .Cast<FileSystemAccessRule>().Select(rule => $"{rule.IdentityReference}|{rule.FileSystemRights}|{rule.AccessControlType}|{rule.IsInherited}|{rule.InheritanceFlags}|{rule.PropagationFlags}")
        .OrderBy(rule => rule, StringComparer.Ordinal).ToArray();
    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "AdaPanel.AtomicXml." + Guid.NewGuid().ToString("N"));
        public string SiteRoot => Path.Combine(root, "site");
        public string OutsideRoot => Path.Combine(root, "outside");
        public string Configuration => Path.Combine(SiteRoot, "web.config");
        public Fixture(bool exists)
        {
            Directory.CreateDirectory(SiteRoot); Directory.CreateDirectory(OutsideRoot);
            if (exists) File.WriteAllBytes(Configuration, Original);
        }
        public void GrantInheritedSiteRead()
        {
            var directory = new DirectoryInfo(SiteRoot); var security = directory.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(SiteUser, FileSystemRights.Read,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            directory.SetAccessControl(security);
        }
        public void AssertNoTemporaryFiles() => Assert.Empty(Directory.GetFiles(SiteRoot, ".adapanel-config-*"));
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
