using System.Reflection;
using System.Text;
using System.Xml.Linq;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class HostingAtomicConfigurationTests
{
    private static readonly byte[] Original = Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<configuration><!-- preserve exact bytes --><appSettings><add key=\"secret\" value=\"private\" /></appSettings></configuration>\r\n");

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task InterruptedHostingXmlCannotPublishPartialConfigurationOrSavedSettings(bool exists, bool canceled)
    {
        using var f = new Fixture(exists);
        InterruptedXmlWriteStream? interrupted = null;
        var service = f.Service(stream => interrupted = new InterruptedXmlWriteStream(stream, canceled));

        var error = await Record.ExceptionAsync(() => service.UpdateAsync("site.test", new UpdateSiteHostingSettingsRequest()));

        if (canceled) Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.IsType<AppException>(error);
        Assert.NotNull(interrupted);
        Assert.True(interrupted.BytesWritten > 0);
        Assert.Equal(f.OriginalState, File.ReadAllBytes(f.State));
        if (exists) Assert.Equal(Original, File.ReadAllBytes(f.Configuration));
        else Assert.False(File.Exists(f.Configuration));
        Assert.Equal(exists ? new[] { "web.config" } : [], Directory.GetFiles(f.SiteRoot).Select(Path.GetFileName).ToArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CompletedHostingXmlStillHasVerifiedContentAndNoTemporaryFile(bool exists)
    {
        using var f = new Fixture(exists);
        var probes = 0;

        var result = SiteHostingSettingsService.UpdateWebConfig(f.SiteRoot, new UpdateSiteHostingSettingsRequest(), () => { probes++; return true; });

        Assert.True(result.Verified);
        Assert.True(result.IisVerified);
        Assert.Equal(1, probes);
        var document = XDocument.Load(f.Configuration);
        if (exists) Assert.Equal("private", document.Root!.Element("appSettings")!.Element("add")!.Attribute("value")!.Value);
        Assert.Equal(new[] { "web.config" }, Directory.GetFiles(f.SiteRoot).Select(Path.GetFileName).ToArray());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "AdaPanel.HostingXml." + Guid.NewGuid().ToString("N"));
        public string SiteRoot => Path.Combine(root, "site");
        private string DataRoot => Path.Combine(root, "data");
        public string Configuration => Path.Combine(SiteRoot, "web.config");
        public string State => Path.Combine(DataRoot, "config", "site-hosting-settings.json");
        public byte[] OriginalState { get; } = Encoding.UTF8.GetBytes("[{\"siteName\":\"existing.test\"}]");
        public Fixture(bool exists)
        {
            Directory.CreateDirectory(SiteRoot); Directory.CreateDirectory(Path.GetDirectoryName(State)!);
            File.WriteAllBytes(State, OriginalState);
            if (exists) File.WriteAllBytes(Configuration, Original);
        }
        public SiteHostingSettingsService Service(Func<Stream, Stream> configureStream)
        {
            var options = Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = DataRoot });
            var iis = Fake<IIisSiteService>((method, _) => method.Name == "GetSiteAsync"
                ? Task.FromResult(new SiteDto { Name = "site.test", PhysicalPath = SiteRoot })
                : throw new InvalidOperationException("Unexpected native IIS call: " + method.Name));
            return new SiteHostingSettingsService(iis, Fake<IComponentBootstrapper>(), Fake<ISchedulerService>(),
                options, Fake<IConcurrencyLock>(), new JsonFileStore(options)) { ConfigurationStream = configureStream };
        }
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HostingCancellationAfterSerializationDoesNotPublishConfiguration(bool exists)
    {
        using var f = new Fixture(exists);
        using var canceled = new CancellationTokenSource();
        var service = f.Service(stream => new ObservedXmlWriteStream(stream, canceled.Cancel));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.UpdateAsync("site.test", new UpdateSiteHostingSettingsRequest(), canceled.Token));

        Assert.Equal(f.OriginalState, File.ReadAllBytes(f.State));
        if (exists) Assert.Equal(Original, File.ReadAllBytes(f.Configuration));
        else Assert.False(File.Exists(f.Configuration));
        Assert.Empty(Directory.GetFiles(f.SiteRoot, ".adapanel-config-*"));
    }

    private static T Fake<T>(Func<MethodInfo, object?[], object?>? invoke = null) where T : class
    {
        var value = DispatchProxy.Create<T, Proxy>();
        ((Proxy)(object)value).Call = invoke ?? ((method, _) => throw new InvalidOperationException("Unexpected dependency: " + method.Name));
        return value;
    }
    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Call { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Call(targetMethod!, args ?? []);
    }
}
