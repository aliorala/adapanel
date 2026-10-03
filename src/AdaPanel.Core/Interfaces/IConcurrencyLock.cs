namespace AdaPanel.Core.Interfaces;

/// <summary>
/// Aynı anda tek çalışması gereken işleri sıraya sokar.
/// <para>
/// <b>ANAHTAR VERMEK ÖNEMLİ.</b> Anahtarsız çağrılar ortak <see cref="ConcurrencyKeys.Iis"/>
/// kuyruğuna girer; o kuyruk <c>applicationHost.config</c>'e yazan servisler içindir (IIS
/// yapılandırması TEK bir dosyadır, eşzamanlı yazımda biri sessizce kaybolur). IIS ile ilgisi
/// olmayan bir iş anahtarsız çağırırsa alakasız işleri birbirine bekletir.
/// </para>
/// <para>
/// Bu gerçek bir arızaydı: otomasyon gece yedeğini bu kilidin İÇİNDE çalıştırıyordu (uzak
/// yükleme zaman aşımı <b>2 saat</b>), giriş akışı ise başarılı girişte aynı kilidi istiyordu.
/// Sonuç: yedek sürerken doğru parolayı giren yönetici bile beklemede kalıyordu — panel donmuş
/// görünüyor, hiçbir zaman aşımı da yok. Kuyruklar ayrılınca bu sınıf kapandı.
/// </para>
/// </summary>
public interface IConcurrencyLock
{
    /// <summary>Ortak IIS kuyruğu. Yalnız IIS yapılandırmasına yazanlar için.</summary>
    Task RunExclusiveAsync(Func<Task> operation, CancellationToken cancellationToken = default);

    /// <summary>Ortak IIS kuyruğu. Yalnız IIS yapılandırmasına yazanlar için.</summary>
    Task<T> RunExclusiveAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default);

    /// <summary>Adlandırılmış kuyruk: yalnız AYNI anahtarı kullanan işler birbirini bekler.</summary>
    Task RunExclusiveAsync(string key, Func<Task> operation, CancellationToken cancellationToken = default);

    /// <summary>Adlandırılmış kuyruk: yalnız AYNI anahtarı kullanan işler birbirini bekler.</summary>
    Task<T> RunExclusiveAsync<T>(string key, Func<Task<T>> operation, CancellationToken cancellationToken = default);
}

/// <summary>Panelin kuyruk adları. Yeni bir alan eklerken buraya da ekle.</summary>
public static class ConcurrencyKeys
{
    /// <summary>applicationHost.config yazanlar: site, PHP, ASP.NET, reverse proxy, app runtime.</summary>
    public const string Iis = "iis";

    /// <summary>Giriş güvenliği durumu (bruteforce.json). Girişin SICAK YOLU — uzun işlerle asla paylaşılmaz.</summary>
    public const string Auth = "auth";

    /// <summary>Zamanlanmış otomasyon turu (yedek, SSL yenileme). SAATLERCE sürebilir.</summary>
    public const string Automation = "automation";

    /// <summary>
    /// Abonelik yedek turları. Otomasyon kuyruğundan AYRI olmasının sebebi ölçülmüş bir tuzaktır:
    /// abonelik yedeği "şimdi yedekle" düğmesiyle de tetiklenir ve o bir HTTP isteğidir. Sunucu
    /// geneli gece yedeğiyle aynı kuyruğa girseydi, kullanıcının isteği saatlerce askıda kalır ve
    /// panel donmuş görünürdü. Aynı kuyruk yalnız abonelik yedeklerini birbirine sıraya sokar.
    /// </summary>
    public const string SubscriptionBackup = "subscription-backup";
}
