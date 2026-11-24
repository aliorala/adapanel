using System.Reflection;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Php;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Php;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// MÜŞTERİNİN PHP AYAR KAYDI SUNUCU GENELİNE DOKUNAMAZ.
/// <para>
/// Ölçülen hata: müşteri yolu <c>OpenBasedirEnabled=true</c> ile sunucudaki BÜTÜN php-cgi süreçlerini
/// öldürüyordu (arayüz alanı her kayıtta gönderir → normal kayıt herkesin PHP'sini keser; döngüyle DoS) ve
/// <c>MaxExecutionTime</c> bütün FastCGI uygulamalarının zaman aşımını kalıcı yükseltiyordu.
/// </para>
/// </summary>
public sealed class PhpTenantSideEffectsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-php-tenant-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection connection;
    private readonly AdaPanelDbContext db;
    private int refreshCalls, recycleCalls, lockCalls;
    private readonly PhpService service;

    public PhpTenantSideEffectsTests()
    {
        Directory.CreateDirectory(root);
        connection = new SqliteConnection("Data Source=:memory:"); connection.Open();
        db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        var sites = DispatchProxy.Create<IIisSiteService, PhpSettingsPreservationTests.Proxy>();
        ((PhpSettingsPreservationTests.Proxy)(object)sites).Handler = (method, _) => method.Name == nameof(IIisSiteService.GetSiteAsync)
            ? Task.FromResult(new SiteDto { Name = "fixture.test", PhysicalPath = root, ApplicationPoolName = "pool-fixture" })
            : throw new NotSupportedException(method.Name);
        var gate = DispatchProxy.Create<IConcurrencyLock, PhpSettingsPreservationTests.Proxy>();
        ((PhpSettingsPreservationTests.Proxy)(object)gate).Handler = (_, _) => { lockCalls++; return Task.CompletedTask; };
        service = new PhpService(db, null!, gate, sites, NullLogger<PhpService>.Instance)
        {
            RefreshPhpProcessesAsync = _ => { refreshCalls++; return Task.CompletedTask; },
            RecycleSitePoolAsync = (_, _) => { recycleCalls++; return Task.CompletedTask; },
            ReadFastCgiTimeoutAsync = _ => Task.FromResult(600),
            SiteLogConfiguration = new PhpSiteLogConfiguration(_ => "test-pool", (path, _, _, apply) =>
            {
                if (apply) Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                return null;
            }, (_, _, _, _) => Task.FromResult<string?>(null)),
            SiteLogProbe = new PhpSiteLogProbe { Send = (_, _, _, _) => Task.FromResult(new PhpSiteLogProbe.Reply(503, "")) }
        };
    }

    private static UpdateSitePhpSettingsRequest Request(bool tenant, bool? openBasedir, int seconds = 120) => new()
    {
        MemoryLimit = "256M", PostMaxSize = "0", MaxExecutionTime = seconds, OpenBasedirEnabled = openBasedir, TenantScope = tenant
    };

    [Fact]
    public async Task Tenant_enabling_isolation_recycles_only_its_own_pool_never_kills_all_php_cgi()
    {
        await service.UpdateSitePhpSettingsAsync("fixture.test", Request(tenant: true, openBasedir: true));

        Assert.Equal(0, refreshCalls);
        Assert.Equal(1, recycleCalls);
    }

    [Fact]
    public async Task Tenant_save_that_does_not_change_isolation_refreshes_nothing()
    {
        // Durum zaten kapalı (PHP kurulu değil → izole değil); istek "kapalı" diyor → değişiklik YOK.
        await service.UpdateSitePhpSettingsAsync("fixture.test", Request(tenant: true, openBasedir: false));

        Assert.Equal(0, refreshCalls);
        Assert.Equal(0, recycleCalls);
    }

    [Fact]
    public async Task Administrator_path_keeps_the_server_wide_refresh()
    {
        await service.UpdateSitePhpSettingsAsync("fixture.test", Request(tenant: false, openBasedir: true));

        Assert.Equal(1, refreshCalls);
        Assert.Equal(0, recycleCalls);
    }

    [Fact]
    public async Task Tenant_never_raises_the_server_wide_FastCGI_timeout_but_administrator_does()
    {
        await service.UpdateSitePhpSettingsAsync("fixture.test", Request(tenant: true, openBasedir: null));
        var tenantLockCalls = lockCalls;
        lockCalls = 0;
        await service.UpdateSitePhpSettingsAsync("fixture.test", Request(tenant: false, openBasedir: null));

        Assert.Equal(tenantLockCalls + 1, lockCalls);
    }

    [Theory]
    [InlineData(600)]
    [InlineData(541)]
    [InlineData(0)]
    public async Task Tenant_max_execution_time_above_the_current_FastCGI_ceiling_is_rejected_with_a_clear_key(int seconds)
    {
        var error = await Assert.ThrowsAsync<AppException>(() =>
            service.UpdateSitePhpSettingsAsync("fixture.test", Request(tenant: true, openBasedir: null, seconds)));

        Assert.Equal("php.max_execution_exceeds_server", error.MessageKey);
    }

    [Fact]
    public async Task Tenant_max_execution_time_within_the_ceiling_is_accepted()
    {
        await service.UpdateSitePhpSettingsAsync("fixture.test", Request(tenant: true, openBasedir: null, seconds: 540));
    }

    [Fact]
    public void TenantScope_is_never_read_from_the_request_body()
    {
        var json = System.Text.Json.JsonSerializer.Deserialize<UpdateSitePhpSettingsRequest>(
            "{\"memoryLimit\":\"256M\",\"tenantScope\":true}",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.False(json!.TenantScope);
    }

    public void Dispose()
    {
        db.Dispose(); connection.Dispose();
        try { Directory.Delete(root, true); } catch (IOException) { }
    }
}
