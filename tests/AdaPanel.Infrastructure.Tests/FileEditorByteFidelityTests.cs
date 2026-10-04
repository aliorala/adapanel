using System.Reflection;
using System.Text;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Files;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Files;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class FileEditorByteFidelityTests
{
    public static IEnumerable<object[]> UnicodeEncodings() =>
        from encoding in new[] { "utf8", "utf8-bom", "utf16-le", "utf16-be", "utf32-le", "utf32-be" }
        from modified in new[] { false, true }
        select new object[] { encoding, modified };

    [Theory]
    [MemberData(nameof(UnicodeEncodings))]
    public async Task ATextEditPreservesTheExistingUnicodeEncodingAndPreamble(string name, bool modified)
    {
        using var f = new Fixture();
        const string original = "Türkçe 日本語 😀\r\nsecond line\tvalue\r\n";
        const string edited = "Düzenlenmiş 你好 😀\nson\n";
        var encoding = EncodingFor(name);
        await File.WriteAllBytesAsync(f.Path, Encode(encoding, original));
        var workspace = await f.Service.GetWorkspaceAsync(f.VirtualPath);
        Assert.True(workspace.ContentLoaded);
        Assert.Equal(original, workspace.Content);
        Assert.Empty(workspace.ContentUnavailableReasonKey);
        var result = await f.Service.WriteAsync(new WriteFileRequest { Path = f.VirtualPath, Content = modified ? edited : workspace.Content });
        Assert.Equal(Encode(encoding, modified ? edited : original), await File.ReadAllBytesAsync(f.Path));
        Assert.True(result.ContentLoaded);
        Assert.Equal(modified ? edited : original, result.Content);
    }

    public static IEnumerable<object[]> NonTextBytes()
    {
        yield return new object[] { "invalid-utf8", new byte[] { 0x48, 0xc3, 0x28, 0xff } };
        yield return new object[] { "legacy-single-byte", new byte[] { 0x63, 0x61, 0x66, 0xe9 } };
        yield return new object[] { "nul-in-utf8", Encoding.UTF8.GetBytes("before\0after") };
        yield return new object[] { "binary-control", Encoding.UTF8.GetBytes("before\u0001after") };
        yield return new object[] { "odd-utf16", new byte[] { 0xff, 0xfe, 0x41 } };
        yield return new object[] { "incomplete-utf32", new byte[] { 0xff, 0xfe, 0, 0, 0x41, 0 } };
        yield return new object[] { "unpaired-utf16", new byte[] { 0xff, 0xfe, 0, 0xd8 } };
    }

    [Theory]
    [MemberData(nameof(NonTextBytes))]
    public async Task UnverifiedOrBinaryBytesAreNeverPresentedAsEditableReplacementText(string name, byte[] original)
    {
        using var f = new Fixture();
        await File.WriteAllBytesAsync(f.Path, original);
        var workspace = await f.Service.GetWorkspaceAsync(f.VirtualPath);
        Assert.False(workspace.ContentLoaded, name);
        Assert.Empty(workspace.Content);
        Assert.Equal("files.content_not_editable", workspace.ContentUnavailableReasonKey);
        Assert.Equal(original, await File.ReadAllBytesAsync(f.Path));
    }

    [Theory]
    [MemberData(nameof(NonTextBytes))]
    public async Task ADirectTextSaveCannotOverwriteAnExistingFileWhoseCodecWasNotVerified(string name, byte[] original)
    {
        using var f = new Fixture();
        await File.WriteAllBytesAsync(f.Path, original);
        var error = await Record.ExceptionAsync(() => f.Service.WriteAsync(new WriteFileRequest { Path = f.VirtualPath, Content = "replacement" }));
        Assert.True(original.SequenceEqual(await File.ReadAllBytesAsync(f.Path)), name);
        Assert.Equal("files.content_not_editable", Assert.IsType<AppException>(error).MessageKey);
    }

    [Fact]
    public async Task ANewUnicodeTextFileUsesBomlessUtf8()
    {
        using var f = new Fixture();
        const string content = "Merhaba 😀\r\n\t世界\n";
        var result = await f.Service.WriteAsync(new WriteFileRequest { Path = f.VirtualPath, Content = content, FailIfExists = true });
        Assert.Equal(new UTF8Encoding(false, true).GetBytes(content), await File.ReadAllBytesAsync(f.Path));
        Assert.True(result.ContentLoaded);
        Assert.Equal(content, result.Content);
    }

    private static Encoding EncodingFor(string name) => name switch
    {
        "utf8" => new UTF8Encoding(false, true),
        "utf8-bom" => new UTF8Encoding(true, true),
        "utf16-le" => new UnicodeEncoding(false, true, true),
        "utf16-be" => new UnicodeEncoding(true, true, true),
        "utf32-le" => new UTF32Encoding(false, true, true),
        "utf32-be" => new UTF32Encoding(true, true, true),
        _ => throw new NotSupportedException(name)
    };
    private static byte[] Encode(Encoding encoding, string text) => [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];
    private sealed class Fixture : IDisposable
    {
        private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "adapanel-editor-codec-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(root, "content.txt");
        public string VirtualPath => "sites/editor.test/content.txt";
        public FileService Service { get; }
        public Fixture()
        {
            Directory.CreateDirectory(root);
            var sites = DispatchProxy.Create<IIisSiteService, SiteProxy>();
            ((SiteProxy)(object)sites).Root = root;
            Service = new FileService(sites);
        }
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    public class SiteProxy : DispatchProxy
    {
        public string Root { get; set; } = string.Empty;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name == nameof(IIisSiteService.GetSiteAsync)
            ? Task.FromResult(new SiteDto { Name = "editor.test", Id = 18, PhysicalPath = Root, ApplicationRootPath = Root })
            : throw new NotSupportedException(method?.Name);
    }
}
