using System.Diagnostics;
using System.Reflection;
using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.AppRuntimes;
using AdaPanel.Infrastructure.ReverseProxy;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class RuntimeAndProxyPathTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ada-runtime-path-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> junctions = [];

    public RuntimeAndProxyPathTests() => Directory.CreateDirectory(root);

    [Theory]
    [InlineData("linked", true)]
    [InlineData("linked/server.js", false)]
    public void Runtime_paths_reject_junctions_outside_the_site(string path, bool directory)
    {
        var site = Path.Combine(root, "site");
        Directory.CreateDirectory(site);
        Junction(Path.Combine(site, "linked"));
        var error = Assert.Throws<TargetInvocationException>(() => ResolveRuntime(site, path, directory));
        Assert.IsType<AppException>(error.InnerException);
    }

    [Fact]
    public void Proxy_web_config_must_not_follow_a_link()
    {
        var site = Path.Combine(root, "site");
        Directory.CreateDirectory(site);
        Junction(Path.Combine(site, "web.config"));
        var method = typeof(ReverseProxyService).GetMethod("ResolveWebConfigPath", BindingFlags.Static | BindingFlags.NonPublic)!;
        var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [site]));
        Assert.IsType<AppException>(error.InnerException);
    }

    [Theory]
    [InlineData("server.js", false)]
    [InlineData("new/subdir", true)]
    [InlineData(".", true)]
    public void Ordinary_site_paths_remain_usable(string path, bool directory)
        => Assert.Equal(Path.GetFullPath(Path.Combine(root, path)), ResolveRuntime(root, path, directory));

    private static string ResolveRuntime(string site, string path, bool directory)
        => (string)typeof(AppRuntimeService).GetMethod("ResolveSiteManagedPath", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [site, path, directory])!;

    private void Junction(string link)
    {
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        Assert.True(process.WaitForExit(10000));
        Assert.Equal(0, process.ExitCode);
        junctions.Add(link);
    }

    public void Dispose()
    {
        foreach (var link in junctions) Directory.Delete(link);
        Directory.Delete(root, recursive: true);
    }
}
