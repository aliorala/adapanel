using AdaPanel.Core.Dtos.Monitoring;
using AdaPanel.Infrastructure.Monitoring;
using AdaPanel.Infrastructure.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class QueryLogWriteOutcomeTests
{
    [Fact]
    public async Task FailedAppendDoesNotReportAPersistedQuery()
    {
        var root = Path.Combine(Path.GetTempPath(), "adapanel-query-write-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        // A file where the query directory should be guarantees an IO failure without permissions or transport.
        await File.WriteAllTextAsync(Path.Combine(root, "logs", "db-queries"), "fixture blocker");
        var service = new QueryLogService(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }), NullLogger<QueryLogService>.Instance);
        ResourceConsumerSnapshotDto Sample(params ResourceConsumerDto[] consumers) => new()
        { Source = "fixture", Available = true, Installed = true, TotalCount = consumers.Length, Consumers = consumers };
        try
        {
            Assert.Equal(0, await service.RecordSampleAsync(Sample(new ResourceConsumerDto() { ResourceType = "database", ConnectionId = "1", Statement = "SELECT 1" })));
            Assert.Equal(0, await service.RecordSampleAsync(Sample()));
            Assert.Empty((await service.QueryAsync(new())).Entries);
        }
        finally { Directory.Delete(root, true); }
    }
}
