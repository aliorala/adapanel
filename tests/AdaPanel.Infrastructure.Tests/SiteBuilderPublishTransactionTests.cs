using System.Reflection;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.SiteBuilder;
using AdaPanel.Infrastructure.SiteBuilder;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class SiteBuilderPublishTransactionTests
{
    [Fact]
    public async Task Second_file_failure_restores_original_index_and_css()
    {
        await using var env = await SiteBuilderTestOrtami.KurAsync();
        await Save(env);
        var index = Path.Combine(env.SiteKoku, "index.html");
        var css = Path.Combine(env.SiteKoku, "assets", "adapanel", "site.css");
        Directory.CreateDirectory(Path.GetDirectoryName(css)!);
        await File.WriteAllTextAsync(index, "ORIGINAL_INDEX");
        await File.WriteAllTextAsync(css, "ORIGINAL_CSS");
        using (var locked = new FileStream(css, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = await Record.ExceptionAsync(() => Publish(env));
            Assert.True(failure is IOException or UnauthorizedAccessException);
        }
        Assert.Equal("ORIGINAL_INDEX", await File.ReadAllTextAsync(index));
        Assert.Equal("ORIGINAL_CSS", await File.ReadAllTextAsync(css));
        Assert.False((await env.Servis.GetProjectAsync(SiteBuilderTestOrtami.SiteAdi)).Published);
        Assert.Empty(Directory.GetFiles(env.SiteKoku, "*.backup", SearchOption.AllDirectories));
        var retry = await Publish(env);
        Assert.Equal(2, retry.WrittenFiles.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Metadata_failure_or_cancellation_rolls_back_and_allows_clean_retry(bool cancel)
    {
        await using var env = await SiteBuilderTestOrtami.KurAsync();
        await Save(env);
        var index = Path.Combine(env.SiteKoku, "index.html");
        await File.WriteAllTextAsync(index, "ORIGINAL_INDEX");
        using var cts = new CancellationTokenSource();
        SetReplacement(env.Depo, (_, _) =>
        {
            if (cancel) { cts.Cancel(); throw new OperationCanceledException(cts.Token); }
            throw new IOException("SYNTHETIC_METADATA_FAILURE");
        });
        await Assert.ThrowsAnyAsync<Exception>(() => env.Servis.PublishAsync(SiteBuilderTestOrtami.SiteAdi,
            new PublishSiteBuilderRequest { OverwriteExistingFiles = true }, cts.Token));
        Assert.Equal("ORIGINAL_INDEX", await File.ReadAllTextAsync(index));
        Assert.False(File.Exists(Path.Combine(env.SiteKoku, "assets", "adapanel", "site.css")));
        var project = await env.Servis.GetProjectAsync(SiteBuilderTestOrtami.SiteAdi);
        Assert.False(project.Published);
        Assert.Empty(project.ManagedFiles);
        SetReplacement(env.Depo, null);
        var retry = await Publish(env);
        Assert.Equal(2, retry.WrittenFiles.Count);
        Assert.True((await env.Servis.GetProjectAsync(SiteBuilderTestOrtami.SiteAdi)).Published);
    }

    [Fact]
    public async Task External_edit_is_preserved_and_failed_rollback_reports_affected_file_and_private_recovery()
    {
        await using var env = await SiteBuilderTestOrtami.KurAsync();
        await Save(env);
        var index = Path.Combine(env.SiteKoku, "index.html");
        await File.WriteAllTextAsync(index, "ORIGINAL_INDEX");
        SetReplacement(env.Depo, (_, _) =>
        {
            File.WriteAllText(index, "EXTERNAL_EDIT");
            throw new IOException("SYNTHETIC_METADATA_FAILURE");
        });
        var error = await Assert.ThrowsAsync<AppException>(() => Publish(env));
        Assert.Equal("sitebuilder.publish_rollback_incomplete", error.MessageKey);
        Assert.Contains("index.html", error.Details);
        Assert.Equal("EXTERNAL_EDIT", await File.ReadAllTextAsync(index));
        var recoveryRoot = env.Depo.ResolvePath("config/sitebuilder-recovery");
        Assert.NotEmpty(Directory.GetFiles(recoveryRoot, "*.backup", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(env.SiteKoku, "*.backup", SearchOption.AllDirectories));
        Assert.False((await env.Servis.GetProjectAsync(SiteBuilderTestOrtami.SiteAdi)).Published);
    }

    [Fact]
    public async Task Metadata_commit_then_error_restores_both_metadata_and_public_files()
    {
        await using var env = await SiteBuilderTestOrtami.KurAsync();
        await Save(env);
        var index = Path.Combine(env.SiteKoku, "index.html");
        await File.WriteAllTextAsync(index, "ORIGINAL_INDEX");
        SetReplacement(env.Depo, (source, target) =>
        {
            File.Move(source, target, overwrite: true);
            throw new IOException("COMMIT_RESULT_UNKNOWN");
        });
        await Assert.ThrowsAnyAsync<Exception>(() => Publish(env));
        Assert.Equal("ORIGINAL_INDEX", await File.ReadAllTextAsync(index));
        var project = await env.Servis.GetProjectAsync(SiteBuilderTestOrtami.SiteAdi);
        Assert.False(project.Published);
        Assert.Empty(project.ManagedFiles);
    }

    [Fact]
    public async Task Positive_control_publishes_files_and_metadata_then_removes_recovery_bytes()
    {
        await using var env = await SiteBuilderTestOrtami.KurAsync();
        await Save(env);
        var result = await Publish(env);
        Assert.Equal(2, result.WrittenFiles.Count);
        Assert.Contains("TRANSACTION_FIXTURE", await File.ReadAllTextAsync(Path.Combine(env.SiteKoku, "index.html")));
        Assert.True(File.Exists(Path.Combine(env.SiteKoku, "assets", "adapanel", "site.css")));
        var project = await env.Servis.GetProjectAsync(SiteBuilderTestOrtami.SiteAdi);
        Assert.True(project.Published);
        Assert.Equal(2, project.ManagedFiles.Count);
        var recoveryRoot = env.Depo.ResolvePath("config/sitebuilder-recovery");
        Assert.True(!Directory.Exists(recoveryRoot) || Directory.GetDirectories(recoveryRoot).Length == 0);
    }

    private static Task Save(SiteBuilderTestOrtami env) => env.Servis.SaveProjectAsync(SiteBuilderTestOrtami.SiteAdi,
        new SaveSiteBuilderProjectRequest { SiteTitle = "TRANSACTION_FIXTURE" });
    private static Task<SiteBuilderPublishResultDto> Publish(SiteBuilderTestOrtami env) => env.Servis.PublishAsync(
        SiteBuilderTestOrtami.SiteAdi, new PublishSiteBuilderRequest { OverwriteExistingFiles = true });
    private static void SetReplacement(JsonFileStore store, Action<string, string>? replacement) =>
        typeof(JsonFileStore).GetProperty("AtomicReplaceOverride", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(store, replacement);
}
