using System.Reflection;
using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.System;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Sunucu geneli durum bilgisinin kiracıya SIZMAMASINI sabitler.
/// </summary>
/// <remarks>
/// <para>
/// Ölçülen asimetri: MCP aracı <c>system_overview</c> yönetici istiyordu (bilinçli), REST ikizi
/// <c>GET /api/v1/system/overview</c> ise yalnız <c>[Authorize]</c> taşıyordu — yani her role
/// açıktı. Müşteri, sunucunun makine adını, işletim sistemini, çekirdek sayısını, belleğini,
/// disk doluluğunu ve ağ arabirimlerini okuyabiliyordu. Aynı yeteneğin iki kapısı varsa ikisi de
/// aynı kilidi taşımalıdır.
/// </para>
/// <para>
/// İkinci sabit: müşteri panosu KIRILMAMALI. Uç 403 dönseydi pano hiç yüklenmezdi (çağrı
/// <c>await</c> zincirinin içinde); bu yüzden yetki değil GÖVDE daraltıldı ve şema korundu.
/// </para>
/// </remarks>
public sealed class SystemOverviewScopeTests
{
    private static SystemOverviewDto TamOlcum() => new()
    {
        MachineName = "VM-185-7-242-225",
        OperatingSystem = "Microsoft Windows Server 2022 Standard",
        OsArchitecture = "X64",
        FrameworkDescription = ".NET 8.0.11",
        PanelVersion = "0.5.0",
        CurrentServerTimeUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        BootTimeUtc = new DateTime(2025, 12, 1, 0, 0, 0, DateTimeKind.Utc),
        Uptime = TimeSpan.FromDays(32),
        CpuUsagePercent = 71.5,
        LogicalProcessorCount = 16,
        TotalMemoryBytes = 68_719_476_736,
        AvailableMemoryBytes = 8_589_934_592,
        UsedMemoryBytes = 60_129_542_144,
        MemoryUsagePercent = 87.5,
        Disks = [new DiskUsageDto { Name = "C:\\", TotalBytes = 500, FreeBytes = 20, UsedBytes = 480, UsagePercent = 96 }],
        NetworkInterfaces = [new NetworkInterfaceDto { Name = "Ethernet", TotalBytesReceived = 1, TotalBytesSent = 2 }]
    };

    // --- 1) Kiracı sunucu geneli hiçbir şey görmemeli ---

    [Fact]
    public void Musteri_sunucunun_kimligini_ve_kapasitesini_GOREMEZ()
    {
        var daraltilmis = SystemOverviewScope.ForRole(TamOlcum(), isAdministrator: false);

        Assert.Equal(string.Empty, daraltilmis.MachineName);
        Assert.Equal(string.Empty, daraltilmis.OperatingSystem);
        Assert.Equal(string.Empty, daraltilmis.OsArchitecture);
        Assert.Equal(string.Empty, daraltilmis.FrameworkDescription);
        Assert.Equal(0, daraltilmis.LogicalProcessorCount);
        Assert.Equal(0, daraltilmis.TotalMemoryBytes);
        Assert.Equal(0, daraltilmis.AvailableMemoryBytes);
        Assert.Equal(0, daraltilmis.UsedMemoryBytes);
        Assert.Equal(0, daraltilmis.CpuUsagePercent);
        Assert.Equal(0, daraltilmis.MemoryUsagePercent);
        Assert.Equal(default, daraltilmis.BootTimeUtc);
        Assert.Equal(default, daraltilmis.Uptime);
        Assert.Empty(daraltilmis.Disks);
        Assert.Empty(daraltilmis.NetworkInterfaces);
    }

    /// <summary>
    /// SAYIM DEĞİL KURAL: DTO'ya sonradan eklenen her alan varsayılan değerinde kalmalı. Alanları
    /// tek tek "temizlenecekler listesine" eklemek, unutmayı "sızdır" olarak cezalandırırdı —
    /// bu depoda gizli giriş yolu muafiyetinde tam olarak bu yaşandı.
    /// </summary>
    [Fact]
    public void Daraltilmis_gorunumde_ACIKCA_izin_verilenler_disinda_HICBIR_alan_dolu_degil()
    {
        var izinliler = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(SystemOverviewDto.PanelVersion),
            nameof(SystemOverviewDto.CurrentServerTimeUtc),
            nameof(SystemOverviewDto.IsRestricted)
        };

        var daraltilmis = SystemOverviewScope.ForRole(TamOlcum(), isAdministrator: false);
        var bos = new SystemOverviewDto();

