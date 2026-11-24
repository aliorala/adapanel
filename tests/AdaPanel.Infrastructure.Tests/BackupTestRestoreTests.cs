using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Migration;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>Yalitilmis test geri yukleme + FTP bakim kesim gunu + kulturden bagimsiz sertifika bitisi.</summary>
public sealed class BackupTestRestoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-testrestore-" + Guid.NewGuid().ToString("N"));
    private readonly string work;
    private readonly string live;

    public BackupTestRestoreTests()
    {
        work = Path.Combine(root, "restore-test");
        live = Path.Combine(root, "live-site");
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(live);
        File.WriteAllText(Path.Combine(live, "index.php"), "LIVE");
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private string BuildZip(string name, Action<ZipArchive> fill)
    {
        var path = Path.Combine(root, name);
        using (var stream = File.Create(path))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            fill(zip);
        return path;
    }

    private static void Add(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static readonly BackupRecordDto Record = new() { Id = "b1", Name = "backup-1" };

    private Task<BackupTestRestoreResultDto> Run(string archive, long cap = BackupTestRestore.DefaultExtractCapBytes) =>
        BackupTestRestore.RunAsync(Record, archive, work, cap, CancellationToken.None);

    private const string GoodDump = "CREATE TABLE t (id int);\nINSERT INTO t VALUES (1);\n-- Dump completed on 2026-11-24\n";

    [Fact]
    public async Task AHealthyArchivePassesAndTheTemporaryFolderIsGone()
    {
        var archive = BuildZip("ok.zip", zip =>
        {
            Add(zip, "files/index.php", "<?php echo 1;");
            Add(zip, "databases/shop.sql", GoodDump);
        });

        var result = await Run(archive);

        Assert.Equal("passed", result.Outcome);
        Assert.Equal(2, result.EntryCount);
        Assert.Equal(1, result.DatabaseDumpsChecked);
        Assert.True(result.CleanedUp);
        Assert.False(result.LiveDataTouched);
        Assert.Empty(Directory.GetDirectories(work));
        Assert.Equal("LIVE", File.ReadAllText(Path.Combine(live, "index.php")));
    }

    [Fact]
    public async Task ACorruptedEntryIsDetectedByCrc()
    {
        var archive = BuildZip("corrupt.zip", zip => Add(zip, "files/a.txt", "AAAAAAAAAAAAAAAA"));
        var bytes = await File.ReadAllBytesAsync(archive);
        var at = Array.IndexOf(bytes, (byte)'A');
        bytes[at] = (byte)'B'; // stored (uncompressed) data: flip one byte, header CRC stays
        await File.WriteAllBytesAsync(archive, bytes);

        var result = await Run(archive);

        Assert.Equal("failed", result.Outcome);
        Assert.Equal(1, result.FileFailures);
        Assert.Contains(result.Issues, issue => issue.Contains("crc_mismatch", StringComparison.Ordinal));
        Assert.True(result.CleanedUp);
    }

    [Fact]
    public async Task ATruncatedDatabaseDumpFailsTheTestEvenWhenTheZipIsIntact()
    {
        var archive = BuildZip("truncated.zip", zip =>
        {
            Add(zip, "files/index.php", "x");
            Add(zip, "databases/shop.sql", "CREATE TABLE t (id int);\nINSERT INTO t VALUES (1);\n");
        });

        var result = await Run(archive);

        Assert.Equal("failed", result.Outcome);
        Assert.Equal(1, result.DatabaseDumpFailures);
        Assert.Contains(result.Issues, issue => issue.Contains("truncated_no_completion_marker", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnEmptyDumpAndAnEmptyArchiveBothFail()
    {
        var emptyDump = BuildZip("emptydump.zip", zip => Add(zip, "databases/shop.sql", "-- Dump completed\n"));
        Assert.Equal("failed", (await Run(emptyDump)).Outcome);

        var empty = BuildZip("empty.zip", _ => { });
        var result = await Run(empty);
        Assert.Equal("failed", result.Outcome);
        Assert.Contains("archive=empty", result.Issues);
    }

    [Fact]
    public async Task AZipSlipEntryIsRefusedAndNothingIsWrittenOutsideTheTemporaryFolder()
    {
        var archive = BuildZip("slip.zip", zip => Add(zip, "../../live-site/index.php", "PWNED"));

        var result = await Run(archive);

        Assert.Equal("failed", result.Outcome);
        Assert.Contains(result.Issues, issue => issue.Contains("unsafe_path", StringComparison.Ordinal));
        Assert.Equal("LIVE", File.ReadAllText(Path.Combine(live, "index.php")));
    }

    [Fact]
    public async Task BeyondTheExtractCapEntriesAreStreamVerifiedWithoutWritingToDisk()
    {
        var archive = BuildZip("big.zip", zip => Add(zip, "files/a.bin", new string('z', 4096)));

        var result = await Run(archive, cap: 100);

        Assert.Equal("passed", result.Outcome);
        Assert.Equal(0, result.ExtractedBytes);
        Assert.Equal(4096, result.StreamVerifiedBytes);
    }

    [Fact]
    public async Task ABakDumpIsReportedAsUncheckedNotAsPassedVerification()
    {
        var archive = BuildZip("bak.zip", zip => { Add(zip, "files/a", "x"); Add(zip, "databases/shop.bak", "BINARY"); });
        var result = await Run(archive);
        Assert.Equal(1, result.DatabaseDumpsUnchecked);
        Assert.Equal(0, result.DatabaseDumpsChecked);
    }

    [Fact]
    public async Task AStandaloneSqlDumpIsChecked()
    {
        var path = Path.Combine(root, "single.sql");
        await File.WriteAllTextAsync(path, "CREATE TABLE t (id int);\n");
        Assert.Equal("failed", (await Run(path)).Outcome);
        await File.WriteAllTextAsync(path, GoodDump);
        Assert.Equal("passed", (await Run(path)).Outcome);
    }

    [Fact]
    public async Task AnUnrecognisedFileIsNotTestedRatherThanPassed()
    {
        var path = Path.Combine(root, "mystery.bin");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4, 5, 6]);
        var result = await Run(path);
        Assert.Equal("not_tested", result.Outcome);
        Assert.NotEmpty(result.NotTestedReason);
    }

    // ---- FTP bakim kesim gunu (UTC ani + tek takvim kurali) ----

    [Fact]
    public void FtpMaintenanceCutoffUsesTheSharedCalendarHelperNotTheMachineClock()
    {
        var plusFourteen = TimeZoneInfo.CreateCustomTimeZone("plus14", TimeSpan.FromHours(14), "plus14", "plus14");
        // 2026-11-24 23:00 UTC is already 2026-11-25 13:00 in UTC+14.
        var nowUtc = new DateTime(2026, 11, 24, 23, 0, 0, DateTimeKind.Utc);

        var cutoff = BackupFtpFolderMaintenance.ResolveCutoff(nowUtc, 5, plusFourteen);

        // Same boundary the local/FTP day sweeps use: expired through (local today - 5) = 11-20; cutoff is exclusive.
        Assert.Equal(new DateTime(2026, 11, 21), cutoff);
        Assert.Equal(BackupRetentionCalendar.GetExpiredThroughLocalDate(nowUtc, 5, plusFourteen)!.Value.AddDays(1), cutoff);
    }

    [Fact]
    public void FtpMaintenanceRefusesANonPositiveRetention()
    {
        Assert.Throws<InvalidOperationException>(() => BackupFtpFolderMaintenance.ResolveCutoff(DateTime.UtcNow, 0));
    }

    // ---- Sertifika bitisi: kulturden bagimsiz ----

    [Theory]
    [InlineData("tr-TR")]
    [InlineData("ar-SA")]
    [InlineData("th-TH")]
    [InlineData("en-US")]
    public void CertificateExpiryDoesNotDependOnTheCurrentCulture(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=expired.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var expired = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(-10));
            using var future = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(10), DateTimeOffset.UtcNow.AddDays(30));

            Assert.True(RemoteCertificateTrustGate.CertificateExpiryPassed(expired, DateTime.UtcNow));
            Assert.False(RemoteCertificateTrustGate.CertificateExpiryPassed(future, DateTime.UtcNow));
            // A plain X509Certificate (not the typed subclass) takes the same culture-free path.
            Assert.True(RemoteCertificateTrustGate.CertificateExpiryPassed(new X509Certificate(expired.RawData), DateTime.UtcNow));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
