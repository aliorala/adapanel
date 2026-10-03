using AdaPanel.Core.Dtos.AppRuntimes;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;

namespace AdaPanel.Infrastructure.AppRuntimes;

public sealed class AppRuntimeInventoryService(AdaPanelDbContext db) : IAppRuntimeInventoryService
{
    internal Func<string, IEnumerable<string>>? AdditionalCandidates { get; init; }
    internal Func<string, bool>? Probe { get; init; }

    public async Task<IReadOnlyCollection<AppRuntimeInventoryDto>> GetAsync(CancellationToken cancellationToken = default)
    {
        var node = await AppRuntimeExecutableResolver.ResolveAsync(db, "nodejs-lts", "node", cancellationToken,
            AdditionalCandidates, Probe);
        var python = await AppRuntimeExecutableResolver.ResolveAsync(db, "python-3", "python", cancellationToken,
            AdditionalCandidates, Probe);
        return [new() { ComponentId = "nodejs-lts", IsInstalled = node is not null },
            new() { ComponentId = "python-3", IsInstalled = python is not null }];
    }
}