        foreach (var property in typeof(SystemOverviewDto).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (izinliler.Contains(property.Name))
            {
                continue;
            }

            var deger = property.GetValue(daraltilmis);
            var varsayilan = property.GetValue(bos);

            if (deger is System.Collections.IEnumerable liste and not string)
            {
                Assert.Empty(liste.Cast<object>());
                continue;
            }

            Assert.Equal(varsayilan, deger);
        }
    }

    /// <summary>
    /// Sıfırlar bir ÖLÇÜM DEĞİLDİR. Bayrak olmasaydı arayüz "sunucu %0 yüklü, disk yok" diye
    /// yalan söylerdi; panelin bilmediğini söyleyebilmesi gerekir.
    /// </summary>
    [Fact]
    public void Daraltilmis_yanit_daraltildigini_SOYLER()
    {
        Assert.True(SystemOverviewScope.ForRole(TamOlcum(), isAdministrator: false).IsRestricted);
        Assert.False(SystemOverviewScope.ForRole(TamOlcum(), isAdministrator: true).IsRestricted);
    }

    // --- 2) Yönetici hiçbir şey kaybetmemeli ---

    [Fact]
    public void Yonetici_TAM_olcumu_almaya_devam_eder()
    {
        var tam = TamOlcum();

        Assert.Same(tam, SystemOverviewScope.ForRole(tam, isAdministrator: true));
    }

    // --- 3) Müşteri panosu KIRILMAMALI ---

    /// <summary>
    /// Pano bu ucu girişte ve 15 saniyede bir çağırıyor, üstelik <c>await</c> zincirinin
    /// ortasında: 403 dönmek panonun tamamını yükleyemez hâle getirirdi. Müşteri yanıtı
    /// BAŞARILI olmalı ve şeması değişmemeli.
    /// </summary>
    [Fact]
    public async Task Musteri_panosu_yine_BASARILI_yanit_alir()
    {
        var sonuc = await Denetleyici("Customer").GetOverviewAsync(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(sonuc.Result);
        var zarf = Assert.IsType<ApiResponse<SystemOverviewDto>>(ok.Value);

        Assert.True(zarf.Success);
        Assert.NotNull(zarf.Data);
        Assert.True(zarf.Data!.IsRestricted);
        // Koleksiyonlar NULL DEĞİL boş: arayüz bunları koşulsuz geziyor, null olsa çökerdi.
        Assert.NotNull(zarf.Data.Disks);
        Assert.NotNull(zarf.Data.NetworkInterfaces);
        // Panel sürümü ve sunucu saati kiracıya kalır (panonun gösterdiği zararsız alanlar).
        Assert.Equal("0.5.0", zarf.Data.PanelVersion);
        Assert.NotEqual(default, zarf.Data.CurrentServerTimeUtc);
    }

    [Fact]
    public async Task Yonetici_uctan_TAM_olcumu_alir()
    {
        var sonuc = await Denetleyici("Administrator").GetOverviewAsync(CancellationToken.None);

        var zarf = Assert.IsType<ApiResponse<SystemOverviewDto>>(Assert.IsType<OkObjectResult>(sonuc.Result).Value);

        Assert.False(zarf.Data!.IsRestricted);
        Assert.Equal("VM-185-7-242-225", zarf.Data.MachineName);
        Assert.Equal(71.5, zarf.Data.CpuUsagePercent);
    }

    [Fact]
    public async Task Bayi_de_daraltilir_MCP_ikiziyle_ayni_kilit()
    {
        var sonuc = await Denetleyici("Reseller").GetOverviewAsync(CancellationToken.None);

        var zarf = Assert.IsType<ApiResponse<SystemOverviewDto>>(Assert.IsType<OkObjectResult>(sonuc.Result).Value);

        Assert.True(zarf.Data!.IsRestricted);
        Assert.Equal(string.Empty, zarf.Data.MachineName);
    }

    // --- 4) Aynı bilgiyi veren KOMŞU uçlar da kapalı olmalı ---

    /// <summary>
    /// Gövdeyi daraltıp yan kapıları açık bırakmak daraltmayı anlamsız kılardı:
    /// <c>ip-addresses</c> sunucunun bütün adreslerini, <c>history</c> ise aynı CPU/RAM
    /// ölçümünü zaman serisi hâlinde verir.
    /// </summary>
    [Theory]
    [InlineData(nameof(SystemController.GetIpAddressesAsync))]
    [InlineData(nameof(SystemController.GetHistoryAsync))]
    public void Sunucu_geneli_komsu_uclar_yalniz_yoneticiye_acik(string metotAdi)
    {
        var attribute = typeof(SystemController)
            .GetMethod(metotAdi)!
            .GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal("Administrator", attribute!.Roles);
    }

    private static SystemController Denetleyici(string rol)
    {
        var kullanici = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, rol)],
            authenticationType: "Test",
            nameType: ClaimTypes.Name,
            roleType: ClaimTypes.Role));

        return new SystemController(new SahteIzlemeServisi())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = kullanici }
            }
        };
    }

    private sealed class SahteIzlemeServisi : ISystemMonitoringService
    {
        public Task<SystemOverviewDto> GetOverviewAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(TamOlcum());

        public Task<IReadOnlyCollection<ServerIpAddressDto>> GetServerIpAddressesAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SystemHistorySampleDto>> GetHistoryAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task RecordSampleAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
