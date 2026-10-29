using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AdaPanel.Infrastructure.Mail;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>Passes the production quoting function through a real Windows child argv boundary.</summary>
public sealed class MailCliArgumentTests : IClassFixture<MailCliArgvProbe>
{
    private readonly MailCliArgvProbe probe;

    public MailCliArgumentTests(MailCliArgvProbe probe) => this.probe = probe;

    private static string Quote(string value) => (string)typeof(MailService)
        .GetMethod("CliJsonArg", BindingFlags.Static | BindingFlags.NonPublic)!
        .Invoke(null, new object[] { value })!;

    [Theory]
    [InlineData("route = 'brevo'")]
    [InlineData(@"C:\mail\queue")]
    [InlineData("quoted \"route\" with spaces")]
    [InlineData("backslash before quote: \\\" tail")]
    [InlineData("ends in slash\\")]
    [InlineData(@"matches('^user\d+@example\.com$')")]
    [InlineData("Türkçe ıİğü 日本語 📨")]
    public void Json_field_survives_real_child_process_and_deserializes_unchanged(string expression)
    {
        // The default serializer deliberately emits apostrophes as \u0027, as in relay routes.
        var json = JsonSerializer.Serialize(new { match = new { }, @else = expression });
        var field = "route=" + json;
        var received = probe.ReadArguments("--field " + Quote(field) + " --sentinel end");

        Assert.Equal(new[] { "--field", field, "--sentinel", "end" }, received);
        using var parsed = JsonDocument.Parse(received[1]["route=".Length..]);
        Assert.Equal(expression, parsed.RootElement.GetProperty("else").GetString());
    }

    [Theory]
    [InlineData(@"field=\u0027literal\u0027")]
    [InlineData(@"field=C:\path\")]
    [InlineData("field=quote \\\" --injected argument")]
    [InlineData("field=two \\\\\" --injected argument")]
    [InlineData("")]
    public void Raw_argument_preserves_backslashes_and_cannot_split_adjacent_arguments(string field)
    {
        Assert.Equal(new[] { "--field", field, "--sentinel", "end" },
            probe.ReadArguments("--field " + Quote(field) + " --sentinel end"));
    }
}

/// <summary>Generated local-only executable: prints argv as base64, does not touch services.</summary>
public sealed class MailCliArgvProbe : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "AdaPanel-mail-argv-" + Guid.NewGuid().ToString("N"));
    private readonly string executable;

    public MailCliArgvProbe()
    {
        Directory.CreateDirectory(directory);
        executable = Path.Combine(directory, "argv.exe");
        var source = Path.Combine(directory, "argv.cs");
        File.WriteAllText(source, """
            using System;
            using System.Text;
            public static class ArgvProbe {
                public static void Main(string[] args) {
                    foreach (var arg in args)
                        Console.WriteLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(arg)));
                }
            }
            """);
        var compiler = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe");
        Assert.True(File.Exists(compiler), "Windows .NET Framework compiler is required for the real argv probe.");
        var start = new ProcessStartInfo(compiler) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in new[] { "/nologo", "/target:exe", "/out:" + executable, source })
            start.ArgumentList.Add(arg);
        using var compilerProcess = Process.Start(start)!;
        Assert.True(compilerProcess.WaitForExit(20_000), "Local argv probe compilation timed out.");
        Assert.True(compilerProcess.ExitCode == 0,
            compilerProcess.StandardOutput.ReadToEnd() + compilerProcess.StandardError.ReadToEnd());
    }

    public string[] ReadArguments(string commandLine)
    {
        using var process = Process.Start(new ProcessStartInfo(executable, commandLine)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        })!;
        Assert.True(process.WaitForExit(20_000), "Local argv probe timed out.");
        Assert.Equal(0, process.ExitCode);
        Assert.Empty(process.StandardError.ReadToEnd());
        return process.StandardOutput.ReadToEnd().Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .SkipLast(1).Select(line => Encoding.UTF8.GetString(Convert.FromBase64String(line))).ToArray();
    }

    public void Dispose()
    {
        // Delete only our exact generated files; never recursively delete a computed temp path.
        foreach (var name in new[] { "argv.exe", "argv.cs" }) File.Delete(Path.Combine(directory, name));
        Directory.Delete(directory);
    }
}
