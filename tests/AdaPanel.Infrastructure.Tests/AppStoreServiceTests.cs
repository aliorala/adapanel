using AdaPanel.Infrastructure.AppStore;
using AdaPanel.Infrastructure.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class AppStoreServiceTests
{
    [Fact]
    public async Task GetTemplatesAsync_IncludesAutomatedLaravelTemplate()
    {
        var service = new AppStoreService(
            null!,
            null!,
            null!,
            null!,
            null!, // IDatabaseService (bu test yalnız GetTemplatesAsync çağırır, kullanılmaz)
            null!,
            Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions()),
            NullLogger<AppStoreService>.Instance);

        var templates = await service.GetTemplatesAsync();
        var laravel = Assert.Single(templates, template => template.Id == "laravel");

        Assert.True(laravel.Featured);
        Assert.Equal("PHP 8.3+", laravel.Runtime);
        Assert.Contains("Composer", laravel.Requirements);
        Assert.Contains("SQLite", laravel.AutomaticSetup);
    }
}
