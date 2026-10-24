using System.Diagnostics;
using System.Collections.Concurrent;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.RunningJobs;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.RunningJobs;

/// <summary>
/// Uzun süren işlerin sunucu tarafındaki kaydı (bkz. <see cref="IRunningJobService"/>).
/// <para>
/// Sahiplik muhasebesi <c>Antivirus/AntivirusService.cs</c>'teki kanıtlanmış desenin birebir
/// uygulamasıdır: PID + süreç başlangıç tiki. Depo da aynı: modülün ZATEN kullandığı
/// <see cref="JsonFileStore"/>; yeni bir depo, zamanlayıcı ya da arka plan servisi eklenmez.
/// </para>
/// </summary>
public sealed class RunningJobService(JsonFileStore fileStore, ILogger<RunningJobService> logger) : IRunningJobService
{
    private const string StatePath = "config/running-jobs.json";
    // Only failed cleanups remain here. Service instances share recovery evidence,
    // isolated by the full data path. Entries disappear after a successful atomic write.
    private static readonly ConcurrentDictionary<CompletedJob, byte> PendingCleanup = new();
    private readonly string stateFile = fileStore.ResolvePath(StatePath).ToLowerInvariant();
    private sealed record CompletedJob(string StateFile, string Slot, string JobId, DateTime StartedAtUtc);

