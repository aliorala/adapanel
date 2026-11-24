using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Infrastructure.Automation;
using AdaPanel.Infrastructure.Backup;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class BackupRetentionExplanationTests
{
    private static readonly DateTime Now = new(2026, 10, 25, 0, 12, 47, DateTimeKind.Utc);

    [Fact]
    public void ExplicitAgeLimit_ExpiresLegacyCoverageWithoutProtectionNotes()
    {
        var old = Record("legacy", 22);
        old.IncludedComponents = null;
        old.IncludedDatabases = null;
        var current = Record("current", 1);
        var result = Plan(old, current);
        Assert.Same(old, Assert.Single(result.Selected));
        Assert.Empty(result.RetainedExpired);
    }

    [Fact]
    public void ExplicitAgeLimit_ExpiresOldPointWithRemovedComponents()
    {
        var old = Record("old", 11);
        old.IncludedComponents!.Add(new() { Kind = "config", Source = "retired.example" });
        var result = Plan(old, Record("current", 1));
        Assert.Same(old, Assert.Single(result.Selected));
        Assert.Empty(result.RetainedExpired);
    }

    [Fact]
    public void MissingNewBackup_DoesNotOverrideExplicitAgeLimit()
    {
        var result = Plan(Record("first", 22), Record("last", 11));
        Assert.Equal(2, result.Selected.Count);
        Assert.Empty(result.RetainedExpired);
    }

    [Fact]
    public void HealthyReplacement_StillDeletesOldAndDoesNotReportProtection()
    {
        var old = Record("old", 11);
        var result = Plan(old, Record("current", 1));
        Assert.Same(old, Assert.Single(result.Selected));
        Assert.Empty(result.RetainedExpired);
    }

    [Fact]
    public void ExplicitAgeLimit_OverridesMinimumCountWhileAgeDisabledRetainsIt()
    {
        var records = new[] { Record("old", 11), Record("older", 12) };
        var result = BackupPrunePlanner.Analyze(records, Now.AddDays(-5), 2, _ => true,
            BackupReplacementPolicy.CanReplace);
        Assert.Equal(2, result.Selected.Count);
        Assert.Empty(result.RetainedExpired);
        var countOnly = BackupPrunePlanner.Analyze(records, DateTime.MinValue, 2, _ => true);
        Assert.Empty(countOnly.Selected);
        Assert.Empty(countOnly.RetainedExpired);
    }

    [Fact]
    public void Summary_ReportsProtectionWithoutCallingItDeletionFailure()
    {
        var result = new BackupPruneResultDto
        {
            Removed = 9,
            RetainedExpired = 23,
            RetentionNotes = ["old.zip: unmeasured_coverage"]
        };
        var summary = AutomationService.SummarizePrune(result);
        Assert.False(summary.HasFailures);
        Assert.Contains("9 eski yedek silindi", summary.Sentence);
        Assert.Contains("23 suresi dolmus yedek", summary.Sentence);
        Assert.Contains("retention: old.zip: unmeasured_coverage", summary.TechnicalLines);
        result.RemoteDeleteFailed = 1;
        var failed = AutomationService.SummarizePrune(result);
        Assert.True(failed.HasFailures);
        Assert.Contains("uzak kopyasi SILINEMEDI", failed.Sentence);
        Assert.Contains("retention: old.zip: unmeasured_coverage", failed.TechnicalLines);
    }

    private static BackupPrunePlanner.PruneAnalysis Plan(params BackupRecordDto[] records) =>
        BackupPrunePlanner.Analyze(records, Now.AddDays(-5), 0, _ => true, BackupReplacementPolicy.CanReplace);

    private static BackupRecordDto Record(string id, int daysAgo) => new()
    {
        Id = id, Name = id + ".zip", Type = "site", Source = "example.test",
        CreatedAtUtc = Now.AddDays(-daysAgo), Status = "created", IntegrityState = BackupIntegrity.Sound,
        IncludedDatabases = [], IncludedComponents = [new() { Kind = "files", Source = "example.test" }]
    };
}
