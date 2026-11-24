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
    private bool failRecycle;
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
            RecycleSitePoolAsync = (_, _) => { recycleCalls++; return failRecycle ? Task.FromException(new InvalidOperationException("recycle failed")) : Task.CompletedTask; },
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

    [Fact]
    public void Appcmd_path_is_real_has_no_control_characters_and_names_appcmd_exe()
    {
        // Sahte nesne ATLANIR: gercek yol uretimi olculur (once dosyaya gercek BEL 0x07 yazilmisti: "inetsrv<BEL>ppcmd").
        var path = (string)typeof(PhpService).GetMethod("AppcmdPath", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;

        Assert.DoesNotContain(path, ch => ch < 0x20);
        Assert.Equal("appcmd.exe", Path.GetFileName(path));
        Assert.Equal("inetsrv", Path.GetFileName(Path.GetDirectoryName(path)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("pool\"; calc")]
    [InlineData("a\u0007b")]
    public async Task Default_recycle_rejects_unsafe_pool_names_instead_of_pretending_success(string pool)
    {
        var real = new PhpService(db, null!, null!, null!, NullLogger<PhpService>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => real.RecycleSitePoolAsync(pool, CancellationToken.None));
    }

    [Fact]
    public async Task Failed_recycle_is_reported_in_the_result_not_silently_swallowed()
    {
        failRecycle = true;
        var failed = await service.UpdateSitePhpSettingsAsync("fixture.test", Request(tenant: true, openBasedir: true));
        Assert.True(failed.PoolRecycleFailed);

        failRecycle = false;
        var ok = await service.UpdateSitePhpSettingsAsync("fixture.test", Request(tenant: true, openBasedir: true));
        Assert.False(ok.PoolRecycleFailed);
    }

    [Fact]
    public void Changed_source_files_contain_no_control_bytes_besides_tab_lf_cr()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "AdaPanel.sln"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var files = new[]
        {
            "src/AdaPanel.Infrastructure/Php/PhpService.cs", "src/AdaPanel.Api/Controllers/PhpController.cs",
            "src/AdaPanel.Api/Controllers/LogsController.cs", "src/AdaPanel.Api/Controllers/SiteHostingSettingsController.cs",
            "src/AdaPanel.Api/Controllers/NotificationsController.cs", "src/AdaPanel.Infrastructure/Notifications/NotificationService.cs",
            "src/AdaPanel.Api/Security/PanelRateLimits.cs", "src/AdaPanel.Core/Dtos/Php/SitePhpSettingsDto.cs"
        };
        foreach (var file in files)
        {
            var bad = File.ReadAllBytes(Path.Combine(dir!, file)).Where(b => b < 0x20 && b != 9 && b != 10 && b != 13).ToArray();
            Assert.True(bad.Length == 0, $"{file}: kontrol bayti var (0x{(bad.Length > 0 ? bad[0] : 0):X2}).");
        }
    }

    public void Dispose()
    {
        db.Dispose(); connection.Dispose();
        try { Directory.Delete(root, true); } catch (IOException) { }
    }
}
