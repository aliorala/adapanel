using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Files;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Files;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class FileExtractAwaitBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APreviouslyCheckedPrefixMustBeCheckedAgainAfterAnEntryWait(bool authorizationWait)
    {
        using var fixture = new Fixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task WaitAtBoundary()
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(deadline.Token);
        }

        var secondEntry = false;
        fixture.Service.ExtractEntryReady = async (entry, _) =>
        {
            secondEntry = entry == "nested/second.txt";
            if (secondEntry && !authorizationWait) await WaitAtBoundary();
        };
        var approvalPaused = false;
        async Task Approve(SiteDto site, CancellationToken _)
        {
            if (site.Name == "target.test" && secondEntry && !approvalPaused)
            {
                approvalPaused = true;
                await WaitAtBoundary();
            }
        }

        var request = new ArchiveFileRequest { Path = "sites/source.test/archive.zip", DestinationPath = "sites/target.test/output" };
        var extraction = authorizationWait
            ? fixture.Service.ExtractAsync(request, Approve, deadline.Token)
            : fixture.Service.ExtractAsync(request, deadline.Token);
        try
        {
            var first = await Task.WhenAny(extraction, entered.Task).WaitAsync(deadline.Token);
            if (first == extraction) await extraction;
            await entered.Task.WaitAsync(deadline.Token);
            Assert.Equal("FIRST", File.ReadAllText(Path.Combine(fixture.Link, "first.txt")));
            await fixture.ReplaceCheckedDirectoryWithJunction(deadline.Token);
            release.TrySetResult();
            var failure = await Record.ExceptionAsync(() => extraction);

            // Check actual bytes first: the old cached prefix overwrites this outside-sandbox file.
            Assert.Equal("OUTSIDE SENTINEL", File.ReadAllText(Path.Combine(fixture.Outside, "second.txt")));
            Assert.Equal("FIRST", File.ReadAllText(Path.Combine(fixture.Original, "first.txt")));
            Assert.False(File.Exists(Path.Combine(fixture.Outside, "first.txt")));
            Assert.Equal("files.path_outside_root", Assert.IsType<AppException>(failure).Message);
        }
        finally
        {
            release.TrySetResult();
            try { await extraction.WaitAsync(deadline.Token); } catch { /* Observe before fixture cleanup. */ }
        }
    }

    private sealed class Fixture : IDisposable
    {
        private const string Prefix = "adapanel-extract-await-";
        public string Root { get; } = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N")));
        public string Link => Path.Combine(Root, "target", "output", "nested");
        public string Original => Path.Combine(Root, "target", "output", "original");
        public string Outside => Path.Combine(Root, "outside-sandbox");
        public FileService Service { get; }

        public Fixture()
        {
            var source = Path.Combine(Root, "source");
            var target = Path.Combine(Root, "target");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(target);
            Directory.CreateDirectory(Outside);
            File.WriteAllText(Path.Combine(Outside, "second.txt"), "OUTSIDE SENTINEL");
            using (var zip = ZipFile.Open(Path.Combine(source, "archive.zip"), ZipArchiveMode.Create))
            {
                foreach (var (name, content) in new[] { ("nested/first.txt", "FIRST"), ("nested/second.txt", "SECOND") })
                {
                    using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                    writer.Write(content);
                }
            }
            var sites = DispatchProxy.Create<IIisSiteService, SitesProxy>();
            ((SitesProxy)(object)sites).Sites = new Dictionary<string, SiteDto>
            {
                ["source.test"] = new() { Name = "source.test", Id = 1, PhysicalPath = source, ApplicationRootPath = source },
                ["target.test"] = new() { Name = "target.test", Id = 2, PhysicalPath = target, ApplicationRootPath = target }
            };
            Service = new FileService(sites);
        }

        public async Task ReplaceCheckedDirectoryWithJunction(CancellationToken token)
        {
            RequireOwnedPath(Link);
            RequireOwnedPath(Original);
            RequireOwnedPath(Outside);
            Directory.Move(Link, Original);
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{Link}\" \"{Outside}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }) ?? throw new InvalidOperationException("Unable to create the fixture junction process.");
            await process.WaitForExitAsync(token);
            Assert.Equal(0, process.ExitCode);
            Assert.True(File.GetAttributes(Link).HasFlag(FileAttributes.ReparsePoint));
        }

        private void RequireOwnedPath(string path)
        {
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(Root), temp, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(Root).StartsWith(Prefix, StringComparison.Ordinal) ||
                !Path.GetFullPath(path).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fixture path is outside its unique temporary root.");
        }

        public void Dispose()
        {
            RequireOwnedPath(Link);
            // Remove only the owned link itself before any recursive cleanup of the test root.
            if (Directory.Exists(Link) && File.GetAttributes(Link).HasFlag(FileAttributes.ReparsePoint))
                Directory.Delete(Link, recursive: false);
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    public class SitesProxy : DispatchProxy
    {
        public Dictionary<string, SiteDto> Sites { get; set; } = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method!.Name == nameof(IIisSiteService.GetSiteAsync)
                ? Task.FromResult(Sites[(string)args![0]!])
                : throw new NotSupportedException(method.Name);
    }
}
