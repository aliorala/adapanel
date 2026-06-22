using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Logs;
using AdaPanel.Infrastructure.Options;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class LogViewerServiceTests
{
    [Fact]
    public async Task TailPanelLogAsync_ReadsLogWhileWriterKeepsFileOpen()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), $"adapanel-log-test-{Guid.NewGuid():N}");
        var logsDirectory = Path.Combine(dataRoot, "logs");
        Directory.CreateDirectory(logsDirectory);
        var logPath = Path.Combine(logsDirectory, "adapanel-test.log");

        try
        {
            await using var writerStream = new FileStream(
                logPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            await using var writer = new StreamWriter(writerStream);

            for (var index = 1; index <= 25; index++)
            {
                await writer.WriteLineAsync($"line-{index}");
            }

            await writer.FlushAsync();

            var service = new LogViewerService(
                new UnusedIisSiteService(),
                Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = dataRoot }));
            var result = await service.TailPanelLogAsync(10);

            Assert.Equal(10, result.Lines.Length);
            Assert.Equal("line-16", result.Lines.First());
            Assert.Equal("line-25", result.Lines.Last());
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    private sealed class UnusedIisSiteService : IIisSiteService
    {
        public Task<IReadOnlyCollection<SiteDto>> GetSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> CreateSiteAsync(CreateSiteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> UpdatePhysicalPathAsync(string siteName, string physicalPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> StartSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> StopSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> AddBindingAsync(string siteName, SiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> RemoveBindingAsync(string siteName, RemoveSiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<SiteDto>> ImportExistingSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
