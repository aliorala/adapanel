using System.Diagnostics;
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
        var istenen = kinds.Select(Normalize).ToHashSet(StringComparer.Ordinal);
        var dosya = await fileStore.ReadAsync(StatePath, new RunningJobsFile(), cancellationToken);

        // YAZMA YALNIZ GEREKİYORSA. Bu uç 15 saniyede bir yoklanıyor; her yoklamada dosyayı
        // yeniden yazmak, hiçbir şey değişmese bile diske sürekli yazmak olurdu. Bayat kayıt
        // varsa (ve ancak o zaman) uzlaştırma yazması yapılır.
        if (dosya.Running.Values.Any(kayit => !IsOwnedByCurrentProcess(kayit)))
        {
            dosya = await fileStore.UpdateAsync(StatePath, new RunningJobsFile(), BayatlariUzlastir, cancellationToken);
        }

        var suren = dosya.Running
            .Where(giris => istenen.Contains(Normalize(giris.Key)))
            .Select(giris => giris.Value)
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
            .Where(giris => istenen.Contains(Normalize(giris.Key)))
            .Select(giris => giris.Value)
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
        var tur = Normalize(kind);
        var baslangic = DateTime.UtcNow;

        // KONTROL + YAZMA AYNI KİLİDİN ALTINDA. UpdateAsync dosya başına kilit tutar; "yer boş mu"
        // sorusunu ayrı bir okumayla sorsaydık iki eşzamanlı istek ikisi de boş görüp ikisi de
        // yazabilirdi — 409 koruması kâğıt üzerinde kalırdı.
        await fileStore.UpdateAsync(
            StatePath,
            new RunningJobsFile(),
            dosya =>
            {
                if (dosya.Running.TryGetValue(tur, out var mevcut) &&
                    mevcut.StartedAtUtc != default &&
                    IsOwnedByCurrentProcess(mevcut))
                {
                    throw new AppException(
                        CakismaAnahtarlari.TryGetValue(tur, out var anahtar) ? anahtar : "runningjob.backup_in_progress",
                        409,
                        409,
                        $"'{mevcut.Title}' started at {mevcut.StartedAtUtc:O} is still running.");
                }

                dosya.Running[tur] = new RunningJobState
                {
                    Kind = tur,
                    Title = title,
                    StartedAtUtc = baslangic,
                    SubjectUserId = subjectUserId is { } sahip ? sahip.ToString("N") : string.Empty,
                    OwnerProcessId = Environment.ProcessId,
                    OwnerProcessStartedAtTicks = CurrentProcessStartTicks()
                };

                // Yeni iş, aynı türün eski "kesildi" uyarısını GEÇERSİZ KILAR: kullanıcı zaten
                // yeniden başlattı, uyarıyı bir daha göstermek gürültü olurdu.
                dosya.Interrupted.Remove(tur);
                return dosya;
            },
            cancellationToken);

        return new JobScope(this, tur, baslangic);
    }

    /// <summary>
    /// İş bitti: kaydı boşaltır.
    /// <para>
    /// <b>BAŞLANGIÇ ANI KARŞILAŞTIRILIYOR:</b> yuvada başka bir işin kaydı duruyorsa (kuramsal
    /// olarak 409 bunu engeller, ama savunma ucuz) onu silmek, GERÇEKTEN süren bir işi ekrandan
    /// yok etmek olurdu. Yalnız kendi yazdığımız kayıt kaldırılır.
    /// </para>
    /// </summary>
    private Task EndAsync(string kind, DateTime startedAtUtc) =>
        fileStore.UpdateAsync(
            StatePath,
            new RunningJobsFile(),
            dosya =>
            {
                if (dosya.Running.TryGetValue(kind, out var mevcut) && mevcut.StartedAtUtc == startedAtUtc)
                {
                    dosya.Running.Remove(kind);
                }

                return dosya;
            },
            CancellationToken.None);

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
    private void TemizlemeHatasiniKaydet(string kind, Exception exception) =>
        logger.LogWarning(
            exception,
            "Çalışan-iş kaydı temizlenemedi ({Kind}); panel yeniden başladığında bayat kayıt olarak temizlenecek.",
            kind);

    /// <summary>
    /// <c>await using</c> kapsamı: iş nasıl biterse bitsin (başarı, hata, iptal) kaydı temizler.
    /// <para>
    /// Elle <c>try/finally</c> yerine kapsam kullanılıyor çünkü kaydı temizlemeyi UNUTMAK sessiz
    /// ve kalıcı bir arızadır: panel bir daha o türden iş başlatamaz (409) ve ekranda sonsuz
    /// "sürüyor" şeridi gösterir.
    /// </para>
    /// </summary>
    private sealed class JobScope(RunningJobService owner, string kind, DateTime startedAtUtc) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await owner.EndAsync(kind, startedAtUtc);
            }
            catch (Exception exception)
            {
                // TEMİZLİK HATASI İŞİN SONUCUNU EZMEZ. Buradan istisna fırlatmak, başarıyla biten
                // bir yedeği kullanıcıya "başarısız" göstermek olurdu. Kayıt diskte kalırsa panel
                // yeniden başladığında sahiplik kontrolü onu zaten "kesildi" sayıp temizleyecek —
                // yani arıza kendini onarır; sessiz kalmaması için loglanır.
                owner.TemizlemeHatasiniKaydet(kind, exception);
            }
        }
    }
}
