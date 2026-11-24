using AdaPanel.Core.Dtos.Scheduler;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using AdaPanel.Infrastructure.Scheduler;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Site silindiginde ARKASINDA IZ KALMAMALI.
/// <para>
/// Cascade silme mail, veritabani, FTP, DNS, SSL, proxy, kota, musteri atamasi, DB baglari,
/// git yapilandirmasi, IIS sitesi ve dosyalari zaten temizliyordu. Ama panelin siteye BAGLI
/// tuttugu bes sey geride kaliyordu:
/// </para>
/// <list type="number">
/// <item>zamanlanmis gorevler (sitenin klasorunden calisanlar) -- TETIKLENMEYE DEVAM EDIYORLARDI</item>
/// <item>kuyruk iscisi ve Laravel zamanlayici gorevleri</item>
/// <item>barindirma ayarlari kaydi</item>
/// <item>site guvenlik durumu (izolasyon / klasor izni / hiz siniri)</item>
/// <item>korumali dizin kayitlari</item>
/// </list>
/// <para>
/// Ilk ikisi GERCEK bir iz: Windows gorevleri site gitse de calisir, her turda hata uretir ve
/// panelde hangi siteye ait olduklari anlasilamaz. Kalanlar ise AYNI ADLA acilan yeni bir sitede
/// hic uygulanmamis korumalari uygulanmis gosterirdi.
/// </para>
/// </summary>
public sealed class SiteDeletionLeftoverTests
{
    [Fact]
    public void TheCascade_ClearsEveryPanelStateBoundToTheSite()
    {
        var kaynak = OkuKaynak("src", "AdaPanel.Infrastructure", "Iis", "SiteCascadeDeletionService.cs");

        Assert.Contains("siteHostingSettingsService.RemoveSiteAsync", kaynak, StringComparison.Ordinal);
        Assert.Contains("wafService.RemoveSiteAsync", kaynak, StringComparison.Ordinal);
        Assert.Contains("directoryProtectionService.RemoveSiteAsync", kaynak, StringComparison.Ordinal);
        Assert.Contains("schedulerService.RemoveTasksUnderRootAsync", kaynak, StringComparison.Ordinal);
    }

    /// <summary>
    /// Gorev temizligi DOSYALAR SILINMEDEN once yapilmali: esleştirme gorevin komut/calisma
    /// klasoru yoluna bakar, dolayisiyla site kokunun ne oldugunu hala bilmemiz gerekir.
    /// </summary>
    [Fact]
    public void ScheduledTasksAreRemoved_BeforeTheFilesAreDeleted()
    {
        var kaynak = OkuKaynak("src", "AdaPanel.Infrastructure", "Iis", "SiteCascadeDeletionService.cs");

        var gorevler = kaynak.IndexOf("schedulerService.RemoveTasksUnderRootAsync", StringComparison.Ordinal);
        var dosyalar = kaynak.IndexOf("TryDeleteDirectory(result, \"Site dosyaları\"", StringComparison.Ordinal);

        Assert.True(gorevler > 0, "Gorev temizligi yok.");
        Assert.True(dosyalar > gorevler, "Dosyalar, gorevler kaldirilmadan ONCE siliniyor; esleştirme yol uzerinden yapildigi icin kok bilgisi kaybolur.");
    }

    /// <summary>
    /// Esleştirme YOL uzerinden yapilir ve kok-adi oneki tuzagina dusmez:
    /// "C:\sites\site1" silinirken "C:\sites\site1-evil" icindeki gorev KALMALI.
    /// </summary>
    [Theory]
    [InlineData(@"C:\sites\site1", @"C:\sites\site1\cron.bat", true)]
    [InlineData(@"C:\sites\site1", @"C:\sites\site1\alt\is.ps1", true)]
    [InlineData(@"C:\sites\site1", @"C:\sites\site1-evil\cron.bat", false)]
    [InlineData(@"C:\sites\site1", @"C:\sites\site10\cron.bat", false)]
    [InlineData(@"C:\sites\site1", @"C:\sites\baska\cron.bat", false)]
    [InlineData(@"C:\sites\site1", @"C:\Windows\System32\cmd.exe", false)]
    public void TaskMatching_UsesTheProvenPathContainmentRule(string kok, string gorevYolu, bool beklenen)
    {
        Assert.Equal(beklenen, ScriptTaskPlanner.IsInsideRoot(kok, gorevYolu));
    }

