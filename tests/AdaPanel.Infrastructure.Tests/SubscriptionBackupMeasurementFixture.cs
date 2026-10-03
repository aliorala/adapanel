using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Dtos.Ftp;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Database;
using AdaPanel.Infrastructure.Ftp;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Security;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

// Real scope/ownership JSON and real ZIPs; only provider, IIS and FTP inventory are fake.
// The real FTP implementation is used only with zero rows, which cannot start OS queries.
internal sealed class SubscriptionBackupMeasurementFixture : IDisposable
{
    public static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111118");
    public static readonly Guid Other = Guid.Parse("22222222-2222-2222-2222-222222222218");
    public const string Site = "owned.example.test";
    public const string ForeignSite = "foreign.example.test";
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "adapanel-subscription-measurement-" + Guid.NewGuid().ToString("N"));
    public JsonFileStore Store { get; }
    public CustomerScopeService Scopes { get; }
    public DatabaseOwnershipService Ownership { get; }
    public BackupService Service { get; }
    public List<(string Engine, string Name)> Exported { get; } = [];
    public int FtpReads { get; private set; }
    public Func<CancellationToken, Task<IReadOnlyCollection<FtpUserDto>>> ReadFtp { get; set; } =
        _ => Task.FromResult<IReadOnlyCollection<FtpUserDto>>([]);

    public SubscriptionBackupMeasurementFixture(bool realEmptyFtp = false)
    {
        Directory.CreateDirectory(Root);
        var options = Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Root });
        Store = new JsonFileStore(options);
        var sitePath = Path.Combine(Root, "www");
        Directory.CreateDirectory(sitePath);
        File.WriteAllText(Path.Combine(sitePath, "index.html"), "owned site contents");
        var site = new SiteDto { Id = 18, Name = Site, PhysicalPath = sitePath, ApplicationPoolName = "owned-pool" };
        var sites = Fake<IIisSiteService>((method, args) => method.Name switch
        {
            "GetSiteAsync" when string.Equals((string)args[0]!, Site, StringComparison.OrdinalIgnoreCase) => Task.FromResult(site),
            "GetSitesAsync" => Task.FromResult<IReadOnlyCollection<SiteDto>>([site]),
            _ => throw new NotSupportedException(method.Name)
        });
        var preferences = Fake<IPanelPreferencesService>((method, _) => method.Name == "GetAsync"
            ? Task.FromResult(new PanelPreferencesDto { DefaultBackupRoot = Path.Combine(Root, "backups") })
            : throw new NotSupportedException(method.Name));
        Scopes = new CustomerScopeService(Store,
            Fake<IServicePlanService>((method, _) => throw new NotSupportedException(method.Name)),
            Fake<IResellerQuotaService>((method, _) => method.Name == "GetQuotaAsync"
                ? Task.FromResult<ResellerQuotaDto?>(null) : throw new NotSupportedException(method.Name)), sites);
        Ownership = new DatabaseOwnershipService(Store);
        Scopes.SetScopeAsync(Owner, [Site], 0, 0).GetAwaiter().GetResult();
        IFtpService? realFtp = realEmptyFtp ? new FtpService(sites, Store, preferences, options) : null;
        var ftp = Fake<IFtpService>((method, args) =>
        {
            if (method.Name != "GetFtpUsersAsync") throw new NotSupportedException(method.Name);
            FtpReads++;
            return realFtp is null ? ReadFtp((CancellationToken)args[0]!) : realFtp.GetFtpUsersAsync((CancellationToken)args[0]!);
        });
        var database = Fake<IDatabaseService>((method, args) => method.Name == "ExportDumpAsync"
            ? ExportAsync((DatabaseDumpRequest)args[0]!, (CancellationToken)args[1]!)
            : throw new NotSupportedException(method.Name));
        Service = new BackupService(sites, database, null!, preferences, ftp, Store, null!,
            customerScopes: Scopes, databaseOwnership: Ownership);
    }

    public BackupSubscriptionRequest Request(bool includeFtp = false) => new()
    {
        UserId = Owner, IncludeFiles = true, IncludeDatabases = true, IncludeMail = false, IncludeFtp = includeFtp
    };

    private async Task<DatabaseDumpResultDto> ExportAsync(DatabaseDumpRequest request, CancellationToken token)
    {
        Exported.Add((request.Engine, request.DatabaseName));
        await File.WriteAllTextAsync(request.FilePath, $"-- measured {request.Engine}:{request.DatabaseName}", token);
        return new DatabaseDumpResultDto { Engine = request.Engine, DatabaseName = request.DatabaseName, FilePath = request.FilePath };
    }

    public static string[] Entries(BackupRecordDto record)
    {
        using var archive = ZipFile.OpenRead(record.Path);
        return archive.Entries.Select(entry => entry.FullName).ToArray();
    }

    public static JsonDocument ReadEntry(BackupRecordDto record, string name)
    {
        using var archive = ZipFile.OpenRead(record.Path);
        using var stream = (archive.GetEntry(name) ?? throw new InvalidOperationException(name)).Open();
        return JsonDocument.Parse(stream);
    }

    public Task<BackupRecordDto[]> Persisted() => Store.ReadAsync("config/backups.json", Array.Empty<BackupRecordDto>());

    public void Dispose()
    {
        var absolute = Path.GetFullPath(Root);
        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "adapanel-subscription-measurement-"), absolute, StringComparison.OrdinalIgnoreCase);
        Directory.Delete(absolute, true);
    }

    private static T Fake<T>(Func<MethodInfo, object?[], object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, SubscriptionMeasurementProxy>();
        ((SubscriptionMeasurementProxy)(object)proxy).Handler = handler;
        return proxy;
    }
}

public class SubscriptionMeasurementProxy : DispatchProxy
{
    public Func<MethodInfo, object?[], object?> Handler { get; set; } = null!;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args ?? []);
}
