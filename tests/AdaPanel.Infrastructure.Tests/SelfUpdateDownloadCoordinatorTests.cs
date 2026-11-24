using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Update;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.SelfUpdate;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Canlıda yaşanan hata: 88 MB'lık paket POST /update/apply isteğinin İÇİNDE iniyordu; sayfa
/// yenilenince istek iptal oldu ve indirme sessizce yarıda kaldı. İndirme artık arka plan işidir.
/// </summary>
public sealed class SelfUpdateDownloadCoordinatorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-updl-" + Guid.NewGuid().ToString("N"));
    private readonly JsonFileStore store;

    public SelfUpdateDownloadCoordinatorTests()
    {
        Directory.CreateDirectory(root);
        store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { /* temizlik zorunlu değil */ }
    }

    private UpdateDownloadCoordinator Create(FakeUpdateService fake)
    {
        var services = new ServiceCollection();
        services.AddScoped<ISelfUpdateService>(_ => fake);
        return new UpdateDownloadCoordinator(store, services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<UpdateDownloadCoordinator>.Instance);
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (await condition()) return;
            await Task.Delay(25);
        }
        Assert.Fail("Condition was not reached.");
    }

    [Fact]
    public async Task Cancelling_the_request_does_not_cancel_the_download()
    {
        var fake = new FakeUpdateService();
        var coordinator = Create(fake);
        using var request = new CancellationTokenSource();

        var (download, started) = await coordinator.StartDownloadAsync(new ApplyUpdateRequest(), request.Token);
        Assert.True(started);
        Assert.Equal(UpdateDownloadDto.Downloading, download.State);
        await fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        request.Cancel(); // kullanıcı sayfayı yeniledi
        fake.Release.SetResult();

        await WaitForAsync(async () => (await coordinator.GetAsync(CancellationToken.None)).State == UpdateDownloadDto.Ready);
        Assert.False(fake.ObservedCancellation);
        Assert.Equal(100, (await coordinator.GetAsync(CancellationToken.None)).Percent);
    }

    [Fact]
    public async Task A_second_request_while_downloading_does_not_start_another_download()
    {
        var fake = new FakeUpdateService();
        var coordinator = Create(fake);

        var first = await coordinator.StartDownloadAsync(new ApplyUpdateRequest(), CancellationToken.None);
        await fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await coordinator.StartDownloadAsync(new ApplyUpdateRequest(), CancellationToken.None);

        Assert.True(first.Started);
        Assert.False(second.Started);
        Assert.Equal(UpdateDownloadDto.Downloading, second.Download.State);
        fake.Release.SetResult();
        await WaitForAsync(async () => (await coordinator.GetAsync(CancellationToken.None)).State == UpdateDownloadDto.Ready);
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task After_a_restart_an_unfinished_download_becomes_failed_and_the_partial_file_is_removed()
    {
        var fake = new FakeUpdateService { PartialPath = Path.Combine(root, "updates", "AdaPanelUpdate-partial.exe") };
        var first = Create(fake);
        await first.StartDownloadAsync(new ApplyUpdateRequest(), CancellationToken.None);
        await fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(File.Exists(fake.PartialPath));

        // Panel yeniden başladı: yeni süreç, aynı veri klasörü.
        var restarted = Create(new FakeUpdateService());
        await restarted.StartAsync(CancellationToken.None);

        var state = await restarted.GetAsync(CancellationToken.None);
        Assert.Equal(UpdateDownloadDto.Failed, state.State);
        Assert.Equal("update.download_interrupted", state.ErrorKey);
        Assert.False(File.Exists(fake.PartialPath));
        fake.Release.SetResult();
    }

    [Fact]
    public async Task Stopping_the_host_stops_the_job_and_reports_it_as_interrupted()
    {
        var fake = new FakeUpdateService { PartialPath = Path.Combine(root, "updates", "AdaPanelUpdate-stop.exe") };
        var coordinator = Create(fake);
        await coordinator.StartDownloadAsync(new ApplyUpdateRequest(), CancellationToken.None);
        await fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await coordinator.StopAsync(CancellationToken.None);

        var state = await coordinator.GetAsync(CancellationToken.None);
        Assert.Equal(UpdateDownloadDto.Failed, state.State);
        Assert.Equal("update.download_interrupted", state.ErrorKey);
        Assert.False(File.Exists(fake.PartialPath));
    }

    [Fact]
    public async Task A_rejected_package_is_reported_as_failed_with_its_message_key()
    {
        var fake = new FakeUpdateService { Failure = new AppException("update.checksum_mismatch", 400, 400) };
        var coordinator = Create(fake);
        fake.Release.SetResult();

        await coordinator.StartDownloadAsync(new ApplyUpdateRequest(), CancellationToken.None);
        await WaitForAsync(async () => (await coordinator.GetAsync(CancellationToken.None)).State == UpdateDownloadDto.Failed);

        Assert.Equal("update.checksum_mismatch", (await coordinator.GetAsync(CancellationToken.None)).ErrorKey);
        // Başarısızlıktan sonra yeni deneme başlatılabilir.
        fake.Failure = null;
        var retry = await coordinator.StartDownloadAsync(new ApplyUpdateRequest(), CancellationToken.None);
        Assert.True(retry.Started);
    }

    private sealed class FakeUpdateService : ISelfUpdateService
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? PartialPath { get; init; }
        public AppException? Failure { get; set; }
        public bool ObservedCancellation { get; private set; }
        public int Calls;

        public async Task<UpdateStatusDto> ApplyUpdateAsync(
            ApplyUpdateRequest request, Func<UpdateDownloadProgress, Task>? report, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            if (PartialPath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PartialPath)!);
                if (report is not null) await report(new UpdateDownloadProgress(UpdateDownloadDto.Downloading, 0, PartialPath));
                await File.WriteAllBytesAsync(PartialPath, [1, 2, 3], cancellationToken);
            }
            Entered.TrySetResult();
            try { await Release.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException)
            {
                ObservedCancellation = true;
                if (PartialPath is not null && File.Exists(PartialPath)) File.Delete(PartialPath);
                throw;
            }
            if (Failure is not null) throw Failure;
            return new UpdateStatusDto { IsPackageReady = true, Notes = "update.package_staged", PreparedVersion = "9.9.9" };
        }

        public Task<UpdateStatusDto> ApplyUpdateAsync(ApplyUpdateRequest request, CancellationToken cancellationToken = default) =>
            ApplyUpdateAsync(request, null, cancellationToken);

        public Task<UpdateStatusDto> CheckForUpdateAsync(bool forceRefresh = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UpdateStatusDto> SetGithubRepoAsync(string repo, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UpdateStatusDto> InstallPreparedUpdateAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
