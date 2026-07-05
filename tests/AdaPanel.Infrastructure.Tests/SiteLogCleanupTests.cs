using AdaPanel.Infrastructure.Automation;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class SiteLogCleanupTests
{
    [Fact]
    public void DeleteOldLogs_RemovesOnlyExpiredLogFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"adapanel-logclean-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "sub"));

        try
        {
            var oldLog = Path.Combine(root, "u_ex200101.log");
            var freshLog = Path.Combine(root, "u_ex991231.log");
            var oldNested = Path.Combine(root, "sub", "u_ex200102.log");
            var oldButNotLog = Path.Combine(root, "notes.txt");
            File.WriteAllText(oldLog, "eski");
            File.WriteAllText(freshLog, "yeni");
            File.WriteAllText(oldNested, "eski-alt");
            File.WriteAllText(oldButNotLog, "log degil");

            var past = DateTime.UtcNow.AddDays(-40);
            File.SetLastWriteTimeUtc(oldLog, past);
            File.SetLastWriteTimeUtc(oldNested, past);
            File.SetLastWriteTimeUtc(oldButNotLog, past);

            var (deleted, freed) = SiteLogCleanup.DeleteOldLogs(root, DateTime.UtcNow.AddDays(-30));

            Assert.Equal(2, deleted);
            Assert.True(freed > 0);
            Assert.False(File.Exists(oldLog));
            Assert.False(File.Exists(oldNested));
            Assert.True(File.Exists(freshLog));   // taze log korunur
            Assert.True(File.Exists(oldButNotLog)); // yalnız *.log silinir
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DeleteOldLogs_MissingDirectoryReturnsZero()
    {
        var (deleted, freed) = SiteLogCleanup.DeleteOldLogs(
            Path.Combine(Path.GetTempPath(), $"yok-{Guid.NewGuid():N}"),
            DateTime.UtcNow);

        Assert.Equal(0, deleted);
        Assert.Equal(0, freed);
    }
}
