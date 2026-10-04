using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Files;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Files;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class FileCompressionLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASourceFileCannotReplaceItselfWithItsOwnArchive(bool changeCase)
    {
        using var fixture = new Fixture();
        var original = fixture.Write("source.txt", "ORIGINAL SOURCE");
        var error = await Record.ExceptionAsync(() => fixture.Service.CompressAsync(new ArchiveFileRequest
        {
            Path = "sites/a.test/source.txt",
            DestinationPath = "sites/a.test/" + (changeCase ? "SOURCE.TXT" : "source.txt")
        }));
        Assert.Equal("ORIGINAL SOURCE", await File.ReadAllTextAsync(original));
        Assert.Equal("files.archive_target_invalid", Assert.IsType<AppException>(error).MessageKey);
        Assert.Equal(["source.txt"], fixture.RelativeFiles());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnArchiveInsideItsSourceTreeIsRejectedBeforeChangingEither(bool existing)
    {
        using var fixture = new Fixture();
        fixture.Write("source/content.txt", "SOURCE CONTENT");
        var destination = fixture.PathFor("source/nested/result.zip");
        var before = existing ? fixture.OldArchive("source/nested/result.zip") : null;
        var filesBefore = fixture.RelativeFiles();
        var error = await Record.ExceptionAsync(() => fixture.Service.CompressAsync(new ArchiveFileRequest
        {
            Path = "sites/a.test/source", DestinationPath = "sites/a.test/source/nested/result.zip"
        }));
        fixture.AssertPreserved(destination, before);
        Assert.Equal(filesBefore, fixture.RelativeFiles());
        Assert.Equal("SOURCE CONTENT", await File.ReadAllTextAsync(fixture.PathFor("source/content.txt")));
        Assert.Equal("files.archive_target_invalid", Assert.IsType<AppException>(error).MessageKey);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ASourceReadFailurePreservesThePreviousArchiveAndLeavesNoPartialFile(bool directory, bool existing)
    {
        using var fixture = new Fixture();
        var source = directory ? "source/locked.txt" : "source.txt";
        var sourceFile = fixture.Write(source, "SOURCE THAT CANNOT BE READ");
        if (directory) fixture.Write("source/readable.txt", "OTHER FILE");
        var destination = fixture.PathFor("result.zip");
        var before = existing ? fixture.OldArchive("result.zip") : null;
        var filesBefore = fixture.RelativeFiles();
        Exception? error;
        using (var locked = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.None))
            error = await Record.ExceptionAsync(() => fixture.Service.CompressAsync(new ArchiveFileRequest
            {
                Path = "sites/a.test/" + (directory ? "source" : source),
                DestinationPath = "sites/a.test/result.zip", Overwrite = true
            }));
        Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString() ?? "Unexpected success");
        fixture.AssertPreserved(destination, before);
        Assert.Equal(filesBefore, fixture.RelativeFiles());
        Assert.Equal("SOURCE THAT CANNOT BE READ", await File.ReadAllTextAsync(sourceFile));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CancellationBeforeOrDuringSiteResolutionDoesNotReplaceTheExistingZip(bool directory, bool duringResolution)
    {
        using var fixture = new Fixture();
        fixture.Write(directory ? "source/content.txt" : "source.txt", "SOURCE CONTENT");
        var before = fixture.OldArchive("result.zip");
        var filesBefore = fixture.RelativeFiles();
        using var cancellation = new CancellationTokenSource();
        if (duringResolution) fixture.OnResolve = call => { if (call == 2) cancellation.Cancel(); };
        else cancellation.Cancel();
        var error = await Record.ExceptionAsync(() => fixture.Service.CompressAsync(new ArchiveFileRequest
        {
            Path = "sites/a.test/" + (directory ? "source" : "source.txt"),
            DestinationPath = "sites/a.test/result.zip", Overwrite = true
        }, cancellation.Token));
        fixture.AssertPreserved(fixture.PathFor("result.zip"), before);
        Assert.Equal(filesBefore, fixture.RelativeFiles());
        Assert.IsAssignableFrom<OperationCanceledException>(error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedDirectoryLinksCannotDiscloseFilesOutsideTheSourceTree(bool existing)
    {
        using var fixture = new Fixture();
        fixture.Write("source/local.txt", "LOCAL");
        var foreign = Path.Combine(fixture.Root, "foreign");
        Directory.CreateDirectory(foreign);
        await File.WriteAllTextAsync(Path.Combine(foreign, "private.txt"), "FOREIGN SECRET");
        fixture.CreateJunction("source/linked", foreign);
        var destination = fixture.PathFor("result.zip");
        var before = existing ? fixture.OldArchive("result.zip") : null;
        var error = await Record.ExceptionAsync(() => fixture.Service.CompressAsync(new ArchiveFileRequest
        {
            Path = "sites/a.test/source", DestinationPath = "sites/a.test/result.zip", Overwrite = true
        }));
        fixture.AssertPreserved(destination, before);
        Assert.Equal("files.path_outside_root", Assert.IsType<AppException>(error).MessageKey);
        Assert.Equal("FOREIGN SECRET", await File.ReadAllTextAsync(Path.Combine(foreign, "private.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACompleteArchiveCanReplaceAnExistingZipAndContainsTheOriginalBytes(bool directory)
    {
        using var fixture = new Fixture();
        var bytes = Enumerable.Range(0, 4096).Select(index => (byte)(index % 251)).ToArray();
        var source = fixture.Write(directory ? "source/nested/data.bin" : "source.bin", bytes);
        if (directory)
        {
            Directory.CreateDirectory(fixture.PathFor("source/empty"));
            var hidden = fixture.Write("source/.hidden.txt", "HIDDEN CONTENT");
            File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
        }
        fixture.OldArchive("result.zip");
        var workspace = await fixture.Service.CompressAsync(new ArchiveFileRequest
        {
            Path = "sites/a.test/" + (directory ? "source" : "source.bin"),
            DestinationPath = "sites/a.test/result.zip", Overwrite = true
        });
        Assert.Equal("sites/a.test", workspace.Path);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(source));
        using var zip = ZipFile.OpenRead(fixture.PathFor("result.zip"));
        Assert.Null(zip.GetEntry("previous.txt"));
        var entry = Assert.IsType<ZipArchiveEntry>(zip.GetEntry(directory ? "source/nested/data.bin" : "source.bin"));
        using var content = new MemoryStream();
        await using (var input = entry.Open()) await input.CopyToAsync(content);
        Assert.Equal(bytes, content.ToArray());
        if (directory)
        {
            Assert.NotNull(zip.GetEntry("source/empty/"));
            await using var input = Assert.IsType<ZipArchiveEntry>(zip.GetEntry("source/.hidden.txt")).Open();
            using var reader = new StreamReader(input);
            Assert.Equal("HIDDEN CONTENT", await reader.ReadToEndAsync());
        }
        Assert.DoesNotContain(fixture.RelativeFiles(), path => path.Contains(".adapanel-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnEmptyDirectoryAndDefaultDestinationRemainSupported()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.PathFor("empty"));
        await fixture.Service.CompressAsync(new ArchiveFileRequest { Path = "sites/a.test/empty" });
        using var zip = ZipFile.OpenRead(fixture.PathFor("empty.zip"));
        Assert.Equal("empty/", Assert.Single(zip.Entries).FullName);
    }

    [Fact]
    public async Task MissingSourcePreservesThePreviousZip()
    {
        using var fixture = new Fixture();
        var before = fixture.OldArchive("result.zip");
        var error = await Assert.ThrowsAsync<AppException>(() => fixture.Service.CompressAsync(new ArchiveFileRequest
        { Path = "sites/a.test/missing", DestinationPath = "sites/a.test/result.zip" }));
        Assert.Equal("files.not_found", error.MessageKey);
        fixture.AssertPreserved(fixture.PathFor("result.zip"), before);
    }

    [Theory]
    [InlineData(false, "io")]
    [InlineData(true, "io")]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    [InlineData(false, "ignored_cancel")]
    [InlineData(true, "ignored_cancel")]
    public async Task FailureAfterCopyingBytesNeverPublishesTheIncompleteArchive(bool existing, string failure)
    {
        using var fixture = new Fixture();
        var source = fixture.Write("source.txt", "ORIGINAL SOURCE");
        var destination = fixture.PathFor("result.zip");
        var before = existing ? fixture.OldArchive("result.zip") : null;
        var filesBefore = fixture.RelativeFiles();
        using var cancellation = new CancellationTokenSource();
        var input = new InterruptedRead(File.ReadAllBytes(source), () =>
        {
            fixture.AssertPreserved(destination, before);
            if (failure == "io") throw new IOException("Fixture source read failure after first bytes");
            cancellation.Cancel();
            if (failure == "cancel") throw new OperationCanceledException(cancellation.Token);
        });
        fixture.Service.OpenCompressionFile = path => { Assert.Equal(source, path); return input; };
        var error = await Record.ExceptionAsync(() => fixture.Service.CompressAsync(new ArchiveFileRequest
        { Path = "sites/a.test/source.txt", DestinationPath = "sites/a.test/result.zip", Overwrite = true }, cancellation.Token));
        if (failure == "io") Assert.IsType<IOException>(error);
        else Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.True(input.WasRead);
        Assert.False(input.CanRead);
        fixture.AssertPreserved(destination, before);
        Assert.Equal(filesBefore, fixture.RelativeFiles());
        Assert.Equal("ORIGINAL SOURCE", await File.ReadAllTextAsync(source));
    }

    [Fact]
    public async Task ANewDraftCannotChangeTheAcceptedDestinationDuringSiteResolution()
    {
        using var fixture = new Fixture();
        fixture.Write("source.txt", "ORIGINAL SOURCE");
        var request = new ArchiveFileRequest { Path = "sites/a.test/source.txt", DestinationPath = "sites/a.test/result.zip" };
        fixture.OnResolve = call =>
        {
            if (call == 1)
            {
                request.Path = "sites/a.test/other.txt";
                request.DestinationPath = "sites/a.test/unapproved.zip";
            }
        };
        await fixture.Service.CompressAsync(request);
        Assert.True(File.Exists(fixture.PathFor("result.zip")));
        Assert.False(File.Exists(fixture.PathFor("unapproved.zip")));
        using var zip = ZipFile.OpenRead(fixture.PathFor("result.zip"));
        Assert.Equal("source.txt", Assert.Single(zip.Entries).FullName);
    }

    private sealed class InterruptedRead : MemoryStream
    {
        private readonly byte[] bytes;
        private readonly Action interrupt;
        public InterruptedRead(byte[] bytes, Action interrupt) : base(bytes)
        {
            this.bytes = bytes;
            this.interrupt = interrupt;
        }
        public bool WasRead { get; private set; }
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            WasRead = true;
            await destination.WriteAsync(bytes.AsMemory(0, 4), cancellationToken);
            interrupt();
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string siteRoot;
        private readonly List<string> links = [];
        private int resolves;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "adapanel-compress-" + Guid.NewGuid().ToString("N"));
        public FileService Service { get; }
        public Action<int>? OnResolve { get; set; }

        public Fixture()
        {
            siteRoot = Path.Combine(Root, "site");
            Directory.CreateDirectory(siteRoot);
            var sites = DispatchProxy.Create<IIisSiteService, SiteProxy>();
            ((SiteProxy)(object)sites).Resolve = name =>
            {
                Assert.Equal("a.test", name);
                OnResolve?.Invoke(++resolves);
                return new SiteDto { Name = name, PhysicalPath = siteRoot, ApplicationRootPath = siteRoot };
            };
            Service = new FileService(sites);
        }

        public string PathFor(string relative) => Path.Combine(siteRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        public string Write(string relative, string content) => Write(relative, Encoding.UTF8.GetBytes(content));
        public string Write(string relative, byte[] content)
        {
            var path = PathFor(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
            return path;
        }
        public byte[] OldArchive(string relative)
        {
            var path = PathFor(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("previous.txt").Open())) writer.Write("PREVIOUS COMPLETE ARCHIVE");
            return File.ReadAllBytes(path);
        }
        public void AssertPreserved(string path, byte[]? before)
        {
            if (before is null) Assert.False(File.Exists(path), "A failed compression published a partial destination.");
            else { Assert.True(File.Exists(path)); Assert.Equal(before, File.ReadAllBytes(path)); }
        }
        public string[] RelativeFiles() => Directory.GetFiles(siteRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(siteRoot, path).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
        public void CreateJunction(string relative, string target)
        {
            var link = PathFor(relative);
            Assert.StartsWith(Root + Path.DirectorySeparatorChar, Path.GetFullPath(link), StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(Root + Path.DirectorySeparatorChar, Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase);
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe", Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            })!;
            Assert.True(process.WaitForExit(10_000), "Temporary junction creation did not finish.");
            Assert.Equal(0, process.ExitCode);
            links.Add(link);
            Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint));
        }
        public void Dispose()
        {
            foreach (var link in links) if (Directory.Exists(link)) Directory.Delete(link);
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    public class SiteProxy : DispatchProxy
    {
        public Func<string, SiteDto> Resolve { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method?.Name == nameof(IIisSiteService.GetSiteAsync)
                ? Task.FromResult(Resolve((string)args![0]!))
                : throw new NotSupportedException(method?.Name);
    }
}