    /// <summary>
    /// Onizlemede gosterilen sayi ile gercekte silinen, AYNI kuraldan gelmeli: kullaniciya
    /// soyledigimizden farkli bir sey yapmayiz.
    /// </summary>
    [Fact]
    public void ThePreviewCount_UsesTheSameRuleAsTheDeletion()
    {
        var kaynak = OkuKaynak("src", "AdaPanel.Infrastructure", "Iis", "SiteCascadeDeletionService.cs");

        Assert.Contains("CountScheduledTasksAsync", kaynak, StringComparison.Ordinal);
        Assert.Contains("ScriptTaskPlanner.IsInsideRoot(kok, gorev.Command)", kaynak, StringComparison.Ordinal);
    }

    /// <summary>
    /// Barindirma temizligi ARKA PLAN GOREVLERINI de durdurmali: kuyruk iscisi ve Laravel
    /// zamanlayici birer Windows gorevidir, yalnizca ayar kaydini silmek yetmez.
    /// </summary>
    [Fact]
    public async Task HostingCleanup_AlsoStopsBackgroundWorkers()
    {
        using var fixture = new HostingCleanupFixture();
        await fixture.Store.WriteAsync(HostingCleanupFixture.StatePath, new SiteHostingSettingsDto[] {
            new() { SiteName = "removed", MaximumConnections = 17 },
            new() { SiteName = "retained", MaximumConnections = 29 }
        });

        await fixture.Service.RemoveSiteAsync("REMOVED");

        var remaining = Assert.Single(await fixture.Store.ReadAsync(HostingCleanupFixture.StatePath, Array.Empty<SiteHostingSettingsDto>()));
        Assert.Equal("retained", remaining.SiteName);
        Assert.Equal(29, remaining.MaximumConnections);
        Assert.Equal(new[] { "AdaQueue-REMOVED", "Laravel-REMOVED-Scheduler" }, fixture.Scheduler.Deleted);
        Assert.Empty(fixture.Scheduler.Tasks);
        Assert.Equal(2, fixture.Scheduler.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_hosting_record_stops_workers_without_creating_or_rewriting_state(bool existingFile)
    {
        using var fixture = new HostingCleanupFixture();
        if (existingFile)
        {
            await fixture.Store.WriteAsync(HostingCleanupFixture.StatePath,
                new SiteHostingSettingsDto[] { new() { SiteName = "retained", MaximumConnections = 29 } });
            File.SetLastWriteTimeUtc(fixture.StateFile, new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc));
        }
        var bytes = existingFile ? await File.ReadAllBytesAsync(fixture.StateFile) : null;
        var modified = existingFile ? File.GetLastWriteTimeUtc(fixture.StateFile) : default;

        await fixture.Service.RemoveSiteAsync("REMOVED");
        await fixture.Service.RemoveSiteAsync("REMOVED");

        Assert.Equal(existingFile, File.Exists(fixture.StateFile));
        if (existingFile)
        {
            Assert.Equal(bytes, await File.ReadAllBytesAsync(fixture.StateFile));
            Assert.Equal(modified, File.GetLastWriteTimeUtc(fixture.StateFile));
        }
        Assert.Equal(new[] { "AdaQueue-REMOVED", "Laravel-REMOVED-Scheduler", "AdaQueue-REMOVED", "Laravel-REMOVED-Scheduler" }, fixture.Scheduler.Deleted);
        Assert.Empty(fixture.Scheduler.Tasks);
        Assert.Equal(4, fixture.Scheduler.Reads);
    }

    /// <summary>
    /// Korumali dizin temizligi KAYDI siler, klasordeki dosyalara DOKUNMAZ: site dosyalari
    /// bilerek saklaniyor olabilir ve o durumda icerige mudahale etmek kullanicinin sakladigi
    /// seyi degistirmek olurdu.
    /// </summary>
    [Fact]
    public void ProtectedDirectoryCleanup_DoesNotTouchTheFilesOnDisk()
    {
        var kaynak = OkuKaynak("src", "AdaPanel.Infrastructure", "Iis", "DirectoryProtectionService.cs");
        var basla = kaynak.IndexOf("public async Task RemoveSiteAsync", StringComparison.Ordinal);
        var son = kaynak.IndexOf("public async Task UnprotectAsync", StringComparison.Ordinal);

        Assert.True(basla > 0 && son > basla, "RemoveSiteAsync bulunamadi.");

        var govde = kaynak[basla..son];
        Assert.DoesNotContain("File.Delete", govde, StringComparison.Ordinal);
    }

    /// <summary>
    /// Temizlik IDEMPOTENT olmali: kayit yoksa dosyaya dokunulmaz. Gereksiz yazma, es zamanli
    /// bir guncellemeyi ezme riski tasir ve silme akisi zaten birden cok kez calisabilir.
    /// </summary>
    [Theory]
    [InlineData("Waf", "WafService.cs")]
    [InlineData("Iis", "DirectoryProtectionService.cs")]
    public void CleanupWritesOnlyWhenSomethingWasRemoved(string klasor, string dosya)
    {
        var kaynak = OkuKaynak("src", "AdaPanel.Infrastructure", klasor, dosya);
        var basla = kaynak.IndexOf("public async Task RemoveSiteAsync", StringComparison.Ordinal);

        Assert.True(basla > 0, $"{dosya}: RemoveSiteAsync yok.");

        var govde = kaynak[basla..Math.Min(basla + 1400, kaynak.Length)];
        Assert.Contains("if (kaldirilan > 0)", govde, StringComparison.Ordinal);
    }

    private sealed class HostingCleanupFixture : IDisposable
    {
        internal const string StatePath = "config/site-hosting-settings.json";
        private readonly string root = Path.Combine(Path.GetTempPath(), "hosting-cleanup-" + Guid.NewGuid().ToString("N"));
        internal JsonFileStore Store { get; }
        internal string StateFile => Store.ResolvePath(StatePath);
        internal CleanupScheduler Scheduler { get; } = new();
        internal SiteHostingSettingsService Service { get; }
        internal HostingCleanupFixture()
        {
            var options = Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root });
            Store = new(options);
            // Missing IIS site throws locally, so queue:restart cannot launch a native process.
            Service = new(new ResellerSiteAssignmentTests.KullanilmayanSiteServisi(), null!, Scheduler,
                options, new ConcurrencyLock(), Store);
        }
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class CleanupScheduler : ISchedulerService
    {
        internal List<string> Deleted { get; } = [];
        internal List<ScheduledTaskDto> Tasks { get; } = [new() { Name = "AdaQueue-REMOVED" }, new() { Name = "Laravel-REMOVED-Scheduler" }];
        internal int Reads { get; private set; }
        public Task DeleteTaskAsync(string name, CancellationToken cancellationToken = default)
        {
            Deleted.Add(name);
            Tasks.RemoveAll(task => task.Name == name);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyCollection<ScheduledTaskDto>> GetTasksAsync(CancellationToken cancellationToken = default)
        { Reads++; return Task.FromResult<IReadOnlyCollection<ScheduledTaskDto>>(Tasks.ToArray()); }
        public Task<ScheduledTaskDto> CreateTaskAsync(CreateScheduledTaskRequest request, ScheduledTaskPrivilege privilege, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RunTaskAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ScheduledTaskStatusDto> GetTaskStatusAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ScheduledTaskOutputDto> GetTaskOutputAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TaskEnvironmentDto> GetEnvironmentAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MachinePathMeasurement> AddPythonToMachinePathAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetTaskEnabledAsync(string name, bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StopScheduledTaskResultDto> StopTaskAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> RepairTaskSettingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> RemoveTasksUnderRootAsync(string siteRootPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static string OkuKaynak(params string[] parts)
    {
        var klasor = AppContext.BaseDirectory;
        for (var derinlik = 0; derinlik < 8; derinlik++)
        {
            var aday = Path.Combine(new[] { klasor }.Concat(parts).ToArray());
            if (File.Exists(aday))
            {
                return File.ReadAllText(aday);
            }

            klasor = Path.Combine(klasor, "..");
        }

        throw new FileNotFoundException(string.Join('/', parts));
    }
}
