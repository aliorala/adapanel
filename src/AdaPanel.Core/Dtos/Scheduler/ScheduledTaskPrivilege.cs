namespace AdaPanel.Core.Dtos.Scheduler;

/// <summary>
/// Bir zamanlanmış görevin ÇAĞIRANDAN gelen yetki sınıfı: görev SYSTEM olarak koşabilir mi,
/// yoksa sınırlı kimliğe indirilmek ZORUNDA mı.
/// </summary>
/// <remarks>
/// <para>
/// <b>ÖLÇÜLEN AÇIK (bu tip onun için var).</b> <c>CronController.CreateTaskAsync</c> yönetici
/// dalında görev tipini hiç zorlamıyordu: <c>taskType="command"</c> ile gelen <c>Command</c> /
/// <c>Arguments</c> doğrudan <c>New-ScheduledTaskAction</c>'a gidiyor ve
/// <c>SchedulerService.BuildCreateScript</c> principal'i
/// <c>-UserId 'SYSTEM' -RunLevel Highest</c> yapıyordu. Yani sahip OLMAYAN bir panel yöneticisi
/// tek bir cron isteğiyle SYSTEM olarak keyfi kod çalıştırabiliyordu — terminal
/// (<c>TerminalController</c>), geri yükleme, mağaza kurulumu ve site fiziksel yolu kapatıldıktan
/// SONRA bile açık kalan dördüncü yol buydu. O yol açıkken diğer üçü kâğıt üstündedir.
/// </para>
/// <para>
/// <b>Neden istek gövdesinde DEĞİL, ayrı bir parametre:</b> bu değer çağıranın KİM OLDUĞUNDAN
/// türer (<c>IServerOwnerService</c>). İsteğin bir alanı olsaydı HTTP gövdesinden gelen JSON onu
/// doldurabilirdi ve koruma, kapattığı kişinin eline verilmiş olurdu.
/// </para>
/// <para>
/// <b>Neden varsayılanı YOK:</b> <see cref="AdaPanel.Core.Interfaces.ISchedulerService"/> üzerinde
/// bu parametre zorunludur. Varsayılan verilseydi ileride eklenecek her yeni çağıran sessizce
/// <see cref="Full"/> alırdı — yani unutmanın cezası "derleme hatası" değil "açık" olurdu.
/// </para>
/// </remarks>
public enum ScheduledTaskPrivilege
{
    /// <summary>
    /// Görev bugünkü gibi SYSTEM (<c>RunLevel Highest</c>) olarak kaydedilebilir.
    /// <para>
    /// Kimlere: sunucu SAHİBİ (terminale de girebilen tek kişi — bu uçtan yeni bir yetenek
    /// kazanmaz) ve PANELİN KENDİ iç çağrıları (otomasyon, mağaza kurulumu, site kuyruk işçisi).
    /// </para>
    /// </summary>
    Full,

    /// <summary>
    /// Kod çalıştıran görev tipleri (<c>command</c>, <c>php</c>, <c>script</c>) SINIRLI kimlikle
    /// kaydedilir (<c>NT AUTHORITY\LOCAL SERVICE</c>, <c>RunLevel Limited</c>).
    /// <para>
    /// Kimlere: sunucu sahibi OLMAYAN panel yöneticisi ve her taklit (impersonate) oturumu.
    /// </para>
    /// <para>
    /// <b>Özellik ölmez:</b> yönetici aynı görevleri kurmaya devam eder; yalnız görevin kimliği
    /// düşer. <c>url</c> tipi etkilenmez: programı ve argümanları PANEL kurar (panelin denetlenen
    /// yürütücüsü, <c>UrlTaskRunner</c>), kullanıcıdan gelen tek şey adrestir ve o adres hem
    /// kurulurken hem HER BAĞLANTIDA denetlenir — keyfi kod değildir.
    /// </para>
    /// </summary>
    Limited
}
