using AdaPanel.Core.Dtos.Components;
using AdaPanel.Core.Dtos.Scheduler;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// LARAVEL ARAÇ KUTUSU KİRACIYA AÇIK — O YÜZDEN GÖREVİ SYSTEM OLAMAZ.
/// </summary>
/// <remarks>
/// <para>
/// <b>ÖLÇÜLEN AÇIK:</b> <c>SiteHostingSettingsController</c>
/// <c>[Authorize(Roles = "Administrator,Reseller")]</c> altındadır — yani "kuyruk işçisini başlat"
/// ve "Laravel zamanlayıcıyı başlat" düğmelerine BİR KİRACI (bayi) basabiliyor. Panel bu isteklerde
/// <c>php.exe artisan …</c> çalıştıran birer zamanlanmış görev kuruyor ve görevler
/// <c>-UserId 'SYSTEM' -RunLevel Highest</c> ile kaydediliyordu. Çalıştırılan kod SİTENİN KENDİ
/// kodudur; kiracı o dosyaları FTP, dosya yöneticisi ve Git dağıtımıyla zaten yazabiliyor. Yani
/// düğme, kiracı için SYSTEM'de keyfi kod çalıştırma düğmesiydi — üstelik panelin kendi kuralına
/// da aykırı: <c>CronController</c> "müşteri/bayi komut/PHP/betik görevi kuramaz" diyor.
/// </para>
/// <para>
/// Bu dosya GERÇEK <see cref="SiteHostingSettingsService"/>'i çalıştırır ve zamanlayıcıya hangi
/// yetkiyle gidildiğini ölçer.
/// </para>
/// </remarks>
public sealed class LaravelToolkitTaskIdentityTests
{
    [Fact]
    public async Task Kuyruk_iscisi_gorevi_SINIRLI_kimlikle_kurulur()
    {
        var duzenek = new Duzenek();

        await duzenek.Servis.StartQueueWorkerAsync("musteri.com");

        Assert.Equal(ScheduledTaskPrivilege.Limited, duzenek.Zamanlayici.SonYetki);
    }

    [Fact]
    public async Task Laravel_zamanlayici_gorevi_SINIRLI_kimlikle_kurulur()
    {
        var duzenek = new Duzenek();

        await duzenek.Servis.StartLaravelSchedulerAsync("musteri.com");

        Assert.Equal(ScheduledTaskPrivilege.Limited, duzenek.Zamanlayici.SonYetki);
    }

    /// <summary>
    /// Sınırlı kimliğin site kökünü okuyup yazabilmesi için görev SİTEYE bağlanmalı: site adı
    /// verilmezse zamanlayıcı izni veremez ve görev her turda erişim hatasıyla düşerdi — yani
    /// koruma özelliği sessizce bozardı.
    /// </summary>
    [Fact]
    public async Task Sinirli_gorev_SITEYE_baglanir_ki_izin_verilebilsin()
    {
        var duzenek = new Duzenek();

        await duzenek.Servis.StartQueueWorkerAsync("musteri.com");

        Assert.Equal("musteri.com", duzenek.Zamanlayici.SonIstek!.SiteName);
    }

    /// <summary>
    /// NÖBETÇİ: özellik ÖLMEDİ — görev yine kuruluyor, komut ve argümanları değişmiyor.
    /// </summary>
    [Fact]
    public async Task Kuyruk_iscisi_HALA_kuruluyor()
    {
        var duzenek = new Duzenek();

        var durum = await duzenek.Servis.StartQueueWorkerAsync("musteri.com");

        Assert.Contains("artisan queue:work", duzenek.Zamanlayici.SonIstek!.Arguments, StringComparison.Ordinal);
        Assert.Equal(duzenek.PhpYolu, duzenek.Zamanlayici.SonIstek.Command);
        Assert.Equal(duzenek.SiteKoku, duzenek.Zamanlayici.SonIstek.WorkingDirectory);
        Assert.True(durum.Running);
    }

