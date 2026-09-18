using System.Diagnostics;
using System.Text;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class PowerShellEnvironmentTests
{
    [Fact]
    public async Task Built_in_commands_remain_available_with_an_inherited_incompatible_module()
    {
        var root = Path.Combine(Path.GetTempPath(), "AdaPanel-ps-module-" + Guid.NewGuid().ToString("N"));
        var module = Path.Combine(root, "Microsoft.PowerShell.Utility");
        Directory.CreateDirectory(module);
        await File.WriteAllTextAsync(Path.Combine(module, "Microsoft.PowerShell.Utility.psd1"),
            "@{ModuleVersion='99.0';GUID='90c691e5-0f1f-4634-b03b-a8bcba4ef9f6';PowerShellVersion='99.0';RootModule='missing.psm1'}");
        try
        {
            var script = "$ErrorActionPreference='Stop'; Get-Command Get-FileHash,Get-Acl,Expand-Archive | ForEach-Object Name";
            var result = await ProcessRunner.RunAsync("powershell.exe",
                "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
                environmentVariables: new Dictionary<string, string> { ["PSModulePath"] = root });
            Assert.True(result.Success, result.StandardError);
            Assert.Contains("Get-FileHash", result.StandardOutput);
            Assert.Contains("Get-Acl", result.StandardOutput);
            Assert.Contains("Expand-Archive", result.StandardOutput);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Custom_module_paths_are_preserved_and_other_executables_are_unchanged()
    {
        var ps = new ProcessStartInfo("powershell.exe");
        ps.Environment["PSModulePath"] = @"C:\CustomModules;D:\MoreModules";
        ProcessRunner.ConfigureWindowsPowerShellEnvironment(ps);
        Assert.EndsWith(@";C:\CustomModules;D:\MoreModules", ps.Environment["PSModulePath"]);
        var once = ps.Environment["PSModulePath"];
        ProcessRunner.ConfigureWindowsPowerShellEnvironment(ps);
        Assert.Equal(once, ps.Environment["PSModulePath"]);
        var other = new ProcessStartInfo("pwsh.exe");
        other.Environment["PSModulePath"] = "custom";
        ProcessRunner.ConfigureWindowsPowerShellEnvironment(other);
        Assert.Equal("custom", other.Environment["PSModulePath"]);
    }
}
