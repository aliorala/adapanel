using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Onay hata başına DEĞİL kurulumda bir kez alındığı için, maskelemenin gerçekten
/// çalıştığı kanıtlanmak zorundadır: buradaki testler "sızmıyor" iddiasının kendisidir.
/// </summary>
public sealed class ErrorReportSanitizerTests
{
    [Fact]
    public void Mask_RemovesPasswordsAndTokens()
    {
        const string secret = "S3cr3t!Passw0rd";
        const string token = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9";

        var masked = ErrorReportSanitizer.Mask(
            $"Login failed: password={secret}; ApiKey={token}; Authorization: Bearer {token}");

        Assert.DoesNotContain(secret, masked, StringComparison.Ordinal);
        Assert.DoesNotContain(token, masked, StringComparison.Ordinal);
        Assert.Contains("<redacted>", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void Mask_RemovesConnectionStringCredentials()
    {
        var masked = ErrorReportSanitizer.Mask(
            "Server=db-01;Database=musteri_db;User Id=root;Password=Tr0ub4dor;");

        Assert.DoesNotContain("Tr0ub4dor", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("root", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void Mask_RewritesSitePathToSymbolicRoot()
    {
        var masked = ErrorReportSanitizer.Mask(@"Cannot open C:\inetpub\wwwroot\musteri.com\x.php");

        Assert.Equal(@"Cannot open <site-root>\x.php", masked);
        Assert.DoesNotContain("musteri.com", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("inetpub", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void Mask_RewritesUserHomeAndKeepsSourceFileName()
    {
        var masked = ErrorReportSanitizer.Mask(
            @"at AdaPanel.Api.Foo.Bar() in C:\Users\aLi\Desktop\AdaPanel\src\Foo.cs:line 42");

        Assert.DoesNotContain("aLi", masked, StringComparison.Ordinal);
        Assert.Contains("<user-home>", masked, StringComparison.Ordinal);
        // Teşhis değeri korunmalı: dosya adı ve satır numarası kalır.
        Assert.Contains("Foo.cs", masked, StringComparison.Ordinal);
        Assert.Contains("line 42", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void Mask_ReducesUnknownAbsolutePathToFileName()
    {
        var masked = ErrorReportSanitizer.Mask(@"Access denied for D:\musteriler\ahmet\gizli\rapor.txt");

        Assert.DoesNotContain("musteriler", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("ahmet", masked, StringComparison.Ordinal);
        Assert.Contains(@"<path>\rapor.txt", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void Mask_HashesDomainsConsistentlyWithoutRevealingThem()
    {
        var first = ErrorReportSanitizer.Mask("Binding failed for musteri.com");
        var second = ErrorReportSanitizer.Mask("Certificate expired for musteri.com");
        var other = ErrorReportSanitizer.Mask("Binding failed for baskasite.com.tr");

        Assert.DoesNotContain("musteri.com", first, StringComparison.Ordinal);
        Assert.Contains("site-", first, StringComparison.Ordinal);

        var firstAlias = ExtractAlias(first);
        var secondAlias = ExtractAlias(second);
        var otherAlias = ExtractAlias(other);

        // Aynı alan adı hep aynı takma ada gitmeli (hatalar site bazında gruplanabilsin)...
        Assert.Equal(firstAlias, secondAlias);
        // ...farklı alan adı farklı takma ad almalı.
        Assert.NotEqual(firstAlias, otherAlias);
        Assert.DoesNotContain("baskasite", other, StringComparison.Ordinal);
    }

    [Fact]
    public void Mask_RemovesEmailAddresses()
    {
        var masked = ErrorReportSanitizer.Mask("Mailbox ahmet.yilmaz@musteri.com is over quota");

        Assert.DoesNotContain("ahmet.yilmaz", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("musteri.com", masked, StringComparison.Ordinal);
        Assert.Contains("<email>", masked, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Blocked request from 203.0.113.42")]
    [InlineData("Blocked request from 2001:db8::1")]
    [InlineData("Blocked request from ::1")]
    public void Mask_RemovesIpAddresses(string message)
    {
        var masked = ErrorReportSanitizer.Mask(message);

        Assert.Contains("<ip>", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("203.0.113.42", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("2001:db8", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void Mask_DropsQueryStringValues()
    {
        var masked = ErrorReportSanitizer.Mask("Request /api/v1/files/list?path=D&token=abc123 failed");

        Assert.DoesNotContain("abc123", masked, StringComparison.Ordinal);
        Assert.Contains("?<query>", masked, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("System.IO.FileNotFoundException")]
    [InlineData("Microsoft.EntityFrameworkCore.DbUpdateException")]
    [InlineData("AdaPanel.Infrastructure.Iis.SiteProvisioningService")]
    [InlineData("could not read config.json")]
    [InlineData("script x.php exited with code 255")]
    public void Mask_KeepsDiagnosticIdentifiersIntact(string text)
    {
        // Noktalı .NET tipleri ve dosya adları alan adı SANILMAMALI; aksi hâlde kayıt
        // teşhis değerini tamamen kaybederdi.
        Assert.Equal(text, ErrorReportSanitizer.Mask(text));
    }

    [Fact]
    public void Sanitize_LeaksNothingFromARealisticFailure()
    {
        var message =
            "Deployment of musteri.com failed: cannot copy C:\\inetpub\\wwwroot\\musteri.com\\web.config " +
            "(owner admin@musteri.com, host 203.0.113.42) using password=Hunter2 token=abc123def456";
        var stackTrace =
            "   at AdaPanel.Infrastructure.Iis.SiteProvisioningService.CopyAsync() in C:\\Users\\aLi\\src\\SiteProvisioningService.cs:line 120\n" +
            "   at AdaPanel.Api.Controllers.SitesController.Create() in C:\\Users\\aLi\\src\\SitesController.cs:line 55";

        var sanitized = ErrorReportSanitizer.Sanitize(
            "System.UnauthorizedAccessException",
            message,
            stackTrace,
            "POST /api/v1/sites/{site}");

        var combined = sanitized.Message + "\n" + sanitized.StackTrace + "\n" + sanitized.RoutePattern;

        foreach (var forbidden in new[]
                 {
                     "musteri.com", "admin@musteri.com", "203.0.113.42", "Hunter2",
                     "abc123def456", "aLi", "inetpub"
                 })
        {
            Assert.DoesNotContain(forbidden, combined, StringComparison.OrdinalIgnoreCase);
        }

        // Buna karşılık hatayı bulmaya yarayan her şey durmalı.
        Assert.Equal("System.UnauthorizedAccessException", sanitized.ExceptionType);
        Assert.Contains("SiteProvisioningService.cs", combined, StringComparison.Ordinal);
        Assert.Contains("line 120", combined, StringComparison.Ordinal);
        Assert.Contains("{site}", sanitized.RoutePattern, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(sanitized.Fingerprint));
    }

    [Fact]
    public void BuildRoutePattern_PrefersRouteTemplate()
    {
        var pattern = ErrorReportSanitizer.BuildRoutePattern(
            "post",
            "api/v1/sites/{site}/quota",
            "/api/v1/sites/musteri.com/quota");

        Assert.Equal("POST /api/v1/sites/{site}/quota", pattern);
        Assert.DoesNotContain("musteri.com", pattern, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRoutePattern_TemplatesValueSegmentsWhenRouteIsUnknown()
    {
        // Yönlendirme çözülmeden patlayan istekte şablon yoktur; değer gibi görünen
        // parçalar (alan adı, kimlik) yine de dışarı çıkmamalı.
        var pattern = ErrorReportSanitizer.BuildRoutePattern(
            "GET",
            null,
            "/api/v1/sites/musteri.com/files?path=C:\\gizli");

        Assert.Equal("GET /api/v1/sites/{value}/files", pattern);
    }

    [Fact]
    public void ComputeFingerprint_IgnoresVaryingIdentifiers()
    {
        var first = ErrorReportSanitizer.ComputeFingerprint(
            "System.InvalidOperationException", "Site 41 not found", "at Foo.Bar():line 10");
        var second = ErrorReportSanitizer.ComputeFingerprint(
            "System.InvalidOperationException", "Site 42 not found", "at Foo.Bar():line 10");

        // Sayı farkı ayrı kayıt üretirse tekilleştirme hiç işe yaramaz.
        Assert.Equal(first, second);
    }

    [Fact]
    public void ComputeFingerprint_SeparatesDifferentFailures()
    {
        var first = ErrorReportSanitizer.ComputeFingerprint(
            "System.InvalidOperationException", "Site not found", "at Foo.Bar()");
        var second = ErrorReportSanitizer.ComputeFingerprint(
            "System.NullReferenceException", "Site not found", "at Foo.Bar()");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task RecordAsync_CollapsesRepeatedFailuresIntoASingleRow()
    {
        await using var harness = await ErrorReportHarness.CreateAsync();

        // Döngüye giren bir hatayı taklit et: aynı hata 25 kez.
        for (var index = 0; index < 25; index++)
        {
            await harness.Service.RecordAsync(CreateFailure("boom"), "GET /api/v1/health", "trace-1");
        }

        var reports = await harness.Service.GetRecentAsync(50);

        Assert.Single(reports);
        Assert.Equal(25, reports.Single().OccurrenceCount);
        Assert.Equal(1, await harness.DbContext.ErrorReports.CountAsync());
    }

    [Fact]
    public async Task RecordAsync_KeepsDistinctFailuresApart()
    {
        await using var harness = await ErrorReportHarness.CreateAsync();

        await harness.Service.RecordAsync(CreateFailure("boom"), "GET /api/v1/health", "trace-1");
        await harness.Service.RecordAsync(
            new InvalidOperationException("completely different"), "GET /api/v1/health", "trace-2");

        var reports = await harness.Service.GetRecentAsync(50);

        Assert.Equal(2, reports.Count);
        Assert.All(reports, report => Assert.Equal(1, report.OccurrenceCount));
    }

    [Fact]
    public async Task RecordAsync_StoresOnlyMaskedContent()
    {
        await using var harness = await ErrorReportHarness.CreateAsync();

        var failure = CreateFailure(@"Cannot write C:\inetpub\wwwroot\musteri.com\x.php (password=Hunter2)");
        await harness.Service.RecordAsync(failure, "POST /api/v1/sites/{site}", "trace-9");

        var stored = await harness.DbContext.ErrorReports.SingleAsync();

        Assert.DoesNotContain("musteri.com", stored.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Hunter2", stored.Message, StringComparison.Ordinal);
        Assert.Contains("<site-root>", stored.Message, StringComparison.Ordinal);
        Assert.Null(stored.SentAtUtc);
        Assert.False(string.IsNullOrWhiteSpace(stored.PanelVersion));
    }

    [Fact]
    public async Task PurgeAsync_RemovesOnlySentAndExpiredReports()
    {
        await using var harness = await ErrorReportHarness.CreateAsync();

        await harness.Service.RecordAsync(CreateFailure("first"), "GET /a", "t1");
        await harness.Service.RecordAsync(new InvalidOperationException("second"), "GET /b", "t2");

        var all = await harness.DbContext.ErrorReports.ToListAsync();
        var sentAndOld = all[0];
        sentAndOld.SentAtUtc = DateTime.UtcNow.AddDays(-40);
        sentAndOld.LastSeenAtUtc = DateTime.UtcNow.AddDays(-40);
        // İkincisi eski AMA gönderilmemiş: silinmemeli, yoksa geliştirici o hatayı hiç görmez.
        all[1].LastSeenAtUtc = DateTime.UtcNow.AddDays(-90);
        await harness.DbContext.SaveChangesAsync();

        var removed = await harness.Service.PurgeAsync();

        Assert.Equal(1, removed);
        var remaining = await harness.DbContext.ErrorReports.SingleAsync();
        Assert.Null(remaining.SentAtUtc);
    }

    [Fact]
    public async Task MarkSentAsync_MovesReportsOutOfThePendingQueue()
    {
        await using var harness = await ErrorReportHarness.CreateAsync();

        await harness.Service.RecordAsync(CreateFailure("first"), "GET /a", "t1");
        await harness.Service.RecordAsync(new InvalidOperationException("second"), "GET /b", "t2");

        var pending = await harness.Service.GetUnsentAsync(50);
        Assert.Equal(2, pending.Count);

        await harness.Service.MarkSentAsync(pending.Select(report => report.Id).ToArray());

        Assert.Empty(await harness.Service.GetUnsentAsync(50));
    }

    /// <summary>Gerçek yığın izi olan bir hata üretir (aynı satırdan atıldığı için kareler özdeştir).</summary>
    private static Exception CreateFailure(string message)
    {
        try
        {
            throw new InvalidOperationException(message);
        }
        catch (InvalidOperationException exception)
        {
            return exception;
        }
    }

    private static string ExtractAlias(string masked)
    {
        var index = masked.IndexOf("site-", StringComparison.Ordinal);
        Assert.True(index >= 0, "masked text should contain a hashed site alias");
        return masked[index..].Split(' ')[0].TrimEnd('.', ',', ';');
    }

    private sealed class ErrorReportHarness : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private ErrorReportHarness(SqliteConnection connection, AdaPanelDbContext dbContext, ErrorReportService service)
        {
            this.connection = connection;
            DbContext = dbContext;
            Service = service;
        }

        public AdaPanelDbContext DbContext { get; }

        public ErrorReportService Service { get; }

        public static async Task<ErrorReportHarness> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options;
            var dbContext = new AdaPanelDbContext(options);
            await dbContext.Database.EnsureCreatedAsync();

            return new ErrorReportHarness(
                connection,
                dbContext,
                new ErrorReportService(dbContext, NullLogger<ErrorReportService>.Instance));
        }

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
