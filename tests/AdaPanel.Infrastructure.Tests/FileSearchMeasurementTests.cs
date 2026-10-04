using System.Reflection;
using AdaPanel.Core.Dtos.Files;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Files;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class FileSearchMeasurementTests
{
    [Theory]
    [InlineData(4999, false)]
    [InlineData(5000, true)]
    [InlineData(5001, true)]
    public async Task AScanBudgetCannotBePresentedAsAnExhaustiveEmptySearch(int count, bool truncated)
    {
        using var f = new Fixture();
        for (var i = 0; i < count; i++) File.WriteAllBytes(Path.Combine(f.Root, $"file-{i:D5}.bin"), []);
        var result = await f.Service.SearchMeasuredAsync(new SearchFilesRequest { Path = f.VirtualPath, Query = "absent" });
        Assert.Empty(result.Items);
        Assert.Equal(Math.Min(count, 5000), result.ScannedFiles);
        Assert.Equal(truncated, result.Truncated);
        Assert.Equal(truncated ? "files.search_truncated_files" : string.Empty, result.TruncatedReasonKey);
    }

    [Theory]
    [InlineData(99, false)]
    [InlineData(100, true)]
    [InlineData(101, true)]
    public async Task AResultLimitCannotBePresentedAsTheCompleteMatchingSet(int count, bool truncated)
    {
        using var f = new Fixture();
        for (var i = 0; i < count; i++) File.WriteAllBytes(Path.Combine(f.Root, $"match-{i:D5}.bin"), []);
        var request = new SearchFilesRequest { Path = f.VirtualPath, Query = "match" };
        var result = await f.Service.SearchMeasuredAsync(request);
        Assert.Equal(Math.Min(count, 100), result.Items.Length);
        Assert.Equal(Math.Min(count, 100), result.ScannedFiles);
        Assert.Equal(truncated, result.Truncated);
        Assert.Equal(truncated ? "files.search_truncated_results" : string.Empty, result.TruncatedReasonKey);
        Assert.All(result.Items, entry => Assert.StartsWith(f.VirtualPath + "/", entry.Path));
        // Existing trusted callers retain their array contract and limit.
        Assert.Equal(result.Items.Select(item => item.Path), (await f.Service.SearchAsync(request)).Select(item => item.Path));
    }

    [Fact]
    public async Task ACompleteEmptyDirectoryStillHasAConfirmedEmptyResult()
    {
        using var f = new Fixture();
        var result = await f.Service.SearchMeasuredAsync(new SearchFilesRequest { Path = f.VirtualPath + "/", Query = " match " });
        Assert.Equal(f.VirtualPath, result.Path);
        Assert.Equal("match", result.Query);
        Assert.Empty(result.Items);
        Assert.Equal(0, result.ScannedFiles);
        Assert.False(result.Truncated);
        Assert.Empty(result.TruncatedReasonKey);
    }

    [Fact]
    public async Task ContentMatchesCountOnlyInspectedFilesAndKeepVirtualPaths()
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.Root, "alpha.txt"), "contains needle");
        File.WriteAllText(Path.Combine(f.Root, "beta.txt"), "unrelated");
        var result = await f.Service.SearchMeasuredAsync(new SearchFilesRequest { Path = f.VirtualPath, Query = "needle" });
        Assert.Equal(f.VirtualPath + "/alpha.txt", Assert.Single(result.Items).Path);
        Assert.Equal(2, result.ScannedFiles);
        Assert.False(result.Truncated);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "adapanel-search-measurement-" + Guid.NewGuid().ToString("N"));
        public string VirtualPath => "sites/search.test";
        public FileService Service { get; }
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            var site = new SiteDto { Name = "search.test", Id = 12, PhysicalPath = Root, ApplicationRootPath = Root, ApplicationPoolName = "search.test" };
            var sites = DispatchProxy.Create<IIisSiteService, Proxy>();
            ((Proxy)(object)sites).Site = site;
            Service = new FileService(sites);
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    public class Proxy : DispatchProxy
    {
        public SiteDto Site { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name == nameof(IIisSiteService.GetSiteAsync)
            ? Task.FromResult(Site) : throw new NotSupportedException(method.Name);
    }
}
