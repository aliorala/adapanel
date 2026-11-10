using System.Reflection;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Migration.Packages;
using AdaPanel.Infrastructure.Migration.Sources;
using AdaPanel.Infrastructure.Options;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class MigrationStoragePolicyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ada-migration-root-policy-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task NativeInventoryFailureDoesNotCreatePackageDirectory()
    {
        var store = new PortableMigrationPackageStore(Runtime(), Limits(), Sites(() => throw new IOException("inventory failed")));
        Assert.False(Directory.Exists(root));
        await Assert.ThrowsAsync<IOException>(() => store.UploadAsync(new MemoryStream()));
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HiddenSiteDocumentAndApplicationRootsBlockStorage(bool applicationRoot)
    {
        var sites = Sites(() => [new() { Name = "internal-tool", PhysicalPath = applicationRoot ? Path.Combine(root, "public") : root,
            ApplicationRootPath = applicationRoot ? root : "" }]);
        var store = new PortableMigrationPackageStore(Runtime(), Limits(), sites);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.UploadAsync(new MemoryStream()));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task CurrentInventoryIsCheckedAgainForReadAndDelete()
    {
        var calls = 0;
        var store = new PortableMigrationPackageStore(Runtime(), Limits(), Sites(() => { calls++; return [new() { PhysicalPath = root }]; }));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadManifestAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.OpenPayloadAsync(Guid.NewGuid(), "a"));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.DeleteAsync(Guid.NewGuid()));
        Assert.Equal(3, calls);
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData("cpanel")]
    [InlineData("plesk")]
    public async Task NormalizerChecksNativeRootsBeforeReadingOrWriting(string provider)
    {
        var sites = Sites(() => [new() { Name = "hidden-panel", PhysicalPath = root }]);
        var unused = DispatchProxy.Create<IMigrationPackageStore, Proxy>();
        ((Proxy)(object)unused).Handler = (_, _) => throw new InvalidOperationException("Package store must not be reached");
        using var input = new MemoryStream([1, 2, 3]);
        if (provider == "cpanel")
            await Assert.ThrowsAsync<InvalidDataException>(() => new CpanelBackupNormalizer(Runtime(), Limits(), unused, sites).NormalizeAsync(input));
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => new PleskBackupNormalizer(Runtime(), Limits(), unused, sites).NormalizeAsync(input, "tar"));
        Assert.Equal(0, input.Position);
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData("package")]
    [InlineData("cpanel")]
    [InlineData("plesk")]
    public async Task UnmeasuredNativeRootCannotBeSilentlyOmitted(string operation)
    {
        var sites = Sites(() => [new() { Name = "unmeasured-root" }]);
        var store = new PortableMigrationPackageStore(Runtime(), Limits(), sites);
        using var input = new MemoryStream([1, 2, 3]);
        if (operation == "package") await Assert.ThrowsAsync<InvalidDataException>(() => store.UploadAsync(input));
        else if (operation == "cpanel") await Assert.ThrowsAsync<InvalidDataException>(() => new CpanelBackupNormalizer(Runtime(), Limits(), store, sites).NormalizeAsync(input));
        else await Assert.ThrowsAsync<InvalidDataException>(() => new PleskBackupNormalizer(Runtime(), Limits(), store, sites).NormalizeAsync(input, "tar"));
        Assert.Equal(0, input.Position);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task ConfiguredRootsAreKeptAlongsideNativeRoots()
    {
        var actual = await MigrationStoragePolicy.PublicRootsAsync(Sites(() => [new() { PhysicalPath = Path.Combine(root, "native"),
            ApplicationRootPath = Path.Combine(root, "application") }]), [Path.Combine(root, "configured")], default);
        Assert.Equal(3, actual.Length);
        Assert.Contains(Path.Combine(root, "configured"), actual);
        Assert.Contains(Path.Combine(root, "application"), actual);
    }

    private IOptions<PanelRuntimeOptions> Runtime() => Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root });
    private static IOptions<PortableMigrationPackageOptions> Limits() => Microsoft.Extensions.Options.Options.Create(new PortableMigrationPackageOptions());
    private static IIisSiteService Sites(Func<IReadOnlyCollection<SiteDto>> get)
    {
        var proxy = DispatchProxy.Create<IIisSiteService, Proxy>();
        ((Proxy)(object)proxy).Handler = (method, _) => method.Name == nameof(IIisSiteService.GetMigrationTargetSitesAsync)
            ? Task.FromResult(get()) : throw new InvalidOperationException("Visible-site inventory is insufficient");
        return proxy;
    }
    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
    }
    public void Dispose() { if (Directory.Exists(root)) PortableMigrationStorage.DeleteOwnedDirectory(root); }
}
