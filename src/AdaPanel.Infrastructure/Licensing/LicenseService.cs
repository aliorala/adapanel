using Microsoft.Extensions.Logging;
using System.Reflection;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Licensing;
using AdaPanel.Core.Interfaces;
using Microsoft.Extensions.Options;

namespace AdaPanel.Infrastructure.Licensing;

/// <summary>
/// <see cref="ILicenseService"/>'in tek gerçeklemesi. Kuralın kendisi burada DEĞİL
/// <see cref="LicenseDecision"/>'da; burada yalnız durum okunur/yazılır ve sunucuya sorulur.
/// </summary>
public sealed class LicenseService(
    IOptions<LicenseOptions> options,
    LicenseStateStore store,
    IMachineFingerprintProvider fingerprintProvider,
    ILicenseVerificationClient verificationClient,
    TimeProvider? timeProvider = null,
    Microsoft.Extensions.Logging.ILogger<LicenseService>? logger = null) : ILicenseService
{
    /// <summary>
    /// Sunucuya ulaşılamadığında yeniden deneme aralığı. Doğrulama aralığından (saatler) çok daha
    /// kısa: sunucu geri geldiğinde panel saatlerce "tolerans"ta beklememeli. Yine de sabit bir alt
    /// sınır var — her istekte yeniden denemek, lisans sunucusu kapalıyken paneli yavaşlatırdı.
    /// </summary>
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(15);

    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Durum okuması. AĞ ÇAĞRISI KİLİT DIŞINDA (M4): eskiden doğrulama sunucusuna giden istek
    /// <c>RunExclusiveAsync</c> içindeydi; yavaş/ulaşılamayan bir lisans sunucusu, her
    /// <c>EnsureCanCreateAsync</c> çağrısını (ve etkinleştirme/devre dışı bırakmayı) zaman aşımı
    /// boyunca tıkıyordu. Şimdi kilit yalnız kısa disk adımlarını korur:
    /// (1) "deneme" damgası atılır (aynı anda gelen diğer istekler ağa gitmez),
    /// (2) ağ çağrısı kilitsiz yapılır,
    /// (3) karar kilit altında yazılır — ama anahtar bu arada DEĞİŞTİYSE (etkinleştirme/devre dışı)
    /// geç gelen karar atılır; eski anahtarın hükmü yeni anahtarın üstüne yazılmaz.
    /// </summary>
    public async Task<LicenseStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var (record, claimedKey, attemptedAt) = await store.RunExclusiveAsync(async () =>
        {
            var current = await LoadAsync(cancellationToken);
            if (!ShouldAttemptVerification(current, Now))
            {
                return (current, (string?)null, default(DateTime));
            }

            var now = Now;
            var stamped = await store.UpdateAsync(existing =>
            {
                existing.LastAttemptAtUtc = now;
                return existing;
            }, cancellationToken);
            stamped.IsConfigured = IsConfigured;
            return (stamped, current.ProtectedKey, now);
        }, cancellationToken);

        if (claimedKey is null)
        {
            return BuildStatus(record);
        }

        var key = store.UnprotectKey(claimedKey);
        if (string.IsNullOrWhiteSpace(key))
        {
            return BuildStatus(record);
        }

        // KİLİT DIŞI: ağ.
        var outcome = await verificationClient.VerifyAsync(NewRequest(key), cancellationToken);

        record = await store.RunExclusiveAsync(async () =>
        {
            var updated = await store.UpdateAsync(existing =>
                string.Equals(existing.ProtectedKey, claimedKey, StringComparison.Ordinal) &&
                existing.LastAttemptAtUtc == attemptedAt
                    ? ApplyOutcome(existing, outcome, attemptedAt)
                    : existing, // anahtar değişti: bu karar ESKİ anahtarındı, uygulanmaz
                cancellationToken);
            updated.IsConfigured = IsConfigured;
            return updated;
        }, cancellationToken);

        return BuildStatus(record);
    }

    private LicenseVerificationRequest NewRequest(string key) => new()
    {
        Product = options.Value.ProductCode,
        LicenseKey = key,
        Fingerprint = fingerprintProvider.Get(),
        PanelVersion = PanelVersion
    };

    private static LicenseRecord ApplyOutcome(LicenseRecord existing, LicenseVerificationOutcome outcome, DateTime attemptedAt)
    {
        if (outcome.Response is null)
        {
            // ULAŞILAMADI: önceki karar OLDUĞU GİBİ korunur. Ağ hatası bir karar değildir.
            existing.LastError = outcome.Error;
            return existing;
        }

        existing.LastVerdict = outcome.Response.Status.Trim().ToLowerInvariant();
        existing.LastVerifiedAtUtc = attemptedAt;
        existing.Sku = outcome.Response.Sku ?? string.Empty;
        existing.MaxSubscriptions = Math.Max(0, outcome.Response.MaxSubscriptions);
        existing.ExpiresAtUtc = outcome.Response.ExpiresAtUtc;
        existing.LastError = outcome.Response.Message ?? string.Empty;
        return existing;
    }

    public Task<LicenseStatusDto> ActivateAsync(string licenseKey, CancellationToken cancellationToken = default) =>
        store.RunExclusiveAsync(() => ActivateCoreAsync(licenseKey, cancellationToken), cancellationToken);

    private async Task<LicenseStatusDto> ActivateCoreAsync(string licenseKey, CancellationToken cancellationToken)
    {
        var trimmed = (licenseKey ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new AppException("license.key_required", 400, 400);
        }

        if (string.IsNullOrWhiteSpace(options.Value.VerificationUrl))
        {
            // Lisanslama kapalıyken anahtar kabul etmek, kapalı olduğunu gizlemek olurdu.
            throw new AppException("license.not_configured", 400, 400);
        }

        var current = await LoadAsync(cancellationToken);
        var probed = await VerifyAsync(current, licenseKeyOverride: trimmed, cancellationToken);

        // Sunucu anahtarı AÇIKÇA reddettiyse saklamayız: yanlış yazılmış bir anahtarın panelde
        // kalıp her açılışta "geçersiz" demesi kullanıcıyı yanıltır.
        if (probed.InvalidResponse)
        {
            await store.UpdateAsync(existing =>
            {
                existing.ProtectedKey = string.Empty;
                existing.KeySuffix = string.Empty;
                existing.KeyStoredAtUtc = null;
                existing.LastVerdict = string.Empty;
                existing.LastError = "invalid_key";
                existing.LastAttemptAtUtc = Now;
                return existing;
            }, cancellationToken);

            throw new AppException("license.invalid", 402, 402);
        }

        return BuildStatus(probed.Record);
    }

    public Task<LicenseStatusDto> DeactivateAsync(CancellationToken cancellationToken = default) =>
        store.RunExclusiveAsync(() => DeactivateCoreAsync(cancellationToken), cancellationToken);

    private async Task<LicenseStatusDto> DeactivateCoreAsync(CancellationToken cancellationToken)
    {
        // Kayıt SİLİNMEZ, SIFIRLANIR: dosyanın var olması "lisans daha önce girilmişti" bilgisini
        // korur ve bir sonraki okuma taze bir kayıt oluşturmak zorunda kalmaz.
        var record = await store.UpdateAsync(existing =>
        {
            existing.ProtectedKey = string.Empty;
            existing.KeySuffix = string.Empty;
            existing.KeyStoredAtUtc = null;
            existing.LastVerdict = string.Empty;
            existing.LastVerifiedAtUtc = null;
            existing.LastAttemptAtUtc = null;
            existing.Sku = string.Empty;
            existing.MaxSubscriptions = 0;
            existing.ExpiresAtUtc = null;
            existing.LastError = string.Empty;
            return existing;
        }, cancellationToken);

        record.IsConfigured = IsConfigured;
        return BuildStatus(record);
    }

    public Task<LicenseStatusDto> RefreshAsync(CancellationToken cancellationToken = default) =>
        store.RunExclusiveAsync(() => RefreshCoreAsync(cancellationToken), cancellationToken);

    private async Task<LicenseStatusDto> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        var record = await LoadAsync(cancellationToken);
        if (!IsConfigured || string.IsNullOrWhiteSpace(record.ProtectedKey))
        {
            return BuildStatus(record);
        }

        record = (await VerifyAsync(record, licenseKeyOverride: null, cancellationToken)).Record;
        return BuildStatus(record);
    }

    public string GetFingerprint() => fingerprintProvider.Get();

    public async Task EnsureCanCreateAsync(
        LicensedResource resource,
        int existingCount = 0,
        CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(cancellationToken);

        // Lisanslama kapalıysa hiçbir şey sorulmaz; en sık yol da budur.
        if (!status.IsConfigured)
        {
            return;
        }

        if (status.RestrictsNewResources)
        {
            // 402 Payment Required: istemci "yetkin yok" (403) ile "lisansın kapsamıyor" (402)
            // arasındaki farkı görebilsin. Mesaj anahtarı NEDEN kısıtlandığını söyler.
            throw new AppException(status.MessageKey, 402, 402);
        }

        if (resource == LicensedResource.Subscription &&
            LicenseDecision.ExceedsSubscriptionLimit(status.MaxSubscriptions, existingCount))
        {
            throw new AppException("license.subscription_limit", 402, 402, status.MaxSubscriptions.ToString());
        }
    }

    // --- iç yardımcılar ---

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private bool IsConfigured => !string.IsNullOrWhiteSpace(options.Value.VerificationUrl);

    private async Task<LicenseRecord> LoadAsync(CancellationToken cancellationToken)
    {
        var record = await store.ReadAsync(cancellationToken);
        // Yapılandırma diske yazılmaz; her okumada GERÇEK ayardan doldurulur. Aksi hâlde
        // lisanslamayı kapatmak, eski dosyadaki "açık" değeri yüzünden etkisiz kalırdı.
        record.IsConfigured = IsConfigured;
        return record;
    }

    /// <summary>
    /// Şimdi sunucuya sormalı mıyız? Yapılandırılmamışsa ya da anahtar yoksa ASLA sormayız
    /// (gizlilik: yapılandırılmadıkça hiçbir yere istek gitmez).
    /// </summary>
    private bool ShouldAttemptVerification(LicenseRecord record, DateTime nowUtc)
    {
        if (!record.IsConfigured || string.IsNullOrWhiteSpace(record.ProtectedKey))
        {
            return false;
        }

        // Son deneme başarısızsa kısa aralıkla, başarılıysa yapılandırılan aralıkla.
        var lastSuccess = record.LastVerifiedAtUtc;
        var lastAttempt = record.LastAttemptAtUtc;

        if (lastAttempt is null)
        {
            return true;
        }

        var succeededOnLastAttempt = lastSuccess.HasValue && lastSuccess.Value >= lastAttempt.Value;
        var wait = succeededOnLastAttempt
            ? TimeSpan.FromHours(Math.Max(1, options.Value.VerifyIntervalHours))
            : RetryAfterFailure;

        return nowUtc >= lastAttempt.Value.Add(wait);
    }

    /// <summary>
    /// Sunucuya sorar ve sonucu diske işler. HİÇBİR koşulda istisna fırlatmaz (iptal hariç):
    /// doğrulama başarısız olmak, panelin çalışmasını durdurmamalı.
    /// </summary>
    private async Task<(LicenseRecord Record, bool InvalidResponse)> VerifyAsync(
        LicenseRecord record,
        string? licenseKeyOverride,
        CancellationToken cancellationToken)
    {
        var key = licenseKeyOverride ?? store.UnprotectKey(record.ProtectedKey);
        if (string.IsNullOrWhiteSpace(key))
        {
            return (record, false);
        }

        var differentKey = licenseKeyOverride is not null &&
            !string.Equals(licenseKeyOverride, store.UnprotectKey(record.ProtectedKey), StringComparison.Ordinal);

        var attemptedAt = Now;
        var outcome = await verificationClient.VerifyAsync(
            new LicenseVerificationRequest
            {
                Product = options.Value.ProductCode,
                LicenseKey = key,
                Fingerprint = fingerprintProvider.Get(),
                PanelVersion = PanelVersion
            },
            cancellationToken);

        LicenseRecord updated;
        try
        {
        updated = await store.UpdateAsync(existing =>
        {
            existing.LastAttemptAtUtc = attemptedAt;

            // Yeni anahtar deneniyorsa ULAŞILAMASA BİLE saklanır: kurulum anında ağ yoksa
            // müşteri lisansını girip çevrimdışı toleransla çalışmaya başlayabilmeli.
            if (differentKey)
            {
                existing.ProtectedKey = store.ProtectKey(key);
                existing.KeySuffix = LicenseStateStore.Suffix(key);
                existing.KeyStoredAtUtc = attemptedAt;
                // A verdict belongs to the key it verified. A new offline key starts its own
                // grace period; re-entering the same key must not renew that period.
                existing.LastVerdict = string.Empty;
                existing.LastVerifiedAtUtc = null;
                existing.Sku = string.Empty;
                existing.MaxSubscriptions = 0;
                existing.ExpiresAtUtc = null;
            }

            if (outcome.Response is null)
            {
                // ULAŞILAMADI: önceki karar OLDUĞU GİBİ korunur. Ağ hatası bir karar değildir;
                // "geçerli"yi "geçersiz"e çevirmek tam da kaçınmak istediğimiz davranış olurdu.
                existing.LastError = outcome.Error;
                return existing;
            }

            existing.LastVerdict = outcome.Response.Status.Trim().ToLowerInvariant();
            existing.LastVerifiedAtUtc = attemptedAt;
            existing.Sku = outcome.Response.Sku ?? string.Empty;
            existing.MaxSubscriptions = Math.Max(0, outcome.Response.MaxSubscriptions);
            existing.ExpiresAtUtc = outcome.Response.ExpiresAtUtc;
            existing.LastError = outcome.Response.Message ?? string.Empty;
            return existing;
        }, cancellationToken);
        }
        catch (IOException exception)
        {
            // Mühür anahtarı okunamadı (geçici G/Ç): kayıt MÜHÜRLENEMEDİ ve dosyaya yazılmadı. VerifyAsync
            // istisna atmaz sözleşmesi gereği önceki karar OLDUĞU GİBİ korunur, uyarı günlüğe yazılır.
            logger?.LogWarning(exception, "Lisans doğrulama sonucu kaydedilemedi (mühür anahtarı okunamadı); önceki karar korunuyor.");
            record.IsConfigured = IsConfigured;
            return (record, false);
        }

        updated.IsConfigured = IsConfigured;
        return (updated, string.Equals(outcome.Response?.Status?.Trim(), LicenseVerdicts.Invalid, StringComparison.OrdinalIgnoreCase));
    }

    private LicenseStatusDto BuildStatus(LicenseRecord record)
    {
        var settings = options.Value;
        var decision = LicenseDecision.Evaluate(record, settings.OfflineGraceDays, settings.VerifyIntervalHours, Now);

        return new LicenseStatusDto
        {
            State = decision.State,
            MessageKey = decision.MessageKey,
            IsConfigured = record.IsConfigured,
            RestrictsNewResources = decision.RestrictsNewResources,
            HasKey = !string.IsNullOrWhiteSpace(record.ProtectedKey),
            KeySuffix = record.KeySuffix,
            Sku = record.Sku,
            MaxSubscriptions = record.MaxSubscriptions,
            ExpiresAtUtc = record.ExpiresAtUtc,
            LastVerifiedAtUtc = record.LastVerifiedAtUtc,
            OfflineGraceEndsAtUtc = decision.OfflineGraceEndsAtUtc,
            Fingerprint = fingerprintProvider.Get(),
            FingerprintSource = fingerprintProvider.GetSource(),
            LastError = record.LastError
        };
    }

    /// <summary>Panel sürümü; lisans sunucusuna gönderilen tek "ürün" bilgisi.</summary>
    private static string PanelVersion =>
        typeof(LicenseService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(LicenseService).Assembly.GetName().Version?.ToString()
        ?? string.Empty;
}
