using System.ComponentModel;
using AdaPanel.Infrastructure.Mail;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class MailEnableInstallationTests
{
    [Theory]
    [InlineData("BIN", "BIN", false)]
    [InlineData("BIN", "BIN64", true)]
    [InlineData("BIN64", "BIN", false)]
    [InlineData("BIN64", "BIN64", true)]
    public void Registered_custom_installation_supports_both_binary_layouts_and_arguments(string serviceBin, string installerBin, bool quoted)
    {
        using var fixture = new InstallationFixture(serviceBin, installerBin);
        var command = quoted ? "  \"" + fixture.Executable + "\" /service --flag=1  " : fixture.Executable + " /service --flag=1";
        Assert.Equal(fixture.Root, MailEnableInstallation.DiscoverRoot(() => command));
    }

    [Fact]
    public void Environment_expansion_and_quoted_path_disambiguation_preserve_the_registered_installation()
    {
        using var fixture = new InstallationFixture();
        var variable = "ADAPANEL_ME_FIXTURE_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, fixture.Root);
        try
        {
            Assert.Equal(fixture.Root, MailEnableInstallation.DiscoverRoot(() => "\"%" + variable + "%\\BIN64\\MESMTPC.exe\" -service"));
            File.WriteAllText(Path.Combine(fixture.Container, "Mail.exe"), "ambiguous executable candidate");
            Assert.Null(MailEnableInstallation.DiscoverRoot(() => fixture.Executable + " -service"));
            Assert.Equal(fixture.Root, MailEnableInstallation.DiscoverRoot(() => "\"" + fixture.Executable + "\" -service"));
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Theory]
    [InlineData("missing-service")]
    [InlineData("missing-executable")]
    [InlineData("missing-installer")]
    [InlineData("installer-outside-root")]
    [InlineData("wrong-directory")]
    [InlineData("wrong-executable")]
    [InlineData("wrong-extension")]
    [InlineData("unclosed-quote")]
    [InlineData("joined-arguments")]
    [InlineData("relative-path")]
    [InlineData("wrapper")]
    [InlineData("embedded-command")]
    public void Leftovers_and_unverified_service_paths_do_not_claim_an_installation(string scenario)
    {
        using var fixture = new InstallationFixture();
        string? path = fixture.Executable;
        switch (scenario)
        {
            case "missing-service": path = null; break;
            case "missing-executable": File.Delete(fixture.Executable); break;
            case "missing-installer": File.Delete(fixture.Installer); break;
            case "installer-outside-root":
                File.Move(fixture.Installer, Path.Combine(fixture.Container, "MEInstaller.exe")); break;
            case "wrong-directory":
                Directory.CreateDirectory(Path.Combine(fixture.Root, "tools"));
                path = Path.Combine(fixture.Root, "tools", "MESMTPC.exe");
                File.WriteAllText(path, "fixture"); break;
            case "wrong-executable":
                path = Path.Combine(fixture.Root, "BIN64", "other.exe");
                File.WriteAllText(path, "fixture"); break;
            case "wrong-extension":
                path = fixture.Executable + ".bak";
                File.WriteAllText(path, "fixture"); break;
            case "unclosed-quote": path = "\"" + path; break;
            case "joined-arguments": path = "\"" + path + "\"--service"; break;
            case "relative-path": path = "BIN64\\MESMTPC.exe"; break;
            case "wrapper": path = "\"C:\\Windows\\System32\\cmd.exe\" /c \"" + path + "\""; break;
            case "embedded-command": path = "C:\\Windows\\System32\\cmd.exe /c " + path; break;
        }
        Assert.Null(MailEnableInstallation.DiscoverRoot(() => path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_or_unreadable_service_registration_never_falls_back_to_remaining_files(bool unreadable)
    {
        using var fixture = new InstallationFixture();
        Assert.True(File.Exists(fixture.Installer));
        Assert.True(File.Exists(fixture.Executable));
        Assert.Null(MailEnableInstallation.DiscoverRoot(() => unreadable ? throw new Win32Exception(5) : null));
    }

    private sealed class InstallationFixture : IDisposable
    {
        public string Container { get; } = Path.Combine(Path.GetTempPath(), "adapanel-mailenable-" + Guid.NewGuid().ToString("N"));
        public string Root { get; }
        public string Executable { get; }
        public string Installer { get; }
        public InstallationFixture(string serviceBin = "BIN64", string installerBin = "BIN")
        {
            // A directory containing '.exe ' must not be mistaken for an executable.
            Root = Path.Combine(Container, "Mail Suite.exe files");
            Executable = Path.Combine(Root, serviceBin, "MESMTPC.exe");
            Installer = Path.Combine(Root, installerBin, "MEInstaller.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(Executable)!);
            Directory.CreateDirectory(Path.GetDirectoryName(Installer)!);
            File.WriteAllText(Executable, "fixture only; never executed");
            File.WriteAllText(Installer, "fixture only; never executed");
        }
        public void Dispose()
        {
            var full = Path.GetFullPath(Container);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(full), temp, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(full).StartsWith("adapanel-mailenable-", StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup escaped its owned directory.");
            Directory.Delete(full, recursive: true);
        }
    }
}
