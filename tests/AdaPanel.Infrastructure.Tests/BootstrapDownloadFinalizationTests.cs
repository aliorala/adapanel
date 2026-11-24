using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Bootstrap;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class BootstrapDownloadFinalizationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-download-finalize-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public async Task Locked_download_destination_reaches_fallback_and_reports_file_locked_error()
    {
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, "package.zip");
        var temporary = destination + ".part";
        await File.WriteAllBytesAsync(destination, [1, 2]);
        await File.WriteAllBytesAsync(temporary, [3, 4]);
        // Local fixture prevents replacement, rename and copy; no real package is used.
        using var held = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
        var error = await Assert.ThrowsAsync<AppException>(() =>
            ComponentBootstrapper.FinalizeDownloadAsync(temporary, destination, CancellationToken.None));
        Assert.Equal("software.install_file_locked", error.MessageKey);
        Assert.Equal(423, error.HttpStatusCode);
        Assert.Equal(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(destination));
        Assert.True(File.Exists(temporary));
    }

    [Fact]
    public async Task Normal_finalization_replaces_destination_with_downloaded_bytes()
    {
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, "package.zip");
        var temporary = destination + ".part";
        await File.WriteAllBytesAsync(destination, [1, 2]);
        await File.WriteAllBytesAsync(temporary, [3, 4]);
        await ComponentBootstrapper.FinalizeDownloadAsync(temporary, destination, CancellationToken.None);
        Assert.Equal(new byte[] { 3, 4 }, await File.ReadAllBytesAsync(destination));
        Assert.False(File.Exists(temporary));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
