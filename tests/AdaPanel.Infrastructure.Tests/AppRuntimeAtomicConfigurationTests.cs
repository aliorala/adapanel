using System.Xml.Linq;
using AdaPanel.Core.Common;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed partial class AppRuntimeLifecycleTests
{
    public static IEnumerable<object[]> InterruptedRuntimeXmlWrites() =>
        from runtime in new[] { "node", "python" }
        from exists in new[] { true, false }
        from canceled in new[] { false, true }
        select new object[] { runtime, exists, canceled };

    [Theory]
    [MemberData(nameof(InterruptedRuntimeXmlWrites))]
    public async Task InterruptedXmlPublicationPreservesTheWorkingFileAndPoolWithoutSuccess(string runtime, bool exists, bool canceled)
    {
        using var f = await Fixture.Create();
        var original = File.ReadAllBytes(f.WebConfig);
        if (!exists) File.Delete(f.WebConfig);
        InterruptedXmlWriteStream? interrupted = null;
        f.ConfigureXmlStream = stream => interrupted = new InterruptedXmlWriteStream(stream, canceled);

        var error = await Capture(() => f.Invoke(runtime));

        if (canceled) Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.IsType<AppException>(error);
        Assert.NotNull(interrupted);
        Assert.True(interrupted.BytesWritten > 0, "The real XML serializer must write bytes before the injected failure.");
        Assert.Equal("v4.0", f.Pools.Single().ManagedRuntimeVersion);
        Assert.Empty(f.Audits);
        if (exists) Assert.Equal(original, File.ReadAllBytes(f.WebConfig));
        else Assert.False(File.Exists(f.WebConfig), "An interrupted first configuration must not publish a partial web.config.");
        Assert.Equal(exists ? new[] { "index.html", "web.config" } : ["index.html"],
            Directory.GetFiles(f.SitePath).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    [Theory]
    [InlineData("node", true)]
    [InlineData("node", false)]
    [InlineData("python", true)]
    [InlineData("python", false)]
    public async Task CompleteXmlPublicationStillConfiguresRuntimeAndAuditsSuccess(string runtime, bool exists)
    {
        using var f = await Fixture.Create();
        if (!exists) File.Delete(f.WebConfig);

        var result = await f.Invoke(runtime);

        Assert.True(result.IsEnabled);
        Assert.Equal(string.Empty, f.Pools.Single().ManagedRuntimeVersion);
        Assert.Single(f.Audits);
        var document = XDocument.Load(f.WebConfig);
        Assert.NotNull(document.Root!.Element("system.webServer")!.Element("httpPlatform"));
        if (exists) Assert.Equal("keep", document.Root!.Element("appSettings")!.Element("add")!.Attribute("value")!.Value);
        Assert.Equal(new[] { "index.html", "web.config" },
            Directory.GetFiles(f.SitePath).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    private sealed partial class Fixture
    {
        public Func<Stream, Stream>? ConfigureXmlStream;
    }

    [Theory]
    [InlineData("node")]
    [InlineData("python")]
    public async Task CancellationAfterXmlSerializationStillPreservesConfigAndRestoresPool(string runtime)
    {
        using var f = await Fixture.Create();
        using var canceled = new CancellationTokenSource();
        var original = File.ReadAllBytes(f.WebConfig);
        f.ConfigureXmlStream = stream => new ObservedXmlWriteStream(stream, canceled.Cancel);

        Assert.IsAssignableFrom<OperationCanceledException>(await Capture(() => f.Invoke(runtime, canceled.Token)));

        Assert.Equal(original, File.ReadAllBytes(f.WebConfig));
        Assert.Equal("v4.0", f.Pools.Single().ManagedRuntimeVersion);
        Assert.Empty(f.Audits);
        Assert.Empty(Directory.GetFiles(f.SitePath, ".adapanel-config-*"));
    }
}
