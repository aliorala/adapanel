using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Update;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.SelfUpdate;

/// <summary>İndirmeyi başlatan yönetici: iş istekten ayrıldığı için denetim kaydına gerekli bilgi başta yakalanır.</summary>
public sealed record DownloadActor(Guid? UserId, string? IpAddress);

/// <summary>
/// Güncelleme paketinin indirilmesini HTTP isteğinden ayırır.
/// <para>
/// CANLIDA YAŞANAN HATA: 88 MB'lık paket (yaklaşık 4 dakika) <c>POST /update/apply</c> isteğinin
/// İÇİNDE indiriliyordu. Kullanıcı sayfayı yenileyince istek iptal oldu, indirme sessizce yarıda
/// kaldı ve ekran yine "Paketi indir" gösterdi; kullanıcıya hiçbir hata söylenmedi.
/// </para>
/// <para>
/// Burada iş, istek jetonundan BAĞIMSIZ bir jetonla yürür; yalnız servis kapanırken (StopAsync)
/// durdurulur. Aynı anda tek indirme çalışır. Durum <c>updates/download-state.json</c> dosyasında
/// kalıcıdır: panel indirme sırasında yeniden başlarsa dosyada "indiriliyor" görülür, iş sahibi
/// süreç artık yoktur — durum "failed (update.download_interrupted)" yapılır ve yarım dosya silinir.
/// </para>
/// <para>
/// Güvenlik kontrolleri (SHA256, sürüm düşürme, ProductVersion/kimlik) DEĞİŞMEDİ: bu sınıf yalnız
/// <see cref="ISelfUpdateService"/>'in indirme yöntemini arka planda çağırır.
/// </para>
/// </summary>
public sealed class UpdateDownloadCoordinator(
    JsonFileStore fileStore,
    IServiceScopeFactory scopeFactory,
    ILogger<UpdateDownloadCoordinator> logger) : IHostedService
{
    private const string StatePath = "updates/download-state.json";
    private const string PendingUpdatePath = "updates/pending-update.json";
    internal const string InterruptedKey = "update.download_interrupted";

    private readonly SemaphoreSlim startGate = new(1, 1);
    private readonly object sync = new();
    private readonly CancellationTokenSource stopping = new();
    private PersistedState? state;
    private Task? job;
    private bool loaded;

    public async Task StartAsync(CancellationToken cancellationToken) =>
        await EnsureLoadedAsync(cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        stopping.Cancel();
        Task? running;
        lock (sync) running = job;
        if (running is null) return;
        try { await running.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { }
    }

    public async Task<UpdateDownloadDto> GetAsync(CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        lock (sync) return Snapshot();
    }

    /// <summary>
    /// İndirmeyi başlatır ve HEMEN döner. Zaten bir indirme sürüyorsa yenisini başlatmaz,
    /// süren işin durumunu döndürür (Started = false).
    /// </summary>
    public async Task<(UpdateDownloadDto Download, bool Started)> StartDownloadAsync(
        ApplyUpdateRequest request, CancellationToken requestToken, DownloadActor? actor = null)
    {
        await EnsureLoadedAsync(requestToken);
        await startGate.WaitAsync(requestToken);
        try
        {
            lock (sync)
            {
                if (state is { State: UpdateDownloadDto.Downloading or UpdateDownloadDto.Verifying }) return (Snapshot(), false);
            }
            var now = DateTime.UtcNow;
            var next = new PersistedState
            {
                State = UpdateDownloadDto.Downloading,
                StartedAtUtc = now,
                UpdatedAtUtc = now
            };
            // İstek jetonu yalnız bu kayıt için geçerli; iş başlayınca istekten ayrılır.
            fileStore.EnsureDirectory("updates");
            await fileStore.WriteAsync(StatePath, next, requestToken);
            lock (sync)
            {
                state = next;
                var copy = new ApplyUpdateRequest
                {
                    DownloadUrl = request?.DownloadUrl ?? string.Empty,
                    PackageSha256 = request?.PackageSha256 ?? string.Empty
                };
                job = Task.Run(() => RunAsync(copy, actor, stopping.Token), CancellationToken.None);
                return (Snapshot(), true);
            }
        }
        finally
        {
            startGate.Release();
        }
    }

    private async Task RunAsync(ApplyUpdateRequest request, DownloadActor? actor, CancellationToken token)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<ISelfUpdateService>();
            var result = await service.ApplyUpdateAsync(request, ReportAsync, token);
            if (result.IsPackageReady && result.Notes == "update.package_staged")
            {
                await FinishAsync(UpdateDownloadDto.Ready, string.Empty, result.PreparedVersion, keepPartial: true, actor,
                    $"Update package ready: version={result.PreparedVersion}, sha256={result.PreparedPackageSha256}.");
            }
            else
            {
                var key = string.IsNullOrWhiteSpace(result.Notes) || result.Notes == "update.package_staged"
                    ? "update.download_failed" : result.Notes;
                await FinishAsync(UpdateDownloadDto.Failed, key, result.LatestVersion, keepPartial: false, actor);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await FinishAsync(UpdateDownloadDto.Failed, InterruptedKey, string.Empty, keepPartial: false, actor);
        }
        catch (AppException exception)
        {
            await FinishAsync(UpdateDownloadDto.Failed, exception.MessageKey, string.Empty, keepPartial: false, actor);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Update package download failed.");
            await FinishAsync(UpdateDownloadDto.Failed, "update.download_failed", string.Empty, keepPartial: false, actor);
        }
    }

    private async Task ReportAsync(UpdateDownloadProgress progress)
    {
        PersistedState? toPersist = null;
        lock (sync)
        {
            if (state is null) return;
            var phaseChanged = !string.Equals(state.State, progress.Phase, StringComparison.Ordinal);
            state.State = progress.Phase;
            state.Percent = progress.Percent;
            state.UpdatedAtUtc = DateTime.UtcNow;
            if (progress.PartialPath is not null) state.PartialPath = progress.PartialPath;
            // Yüzde yalnız bellekte tutulur (yeniden başlatma sonrası zaten "failed" olacak); diske
            // yalnız faz değişince ve yarım dosya yolu öğrenilince yazılır.
            if (phaseChanged || progress.PartialPath is not null) toPersist = state.Clone();
        }
        if (toPersist is not null) await fileStore.WriteAsync(StatePath, toPersist, CancellationToken.None);
    }

    private async Task FinishAsync(string finalState, string errorKey, string targetVersion, bool keepPartial, DownloadActor? actor, string readyDetails = "")
    {
        PersistedState snapshot;
        string partial;
        lock (sync)
        {
            snapshot = (state ?? new PersistedState()).Clone();
            partial = snapshot.PartialPath;
            snapshot.State = finalState;
            snapshot.ErrorKey = errorKey;
            snapshot.Percent = finalState == UpdateDownloadDto.Ready ? 100 : null;
            if (!string.IsNullOrEmpty(targetVersion)) snapshot.TargetVersion = targetVersion;
            snapshot.UpdatedAtUtc = DateTime.UtcNow;
            snapshot.PartialPath = string.Empty;
        }
        // Yayınlanmış paketi (bekleyen kayıt gösteriyorsa) silmez: yeniden başlatma yolundaki kuralın aynısı.
        if (!keepPartial) await DeleteUnlessPublishedAsync(partial, CancellationToken.None);
        // Önce diske, SONRA belleğe: bellekteki durum "çalışıyor" olmaktan çıkınca yeni bir indirme
        // başlayabilir; bu işin eski yazması yeni işin kaydının üstüne binmemeli.
        try { await fileStore.WriteAsync(StatePath, snapshot, CancellationToken.None); }
        catch (Exception exception) { logger.LogError(exception, "Update download state could not be saved."); }
        lock (sync) state = snapshot;

        // Sonuç denetim kaydına yazılır: iş istekten ayrıldığı için kullanıcı bilgisi başta yakalanmıştı.
        await WriteAuditAsync(
            finalState == UpdateDownloadDto.Ready ? "update.package.download_completed" : "update.package.download_failed",
            finalState == UpdateDownloadDto.Ready ? readyDetails : $"Update package download failed: error={errorKey}.",
            actor);
    }

    private async Task WriteAuditAsync(string action, string details, DownloadActor? actor)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var audit = scope.ServiceProvider.GetService<IAuditService>();
            if (audit is null) return;
            await audit.WriteForSubscriptionAsync(action, "panel", details, actor?.UserId, null, actor?.IpAddress, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Update download audit record could not be written: {Action}", action);
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        lock (sync) { if (loaded) return; }
        await startGate.WaitAsync(cancellationToken);
        try
        {
            lock (sync) { if (loaded) return; }
            var persisted = await fileStore.ReadAsync(StatePath, new PersistedState(), cancellationToken);
            if (persisted.State is UpdateDownloadDto.Downloading or UpdateDownloadDto.Verifying)
            {
                // İşin sahibi süreç artık yok (panel yeniden başladı): yarıda kaldı.
                var partial = persisted.PartialPath;
                persisted.State = UpdateDownloadDto.Failed;
                persisted.ErrorKey = InterruptedKey;
                persisted.Percent = null;
                persisted.PartialPath = string.Empty;
                persisted.UpdatedAtUtc = DateTime.UtcNow;
                await DeleteUnlessPublishedAsync(partial, cancellationToken);
                fileStore.EnsureDirectory("updates");
                await fileStore.WriteAsync(StatePath, persisted, cancellationToken);
            }
            lock (sync) { state ??= persisted; loaded = true; }
        }
        finally { startGate.Release(); }
    }

    private async Task DeleteUnlessPublishedAsync(string partial, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(partial)) return;
        // Yayınlandıktan sonra (bekleyen kayıt bu dosyayı gösteriyorsa) paket artık "yarım" değildir.
        try
        {
            var pending = await fileStore.ReadAsync(PendingUpdatePath, new PendingPointer(), cancellationToken);
            if (!string.IsNullOrWhiteSpace(pending.PackagePath) &&
                string.Equals(Path.GetFullPath(pending.PackagePath), Path.GetFullPath(partial), StringComparison.OrdinalIgnoreCase))
                return;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Pending update record could not be read; partial package kept.");
            return;
        }
        DeleteQuietly(partial);
    }

    private void DeleteQuietly(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            // Yalnız panelin kendi indirdiği aday dosya: updates klasörü + AdaPanelUpdate-<guid>.exe
            // (SelfUpdateService.TryDeleteSupersededPackageAsync ile aynı önlem). Durum dosyası
            // elle değiştirilse bile başka bir yol silinemez.
            var full = Path.GetFullPath(path);
            var updates = Path.GetFullPath(fileStore.ResolvePath("updates"));
            var stem = Path.GetFileNameWithoutExtension(full);
            if (!string.Equals(Path.GetDirectoryName(full), updates, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetExtension(full), ".exe", StringComparison.OrdinalIgnoreCase) ||
                !stem.StartsWith("AdaPanelUpdate-", StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(stem["AdaPanelUpdate-".Length..], "N", out _))
            {
                logger.LogWarning("Partial update path is outside the managed pattern; not deleted.");
                return;
            }
            if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0) File.Delete(full);
        }
        catch (Exception exception) { logger.LogWarning(exception, "Partial update package could not be deleted."); }
    }

    private UpdateDownloadDto Snapshot() => state is null ? new UpdateDownloadDto() : new UpdateDownloadDto
    {
        State = state.State,
        Percent = state.Percent,
        ErrorKey = state.ErrorKey,
        TargetVersion = state.TargetVersion,
        StartedAtUtc = state.StartedAtUtc,
        UpdatedAtUtc = state.UpdatedAtUtc
    };

    private sealed class PendingPointer
    {
        public string PackagePath { get; set; } = string.Empty;
    }

    private sealed class PersistedState
    {
        public string State { get; set; } = UpdateDownloadDto.Idle;
        public int? Percent { get; set; }
        public string ErrorKey { get; set; } = string.Empty;
        public string TargetVersion { get; set; } = string.Empty;
        public string PartialPath { get; set; } = string.Empty;
        public DateTime? StartedAtUtc { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }

        public PersistedState Clone() => (PersistedState)MemberwiseClone();
    }
}
