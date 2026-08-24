using AdaPanel.Core.Dtos.RunningJobs;

namespace AdaPanel.Core.Interfaces;

/// <summary>
/// UZUN SÜREN işlerin sunucu tarafındaki kaydı: yedekleme, geri yükleme, göç ve bileşen kurulumu.
/// <para>
/// <b>NEDEN VAR:</b> bu işlerin hiçbiri işi arka plana almıyor — HTTP isteği iş bitene kadar açık
/// kalıyor. Kullanıcı sayfayı yenilediğinde ya da sekmeyi kapattığında iş sunucuda DEVAM ediyor
/// ama panelde "sürüyor" bilgisi tamamen kayboluyordu: ekran boş görünüyor, kullanıcı işi ikinci
/// kez tetikliyordu (iki saatlik ikinci bir yedek, ya da aynı siteye ikinci bir göç). Bu kayıt o
/// boşluğu kapatır — iş başlarken diske yazılır, bitince silinir, arada modülün <c>running</c>
/// ucundan okunur.
/// </para>
/// <para>
/// <b>NEDEN <see cref="IOperationProgressService"/> KULLANILMADI</b> (okundu, uymuyor): o servis
/// TEK YUVALIDIR — <c>Begin()</c> her çağrıldığında önceki işlemin adımlarını siler. Burada dört
/// ayrı iş türü var ve bir yedek sürerken yönetici PHP kurmaya kalkabilir; tek yuva ikisini
/// birbirine ezdirirdi. Ayrıca o servis yalnız BELLEKTEDİR: panel yeniden başlarsa yarıda kalan
/// işin izi tamamen kaybolur ve kullanıcıya "yedeğim neden yok" sorusunun cevabı verilemez.
/// </para>
/// <para>
/// <b>DESEN KAYNAĞI:</b> antivirüs taraması için aynı sorun 0.6.6'da çözüldü
/// (<c>Infrastructure/Antivirus/AntivirusRunningScanState.cs</c>). Buradaki sahiplik kontrolü —
/// panel PID + süreç başlangıç tiki — oradan birebir alınmıştır; yeni bir mekanizma icat
/// edilmemiştir.
/// </para>
/// </summary>
public interface IRunningJobService
{
    /// <summary>
    /// Verilen türlerin çalışan-iş durumunu döndürür ve BAYAT kayıtları yerinde uzlaştırır
    /// (sahibi artık yaşamayan bir kayıt "sürüyor" değil, "kesildi" olarak yüzeye çıkar).
    /// </summary>
    /// <param name="kinds">Okunacak iş türleri (<c>backup</c>, <c>restore</c>, …).</param>
    /// <param name="allowedSubjectUserIds">
    /// Kapsamlı kullanıcının (müşteri/bayi) erişebildiği abonelik kimlikleri ("N" biçiminde).
    /// <c>null</c> = kapsamsız rol (yönetici), her iş görünür. Boş küme = hiçbir iş görünmez.
    /// <para>
    /// KİRACI İZOLASYONU: iş etiketleri site adı / yedek adı taşır. Süzgeç olmasaydı bir müşteri,
    /// başka bir müşterinin sitesinin yedeklendiğini panelin şeridinden okuyabilirdi.
    /// </para>
    /// </param>
    Task<RunningJobsStatusDto> GetStatusAsync(
        IReadOnlyCollection<string> kinds,
        IReadOnlySet<string>? allowedSubjectUserIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// İşi kaydeder ve kaydı temizleyecek bir kapsam döndürür (<c>await using</c> ile kullanılır).
    /// <para>
    /// Aynı türden CANLI bir iş varsa <c>409</c> ile <see cref="Common.AppException"/> fırlatır:
    /// tek yuvalı kayda ikinci bir iş yazmak birincinin izini silerdi ve panel bittiğinde
    /// hangisinin bittiğini söyleyemezdi.
    /// </para>
    /// </summary>
    /// <param name="subjectUserId">
    /// İşin ÜZERİNDE yapıldığı kaynağın sahibi (abonelik kimliği); sunucu geneli işlerde
    /// <c>null</c> — o işler yalnız yöneticide görünür.
    /// </param>
    Task<IAsyncDisposable> BeginAsync(
        string kind,
        string title,
        Guid? subjectUserId,
        CancellationToken cancellationToken = default);
}
