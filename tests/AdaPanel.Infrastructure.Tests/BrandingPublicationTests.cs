using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Branding;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class BrandingPublicationTests
{
    private static byte[] Png(byte marker = 1) => [0x89,0x50,0x4e,0x47,0x0d,0x0a,0x1a,0x0a,0,marker];
    private static byte[] Jpeg() => [0xff,0xd8,0xff,0xe0,0,0x10];
    private static string Url(string mime, byte[] bytes) => $"data:{mime};base64,{Convert.ToBase64String(bytes)}";

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FailedImageStateWriteKeepsPreviouslyPublishedImageAndNoCandidate(bool changedFormat, bool cancelled)
    {
        using var f = new Fixture();
        var scope = BrandingScope.Server;
        await f.Service().SetImageAsync(scope, BrandingImageKind.Logo, Png());
        var originalState = await File.ReadAllBytesAsync(f.State);
        var originalFiles = Directory.GetFiles(f.Images(scope));
        using var cts = new CancellationTokenSource(); FileStream? stateBlock = null;
        var service = f.Service(_ =>
        {
            if (cancelled) cts.Cancel();
            else stateBlock = new FileStream(f.State, FileMode.Open, FileAccess.Read, FileShare.None);
            return Task.CompletedTask;
        });
        Exception? error;
        try { error = await Record.ExceptionAsync(() => service.SetImageAsync(scope, BrandingImageKind.Logo, changedFormat ? Jpeg() : Png(2), cts.Token)); }
        finally { stateBlock?.Dispose(); }
        Assert.NotNull(error);
        if (cancelled) Assert.IsAssignableFrom<OperationCanceledException>(error); else Assert.IsAssignableFrom<IOException>(error);
        Assert.Equal(originalState, await File.ReadAllBytesAsync(f.State));
        Assert.Equal(Url("image/png", Png()), (await f.Service().GetScopeAsync(scope)).LogoDataUrl);
        Assert.Equal(originalFiles, Directory.GetFiles(f.Images(scope)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedRemovalKeepsItsActiveImage(bool cancelled)
    {
        using var f = new Fixture(); var scope = BrandingScope.Server;
        await f.Service().SetImageAsync(scope, BrandingImageKind.Logo, Png());
        var original = await File.ReadAllBytesAsync(f.State); using var cts = new CancellationTokenSource(); FileStream? stateBlock = null;
        var service = f.Service(_ => { if (cancelled) cts.Cancel(); else stateBlock = new FileStream(f.State, FileMode.Open, FileAccess.Read, FileShare.None); return Task.CompletedTask; });
        Exception? error;
        try { error = await Record.ExceptionAsync(() => service.RemoveImageAsync(scope, BrandingImageKind.Logo, cts.Token)); }
        finally { stateBlock?.Dispose(); }
        Assert.NotNull(error); Assert.Equal(original, await File.ReadAllBytesAsync(f.State));
        Assert.Equal(Url("image/png", Png()), (await f.Service().GetScopeAsync(scope)).LogoDataUrl);
    }

    [Fact]
    public async Task IndependentInstancesSerializeImageReadsSettingsAndRemovalWithinOneScope()
    {
        using var f = new Fixture(); var id = Guid.NewGuid(); var scope = new BrandingScope(id);
        await f.Service().UpdateAsync(BrandingScope.Server, new() { PanelName = "Provider" });
        await f.Service().SetImageAsync(scope, BrandingImageKind.Logo, Png());
        var entered = Signal(); var release = Signal();
        var publishing = f.Service(async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); });
        var swap = publishing.SetImageAsync(scope, BrandingImageKind.Logo, Jpeg());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var reader = f.Service();
        var own = reader.GetScopeAsync(scope);
        var effective = reader.GetEffectiveAsync(id, UserRole.Reseller);
        var settings = reader.UpdateAsync(scope, new() { PanelName = "Reseller" });
        var asset = reader.SetImageAsync(scope, BrandingImageKind.Favicon, Png(3));
        var remove = reader.RemoveImageAsync(scope, BrandingImageKind.Logo);
        Task[] blocked = [own, effective, settings, asset, remove];
        try
        {
            // A different scope can finish while this scope's publication is paused.
            await reader.UpdateAsync(new BrandingScope(Guid.NewGuid()), new() { PanelName = "Independent" });
            await Task.Delay(100);
            Assert.All(blocked, operation => Assert.False(operation.IsCompleted));
        }
        finally
        {
            release.TrySetResult();
            try { await Task.WhenAll(blocked.Append(swap)); } catch { /* original assertion or awaited result below reports the failure */ }
        }
        await Task.WhenAll(blocked.Append(swap));
        var ownResult = await own; var effectiveResult = await effective;
        Assert.Equal(Url("image/jpeg", Jpeg()), ownResult.LogoDataUrl);
        Assert.Equal("reseller", effectiveResult.Scope);
        Assert.Equal(Url("image/jpeg", Jpeg()), effectiveResult.LogoDataUrl);
        var final = await reader.GetScopeAsync(scope);
        Assert.Equal("Reseller", final.PanelName); Assert.Empty(final.LogoDataUrl);
        Assert.Equal(Url("image/png", Png(3)), final.FaviconDataUrl);
        Assert.Single(Directory.GetFiles(f.Images(scope)));
    }

    [Fact]
    public async Task SuccessfulFormatReplacementAndRemovalCleanOnlyThePreviousAsset()
    {
        using var f = new Fixture(); var service = f.Service(); var scope = BrandingScope.Server;
        await service.SetImageAsync(scope, BrandingImageKind.Favicon, Png(3));
        await service.SetImageAsync(scope, BrandingImageKind.Logo, Png());
        var result = await service.SetImageAsync(scope, BrandingImageKind.Logo, Jpeg());
        Assert.Equal(Url("image/jpeg", Jpeg()), result.LogoDataUrl); Assert.Equal(2, Directory.GetFiles(f.Images(scope)).Length);
        result = await service.RemoveImageAsync(scope, BrandingImageKind.Logo);
        Assert.Empty(result.LogoDataUrl); Assert.Equal(Url("image/png", Png(3)), result.FaviconDataUrl); Assert.Single(Directory.GetFiles(f.Images(scope)));
    }

    [Fact]
    public async Task ExistingFixedFilenameIndexIsReadableAndMigratesWithoutLeavingItsOldAsset()
    {
        using var f = new Fixture(); var scope = BrandingScope.Server;
        await f.SeedLegacy(); var legacy = Path.Combine(f.Images(scope), "logo.png");
        Assert.Equal(Url("image/png", Png()), (await f.Service().GetScopeAsync(scope)).LogoDataUrl);
        var result = await f.Service().SetImageAsync(scope, BrandingImageKind.Logo, Jpeg());
        Assert.Equal(Url("image/jpeg", Jpeg()), result.LogoDataUrl); Assert.False(File.Exists(legacy));
        Assert.Single(Directory.GetFiles(f.Images(scope)));
    }

    [Fact]
    public async Task SeparateDataRootsCanPublishTheSameServerScopeIndependently()
    {
        using var first = new Fixture(); using var second = new Fixture();
        var entered = Signal(); var release = Signal();
        var writing = first.Service(async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); }).SetImageAsync(BrandingScope.Server, BrandingImageKind.Logo, Png());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var independent = second.Service().SetImageAsync(BrandingScope.Server, BrandingImageKind.Logo, Jpeg());
        try { Assert.Equal(Url("image/jpeg", Jpeg()), (await independent.WaitAsync(TimeSpan.FromSeconds(5))).LogoDataUrl); }
        finally { release.TrySetResult(); await Task.WhenAll(writing, independent); }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-brand-publish-" + Guid.NewGuid().ToString("N"));
        private readonly JsonFileStore store;
        internal string State => store.ResolvePath("config/branding.json");
        internal string Images(BrandingScope scope) => store.ResolvePath($"branding/{scope.StorageKey}");
        internal Fixture() { Directory.CreateDirectory(root); store = new(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root })); }
        internal async Task SeedLegacy()
        {
            var directory = store.EnsureDirectory("branding/server");
            await File.WriteAllBytesAsync(Path.Combine(directory, "logo.png"), Png());
            await store.WriteAsync("config/branding.json", new Dictionary<string, object>
            { ["server"] = new { LogoFileName = "logo.png", LogoMediaType = "image/png" } });
        }
        internal BrandingService Service(Func<CancellationToken, Task>? beforeWrite = null) => new(store, null!) { BeforeImageStateWrite = beforeWrite };
        public void Dispose() => Directory.Delete(root, true);
    }
}
