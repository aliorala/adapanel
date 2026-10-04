using System.Reflection;
using System.Text.Json;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Files;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class FileEditorContentMeasurementTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(17, true)]
    [InlineData(1024 * 1024, true)]
    [InlineData(1024 * 1024 + 1, false)]
    public async Task EditorMetadataDistinguishesACompleteReadFromAnUnloadedFile(int length, bool loaded)
    {
        var root = Path.Combine(Path.GetTempPath(), "adapanel-editor-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "content.txt");
            var original = Enumerable.Repeat((byte)'A', length).ToArray();
            await File.WriteAllBytesAsync(path, original);
            var sites = DispatchProxy.Create<IIisSiteService, SiteProxy>();
            ((SiteProxy)(object)sites).Root = root;
            var service = new FileService(sites);
            var workspace = await service.GetWorkspaceAsync("sites/editor.test/content.txt");
            Assert.False(workspace.IsDirectory);
            Assert.Equal(loaded, workspace.ContentLoaded);
            Assert.Equal(loaded ? new string('A', length) : string.Empty, workspace.Content);
            Assert.Equal(loaded ? string.Empty : "files.content_too_large", workspace.ContentUnavailableReasonKey);
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(workspace, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            Assert.Equal(loaded, json.RootElement.GetProperty("contentLoaded").GetBoolean());
            Assert.Equal(workspace.ContentUnavailableReasonKey, json.RootElement.GetProperty("contentUnavailableReasonKey").GetString());
        }
        finally { Directory.Delete(root, true); }
    }

    public class SiteProxy : DispatchProxy
    {
        public string Root { get; set; } = string.Empty;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name == nameof(IIisSiteService.GetSiteAsync)
            ? Task.FromResult(new SiteDto { Name = "editor.test", Id = 17, PhysicalPath = Root, ApplicationRootPath = Root })
            : throw new NotSupportedException(method?.Name);
    }
}