    private sealed class Duzenek
    {
        public Duzenek()
        {
            var kok = Path.Combine(Path.GetTempPath(), $"adapanel-laravel-{Guid.NewGuid():N}");
            SiteKoku = Path.Combine(kok, "site");
            var phpKlasoru = Path.Combine(kok, "php");
            Directory.CreateDirectory(SiteKoku);
            Directory.CreateDirectory(phpKlasoru);

            // Laravel projesinin işareti: kökteki "artisan" dosyası.
            File.WriteAllText(Path.Combine(SiteKoku, "artisan"), "<?php");
            PhpYolu = Path.Combine(phpKlasoru, "php.exe");
            File.WriteAllText(PhpYolu, "MZ");

            var secenekler = Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = kok });
            Servis = new SiteHostingSettingsService(
                new TekSiteServisi(SiteKoku),
                new PhpKurulu(phpKlasoru),
                Zamanlayici,
                secenekler,
                new SirasizKilit(),
                new JsonFileStore(secenekler));
        }

        public string SiteKoku { get; }

        public string PhpYolu { get; }

        public KaydedenZamanlayici Zamanlayici { get; } = new();

        public SiteHostingSettingsService Servis { get; }
    }

    /// <summary>Görevi KURMAZ; ne gönderildiğini kaydeder ve "kayıtlı" diye geri okutur.</summary>
    private sealed class KaydedenZamanlayici : ISchedulerService
    {
        private readonly List<ScheduledTaskDto> kayitli = [];

        public CreateScheduledTaskRequest? SonIstek { get; private set; }

        public ScheduledTaskPrivilege? SonYetki { get; private set; }

        public Task<ScheduledTaskDto> CreateTaskAsync(
            CreateScheduledTaskRequest request,
            ScheduledTaskPrivilege privilege,
            CancellationToken cancellationToken = default)
        {
            SonIstek = request;
            SonYetki = privilege;
            var gorev = new ScheduledTaskDto { Name = request.Name, State = "Ready" };
            kayitli.Add(gorev);
            return Task.FromResult(gorev);
        }

        public Task<IReadOnlyCollection<ScheduledTaskDto>> GetTasksAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<ScheduledTaskDto>>(kayitli);

        public Task DeleteTaskAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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

    private sealed class TekSiteServisi(string kok) : IIisSiteService
    {
        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default)
            => Task.FromResult(new SiteDto { Name = siteName, PhysicalPath = kok, State = "Started" });

        public Task<IReadOnlyCollection<SiteDto>> GetSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> CreateSiteAsync(CreateSiteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> UpdatePhysicalPathAsync(string siteName, string physicalPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> StartSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> StopSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> AddBindingAsync(string siteName, SiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> RemoveBindingAsync(string siteName, RemoveSiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<SiteDto>> ImportExistingSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class PhpKurulu(string installDir) : IComponentBootstrapper
    {
        public Task<SoftwareCatalogDto> GetCatalogAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new SoftwareCatalogDto
            {
                Installed =
                [
                    new InstalledComponentDto
                    {
                        ComponentId = "php-8.3",
                        Status = "Installed",
                        InstallDir = installDir
                    }
                ]
            });

        public Task<SoftwareCatalogDto> CheckForUpdatesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> GetPendingUpdateCountAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ComponentInstallResultDto> InstallAsync(string componentId, Guid? actorUserId, string? ipAddress, bool force = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<ComponentInstallResultDto>> InstallDefaultsAsync(Guid? actorUserId, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> RemoveAsync(string componentId, Guid? actorUserId, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Kilit davranışı bu testin konusu değil: işi olduğu gibi çalıştırır.</summary>
    private sealed class SirasizKilit : IConcurrencyLock
    {
        public Task RunExclusiveAsync(Func<Task> operation, CancellationToken cancellationToken = default) => operation();

        public Task<T> RunExclusiveAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default) => operation();

        public Task RunExclusiveAsync(string key, Func<Task> operation, CancellationToken cancellationToken = default) => operation();

        public Task<T> RunExclusiveAsync<T>(string key, Func<Task<T>> operation, CancellationToken cancellationToken = default) => operation();
    }
}
