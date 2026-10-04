using System.Reflection;
using System.Text;
using AdaPanel.Core.Dtos.Files;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Files;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class FileSearchReadBudgetTests
{
    private const int Limit = 2 * 1024 * 1024;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataFromBeforeOpenCannotAuthorizeAnUnboundedRead(bool replace)
    {
        using var f = new Fixture();
        File.WriteAllText(f.Path, "small");
        f.Service.OpenSearchContent = path =>
        {
            var bytes = Encoding.UTF8.GetBytes(new string('x', Limit) + "needle");
            if (replace)
            {
                var replacement = System.IO.Path.Combine(f.Root, "replacement.tmp");
                File.WriteAllBytes(replacement, bytes);
                File.Move(replacement, path, overwrite: true);
            }
            else File.WriteAllBytes(path, bytes);
            return f.OpenObserved(path);
        };
        var result = await f.Search();
        Assert.True(f.LastStream!.ReadBytes <= Limit, $"Search read {f.LastStream.ReadBytes} bytes after a stale metadata check.");
        Assert.Empty(result.Items);
        Assert.Equal(1, result.ScannedFiles);
        Assert.True(f.LastStream.Disposed);
        Assert.Equal(Limit + "needle".Length, new FileInfo(f.Path).Length);
    }

    [Fact]
    public async Task ExactlyTwoMebibytesRemainSearchableAndUseOneDisposedHandle()
    {
        using var f = new Fixture();
        File.WriteAllText(f.Path, new string('x', Limit - "needle".Length) + "needle", new UTF8Encoding(false));
        var result = await f.Search();
        Assert.Equal("sites/search.test/alpha.txt", Assert.Single(result.Items).Path);
        Assert.Equal(Limit, f.LastStream!.ReadBytes);
        Assert.Equal(1, f.OpenCount);
        Assert.True(f.LastStream.Disposed);
        using var exclusive = new FileStream(f.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task AFileAlreadyAboveTheContentBudgetDoesNotGetOpened()
    {
        using var f = new Fixture();
        File.WriteAllText(f.Path, new string('x', Limit) + "needle");
        Assert.Empty((await f.Search()).Items);
        Assert.Equal(0, f.OpenCount);
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8-bom")]
    [InlineData("utf16-le")]
    [InlineData("utf16-be")]
    [InlineData("utf32-le")]
    [InlineData("utf32-be")]
    public async Task ExistingBomDetectionAndUnicodeContentSearchStayCompatible(string name)
    {
        using var f = new Fixture();
        Encoding encoding = name switch
        {
            "utf8" => new UTF8Encoding(false), "utf8-bom" => new UTF8Encoding(true),
            "utf16-le" => new UnicodeEncoding(false, true), "utf16-be" => new UnicodeEncoding(true, true),
            "utf32-le" => new UTF32Encoding(false, true), _ => new UTF32Encoding(true, true)
        };
        File.WriteAllBytes(f.Path, [.. encoding.GetPreamble(), .. encoding.GetBytes("Türkçe 文本 needle")]);
        var original = File.ReadAllBytes(f.Path);
        Assert.Single((await f.Search()).Items);
        Assert.Equal(original, File.ReadAllBytes(f.Path));
        Assert.Equal(original.Length, f.LastStream!.ReadBytes);
        Assert.True(f.LastStream.Disposed);
    }

    [Fact]
    public async Task CancellationAfterOpenClosesTheActualHandleWithoutPublishingAMatch()
    {
        using var f = new Fixture();
        File.WriteAllText(f.Path, "needle");
        using var canceled = new CancellationTokenSource();
        f.Service.OpenSearchContent = path => { var stream = f.OpenObserved(path); canceled.Cancel(); return stream; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Search(canceled.Token));
        Assert.True(f.LastStream!.Disposed);
        using var exclusive = new FileStream(f.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task FilenameMatchesDoNotNeedToReadLargeContent()
    {
        using var f = new Fixture();
        var large = System.IO.Path.Combine(f.Root, "needle.txt");
        using (var stream = File.Create(large)) stream.SetLength(Limit + 1);
        Assert.Equal("needle.txt", Assert.Single((await f.Search()).Items).Name);
        Assert.Equal(0, f.OpenCount);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "adapanel-search-read-budget-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Root, "alpha.txt");
        public FileService Service { get; }
        public int OpenCount;
        public ObservedFile? LastStream;
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            var sites = DispatchProxy.Create<IIisSiteService, Proxy>();
            ((Proxy)(object)sites).Site = new SiteDto { Name = "search.test", Id = 12, PhysicalPath = Root, ApplicationRootPath = Root, ApplicationPoolName = "search.test" };
            Service = new FileService(sites) { OpenSearchContent = OpenObserved };
        }
        public Stream OpenObserved(string path) { OpenCount++; return LastStream = new ObservedFile(path); }
        public Task<FileSearchResultDto> Search(CancellationToken token = default) =>
            Service.SearchMeasuredAsync(new SearchFilesRequest { Path = "sites/search.test", Query = "needle" }, token);
        public void Dispose() { LastStream?.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    private sealed class ObservedFile(string path) : Stream
    {
        private readonly FileStream inner = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        public long ReadBytes;
        public bool Disposed;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) { var read = inner.Read(buffer, offset, count); ReadBytes += read; return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { var read = await inner.ReadAsync(buffer, token); ReadBytes += read; return read; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); Disposed = true; } base.Dispose(disposing); }
    }
    public class Proxy : DispatchProxy
    {
        public SiteDto Site { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name == nameof(IIisSiteService.GetSiteAsync)
            ? Task.FromResult(Site) : throw new NotSupportedException(method.Name);
    }
}