    /// <summary>
    /// Tür başına "zaten sürüyor" mesaj anahtarı.
    /// <para>
    /// SABİT DİZGELER: anahtar enterpolasyonla kurulsaydı <c>MessageKeyCoverageTests</c> onu
    /// göremez ve çevirisi olmayan bir anahtar kullanıcının ekranında HAM olarak belirirdi.
    /// </para>
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> CakismaAnahtarlari =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [RunningJobKinds.Backup] = "runningjob.backup_in_progress",
            [RunningJobKinds.Restore] = "runningjob.restore_in_progress",
            [RunningJobKinds.Migration] = "runningjob.migration_in_progress",
            [RunningJobKinds.Software] = "runningjob.software_in_progress"
        };

    public async Task<RunningJobsStatusDto> GetStatusAsync(
        IReadOnlyCollection<string> kinds,
        IReadOnlySet<string>? allowedSubjectUserIds,
        CancellationToken cancellationToken = default)
    {
        await RetryCompletedAsync(cancellationToken);
        var istenen = kinds.Select(Normalize).ToHashSet(StringComparer.Ordinal);
        var dosya = await fileStore.ReadAsync(StatePath, new RunningJobsFile(), cancellationToken);

        // YAZMA YALNIZ GEREKİYORSA. Bu uç 15 saniyede bir yoklanıyor; her yoklamada dosyayı
        // yeniden yazmak, hiçbir şey değişmese bile diske sürekli yazmak olurdu. Bayat kayıt
        // varsa (ve ancak o zaman) uzlaştırma yazması yapılır.
        if (dosya.Running.Values.Any(kayit => !IsOwnedByCurrentProcess(kayit)))
        {
            dosya = await fileStore.UpdateAsync(StatePath, new RunningJobsFile(), BayatlariUzlastir, cancellationToken);
        }

        // SÜZGEÇ YUVA ANAHTARINA DEĞİL, KAYDIN KENDİ TÜRÜNE bakar. Anahtar artık kiracıyı da
        // taşıyor ("backup|<abonelik>"), yani anahtarı türle karşılaştırmak bütün kiracı işlerini
        // görünmez kılardı. Tür zaten kaydın içinde yazılı ve YAZAN taraf onu tek kaynaktan
        // (Normalize) dolduruyor.
        var suren = dosya.Running
            .Select(giris => giris.Value)
            .Where(kayit => istenen.Contains(Normalize(kayit.Kind)))
            .Where(kayit => kayit.StartedAtUtc != default && Gorunur(kayit.SubjectUserId, allowedSubjectUserIds))
            .OrderBy(kayit => kayit.StartedAtUtc)
            .Select(kayit => new RunningJobDto
            {
                Kind = Normalize(kayit.Kind),
                Title = kayit.Title,
                StartedAtUtc = kayit.StartedAtUtc
            })
            .ToArray();

        var kesilen = dosya.Interrupted
            .Select(giris => giris.Value)
            .Where(kayit => istenen.Contains(Normalize(kayit.Kind)))
            .Where(kayit => kayit.StartedAtUtc != default && Gorunur(kayit.SubjectUserId, allowedSubjectUserIds))
            .OrderBy(kayit => kayit.StartedAtUtc)
            .Select(kayit => new InterruptedJobDto
            {
                Kind = Normalize(kayit.Kind),
                Title = kayit.Title,
                StartedAtUtc = kayit.StartedAtUtc,
                DetectedAtUtc = kayit.DetectedAtUtc
            })
            .ToArray();

        return new RunningJobsStatusDto
        {
            Running = suren,
            Interrupted = kesilen,
            ServerTimeUtc = DateTime.UtcNow
        };
    }

    public async Task<IAsyncDisposable> BeginAsync(
        string kind,
        string title,
        Guid? subjectUserId,
        CancellationToken cancellationToken = default)
    {
        await RetryCompletedAsync(cancellationToken);
        var tur = Normalize(kind);
        var yuva = YuvaAnahtari(tur, subjectUserId);
        var baslangic = DateTime.UtcNow;
        var jobId = Guid.NewGuid().ToString("N");

        // KONTROL + YAZMA AYNI KİLİDİN ALTINDA. UpdateAsync dosya başına kilit tutar; "yer boş mu"
        // sorusunu ayrı bir okumayla sorsaydık iki eşzamanlı istek ikisi de boş görüp ikisi de
        // yazabilirdi — 409 koruması kâğıt üzerinde kalırdı.
        await fileStore.UpdateAsync(
            StatePath,
            new RunningJobsFile(),
            dosya =>
            {
                if (dosya.Running.TryGetValue(yuva, out var mevcut) &&
                    mevcut.StartedAtUtc != default &&
                    IsOwnedByCurrentProcess(mevcut))
                {
                    throw new AppException(
                        CakismaAnahtarlari.TryGetValue(tur, out var anahtar) ? anahtar : "runningjob.backup_in_progress",
                        409,
                        409,
                        $"'{mevcut.Title}' started at {mevcut.StartedAtUtc:O} is still running.");
                }

                dosya.Running[yuva] = new RunningJobState
                {
                    JobId = jobId,
                    Kind = tur,
                    Title = title,
                    StartedAtUtc = baslangic,
                    SubjectUserId = subjectUserId is { } sahip ? sahip.ToString("N") : string.Empty,
                    OwnerProcessId = Environment.ProcessId,
                    OwnerProcessStartedAtTicks = CurrentProcessStartTicks()
                };

                // Yeni iş, AYNI YUVANIN eski "kesildi" uyarısını GEÇERSİZ KILAR: o kiracı işi
                // zaten yeniden başlattı, uyarıyı bir daha göstermek gürültü olurdu. Komşu
                // kiracının kesilmiş işi ise yerinde kalır — onun haberi hâlâ verilmemiştir.
                dosya.Interrupted.Remove(yuva);
                return dosya;
            },
            cancellationToken);

        return new JobScope(this, new CompletedJob(stateFile, yuva, jobId, baslangic));
    }

    /// <summary>
    /// Bir işin KİMİNLE yarıştığını belirleyen yuva anahtarı.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>KAPATILAN AÇIK (ölçüldü):</b> anahtar yalnız İŞ TÜRÜYDÜ. Bir müşterinin iki saatlik
    /// yedeği sürerken BÜTÜN öteki kiracılar ve yönetici de 409 alıyordu — yani her kiracı, tek bir
    /// uzun istekle paneldeki bütün yedek/geri yükleme yeteneğini kapatabiliyordu. Bu bir çapraz
    /// kiracı hizmet reddidir ve ücretsiz bir hesapla tetiklenebilirdi.
    /// </para>
    /// <para>
    /// <b>KARAR İŞ TÜRÜNE GÖRE DEĞİL, İŞİN KAYNAĞINA GÖRE VERİLDİ (ölçüldü, tercih değil):</b>
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <b>Sahibi olan iş (<paramref name="subjectUserId"/> dolu) → KİRACI BAŞINA yuva.</b> Bu
    /// işlerin dokunduğu her şey o aboneliğe aittir: yedek arşivi abonelik klasörüne yazılır,
    /// geçici dosyalar GUID'lidir (<c>BackupService</c>, <c>Path.GetTempPath()</c> + <c>Guid</c>),
    /// yedek kataloğu ise <c>JsonFileStore.UpdateAsync</c> kilidiyle güncellenir — yani iki
    /// kiracının aynı anda yedek alması ortak hiçbir duruma yazmaz.
    /// </item>
    /// <item>
    /// <b>Sahipsiz iş (<c>null</c>) → MAKİNE GENELİ tek yuva.</b> Bunlar gerçekten tekil olmak
    /// zorundadır ve kiracı başına bölmek GERÇEK bir yarış açardı: bileşen kurulumu
    /// (<c>software</c>) paylaşılan çalışma zamanı dizinine, sistem PATH'ine ve servis kaydına
    /// yazar; göç (<c>migration</c>) IIS yapılandırmasına yazar; panel/posta yedeği posta
    /// servisini DURDURUR. Hiçbirinin ikinci bir kopyası aynı anda koşamaz.
    /// </item>
    /// </list>
    /// <para>
    /// <b>SAHİPSİZ İŞ, KİRACI İŞİNİ ARTIK ENGELLEMEZ</b> ve bu bilinçlidir: yöneticinin panel
    /// yedeği ile müşterinin site yedeği ayrı kaynaklara dokunur. Engellemek, kapatılan çapraz
    /// kiracı reddini yönetici tarafından geri getirmek olurdu.
    /// </para>
    /// <para>
    /// <b>KİRACI BAŞINA HÂLÂ TEK İŞ:</b> bir müşteri kendi türünde ikinci bir iş açamaz, yani
    /// kaynak tüketimi kiracı sayısıyla sınırlı kalır — ve kiracıları yalnız yönetici oluşturur.
    /// </para>
    /// </remarks>
    private static string YuvaAnahtari(string normalizedKind, Guid? subjectUserId) =>
        subjectUserId is { } sahip ? $"{normalizedKind}|{sahip:N}" : normalizedKind;

    /// <summary>
    /// İş bitti: kaydı boşaltır.
    /// <para>
    /// <b>İŞ KİMLİĞİ VE BAŞLANGIÇ ANI KARŞILAŞTIRILIYOR:</b> yuvada başka bir işin kaydı duruyorsa (kuramsal
    /// olarak 409 bunu engeller, ama savunma ucuz) onu silmek, GERÇEKTEN süren bir işi ekrandan
    /// yok etmek olurdu. Yalnız kendi yazdığımız kayıt kaldırılır.
    /// </para>
    /// </summary>
    private Task EndAsync(CompletedJob completed) =>
        fileStore.UpdateAsync(
            StatePath,
            new RunningJobsFile(),
            dosya => RemoveCompleted(dosya, completed),
            CancellationToken.None);

    private static RunningJobsFile RemoveCompleted(RunningJobsFile file, CompletedJob completed)
    {
        if (file.Running.TryGetValue(completed.Slot, out var current) &&
            current.JobId == completed.JobId && current.StartedAtUtc == completed.StartedAtUtc &&
            IsOwnedByCurrentProcess(current))
            file.Running.Remove(completed.Slot);
        return file;
    }

    private async Task RetryCompletedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pending = PendingCleanup.Keys.Where(item => item.StateFile == stateFile).ToArray();
        if (pending.Length == 0) return;
        await fileStore.UpdateAsync(StatePath, new RunningJobsFile(), file =>
        {
            foreach (var completed in pending) RemoveCompleted(file, completed);
            return file;
        }, cancellationToken);
        // Never forget completion if publication failed or was cancelled. A later request retries.
        foreach (var completed in pending) PendingCleanup.TryRemove(completed, out _);
    }

    /// <summary>
    /// Sahibi artık yaşamayan kayıtları "sürüyor"dan "kesildi"ye taşır.
    /// <para>
    /// Üç durum vardır: (1) kayıt yok → iş yok. (2) kayıt VAR ve sahibi BU süreç → iş gerçekten
    /// sürüyor. (3) kayıt var ama sahibi başka bir süreç → panel iş sürerken yeniden başlamış; o
    /// iş artık koşmuyor ve sonucu HİÇ YAZILAMAYACAK. Üçüncü durumda kayıt "kesildi" olur:
    /// kullanıcı iki saatlik yedeğinin neden bitmediğini öğrenmeli, ekran sessizce boş kalmamalı.
    /// </para>
    /// </summary>
    private RunningJobsFile BayatlariUzlastir(RunningJobsFile dosya)
    {
        var simdi = DateTime.UtcNow;
        foreach (var giris in dosya.Running.ToArray())
        {
            if (IsOwnedByCurrentProcess(giris.Value))
            {
                continue;
            }

            logger.LogWarning(
                "Yarıda kalan uzun iş bulundu ({Kind}: {Title}, başlangıç {StartedAt:O}): panel süreci iş sürerken yeniden başlamış.",
                giris.Value.Kind,
                giris.Value.Title,
                giris.Value.StartedAtUtc);

            dosya.Running.Remove(giris.Key);

            // Başlangıcı olmayan (bozuk/yarım) kayıt "kesildi" diye gösterilmez: kullanıcıya
            // anlatacak hiçbir şeyi yoktur, yalnız gürültü üretirdi. Yuva yine de boşaltılır.
            if (giris.Value.StartedAtUtc == default)
            {
                continue;
            }

            dosya.Interrupted[Normalize(giris.Key)] = new InterruptedJobState
            {
                Kind = Normalize(giris.Value.Kind),
                Title = giris.Value.Title,
                StartedAtUtc = giris.Value.StartedAtUtc,
                DetectedAtUtc = simdi,
                SubjectUserId = giris.Value.SubjectUserId
            };
        }

        return dosya;
    }

    /// <summary>
    /// Kapsamlı kullanıcı bu işi görebilir mi.
    /// <para>
    /// Kapsamsız rol (yönetici) her şeyi görür. Kapsamlı kullanıcı YALNIZ sahibi bilinen ve kendi
    /// erişim kümesinde olan işleri görür: sunucu geneli işler (panel yedeği, posta sunucusu
    /// yedeği, bileşen kurulumu) sahipsizdir ve kiracıya hiç görünmez.
    /// </para>
    /// </summary>
    private static bool Gorunur(string subjectUserId, IReadOnlySet<string>? allowedSubjectUserIds) =>
        allowedSubjectUserIds is null ||
        (!string.IsNullOrWhiteSpace(subjectUserId) && allowedSubjectUserIds.Contains(subjectUserId));

    /// <summary>Kaydın sahibi bu panel süreci mi (PID + süreç başlangıç anı çifti).</summary>
    internal static bool IsOwnedByCurrentProcess(RunningJobState state)
    {
        if (state.OwnerProcessId != Environment.ProcessId)
        {
            return false;
        }

        var ticks = CurrentProcessStartTicks();

        // Başlangıç anı ölçülemediyse (kısıtlı ortam) tek elde kalan kanıt PID'dir; onu da yok
        // saymak, süren HER işi "yarıda kalmış" ilan etmek olurdu.
        return ticks == 0 || state.OwnerProcessStartedAtTicks == 0 || state.OwnerProcessStartedAtTicks == ticks;
    }

    private static long CurrentProcessStartTicks()
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            return current.StartTime.ToUniversalTime().Ticks;
        }
        catch
        {
            // Bazı ortamlarda süreç başlangıç anı okunamaz; 0 = "ölçülemedi".
            return 0;
        }
    }

    /// <summary>
    /// Sözlük anahtarları KÜÇÜK HARFE sabitlenir.
    /// <para>
    /// NEDEN: <c>System.Text.Json</c> bir sözlük özelliğini geri okurken YENİ bir sözlük kurar ve
    /// bizim verdiğimiz <c>OrdinalIgnoreCase</c> karşılaştırıcısı diskten dönen nesnede KAYBOLUR.
    /// Büyük/küçük harf duyarlılığına güvenmek yerine anahtar tek biçime çekiliyor; böylece
    /// yazan ve okuyan aynı yuvayı bulur.
    /// </para>
    /// </summary>
    private static string Normalize(string kind) => (kind ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>
    /// Kapsam temizliğinin başarısızlığını kaydeder. Ayrı bir metot çünkü birincil kurucunun
    /// <c>logger</c> parametresi iç içe sınıftan görünmez.
    /// </summary>
    private void TemizlemeHatasiniKaydet(string yuva, Exception exception) =>
        logger.LogWarning(
            exception,
            "Çalışan-iş kaydı temizlenemedi ({Yuva}); sonraki durum veya başlatma isteğinde tekrar denenecek.",
            yuva);

    /// <summary>
    /// <c>await using</c> kapsamı: iş nasıl biterse bitsin (başarı, hata, iptal) kaydı temizler.
    /// <para>
    /// Elle <c>try/finally</c> yerine kapsam kullanılıyor çünkü kaydı temizlemeyi UNUTMAK sessiz
    /// ve kalıcı bir arızadır: panel bir daha o türden iş başlatamaz (409) ve ekranda sonsuz
    /// "sürüyor" şeridi gösterir.
    /// </para>
    /// </summary>
    private sealed class JobScope(RunningJobService owner, CompletedJob completed) : IAsyncDisposable
    {
        private int disposed;
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try
            {
                await owner.EndAsync(completed);
            }
            catch (Exception exception)
            {
                // TEMİZLİK HATASI İŞİN SONUCUNU EZMEZ. Buradan istisna fırlatmak, başarıyla biten
                // bir yedeği kullanıcıya "başarısız" göstermek olurdu. Bittiğini bellekte sakla;
                // geçici dosya hatası kalkınca başka servis örneği de güvenle temizleyebilir.
                PendingCleanup.TryAdd(completed, 0);
                owner.TemizlemeHatasiniKaydet(completed.Slot, exception);
            }
        }
    }
}
