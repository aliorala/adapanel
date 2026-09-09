using System.Net;
using System.Net.Sockets;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Scheduler;

/// <summary>
/// <c>url</c> tipli zamanlanmış görevin GERÇEK yürütücüsü: <c>AdaPanel.Api.exe --cron-url &lt;adres&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>NEDEN <c>curl.exe</c> BIRAKILDI — ÖLÇÜLEN AÇIK.</b> Görev eskiden şu komutla kuruluyordu:
/// </para>
/// <code>
/// curl.exe -fsSL --proto "=http,https" --proto-redir "=http,https" --max-redirs 5 --max-time 300 --retry 2 "&lt;adres&gt;"
/// </code>
/// <para>
/// Adres, görev KURULURKEN <see cref="OutboundAddressGuard"/>'dan geçiyordu; yani
/// <c>http://169.254.169.254/…</c> ya da <c>http://127.0.0.1:8890/…</c> doğrudan yazılamıyordu.
/// Ama <c>-L</c> ile yönlendirmeyi curl'ün KENDİSİ izliyor ve <b>curl'de adres engel listesi
/// YOKTUR</b>. Bu makinede curl 8.19.0 ve gerçek bir loopback sunucusuyla ÖLÇÜLDÜ: yukarıdaki
/// birebir argümanlarla çağrılan curl, <c>302 Location:</c> ile gösterilen İÇ adrese gitti ve iç
/// gövdeyi çıktı olarak yazdı (çıkış kodu 0). Yani kullanıcı dışarıdan erişilebilir, tamamen meşru
/// bir adres verir; o adres yönlendirir; görev SYSTEM olarak panelin kendi API'sine ya da bulut
/// meta veri servisine bağlanır. Kapı yalnız İLK adresi gördüğü için yönlendirmeyi hiç denetlemez.
/// </para>
/// <para>
/// <b>Neden curl bayraklarıyla kapatılamadı (hepsi ölçüldü):</b>
/// </para>
/// <list type="bullet">
/// <item><c>-L</c>'yi ATMAK: ölçüldü — <c>-L</c> olmadan 302 alan curl çıkış kodu <b>0</b> ve
/// <b>BOŞ</b> çıktı veriyor. Yani <c>http→https</c> ya da <c>www</c> yönlendirmesi olan her meşru
/// cron adresi "başarılı" görünürken hiçbir şey çalıştırmayacaktı. Sessizce çalışmayan bir cron,
/// hata veren bir cron'dan pahalıdır.</item>
/// <item><c>--proto-redir "=http,https"</c>: GEREKLİ ve KORUNDU (aşağıda karşılığı var) ama
/// yalnızca ŞEMA yükseltmesini keser. Ölçüldü: <c>file://</c> yönlendirmesi bununla reddedildi.
/// <c>http→http</c> yönlendirmesiyle iç ağa sapmaya hiçbir etkisi yok.</item>
/// <item><c>--resolve</c>: yalnız YAZILAN ana makine adını sabitler. Yönlendirme BAŞKA bir ada
/// gider; o ad hiç sabitlenmemiştir, curl onu normal çözer. Yönlendirmeye çare DEĞİL.</item>
/// </list>
/// <para>
/// <b>SEÇİLEN ÇÖZÜM:</b> görevi curl yerine panelin KENDİ denetlenen taşıyıcısıyla çalıştırmak.
/// <see cref="OutboundConnectGuard.CreateHandler"/>'ın <c>ConnectCallback</c>'i HER BAĞLANTIDA
/// çalışır — yönlendirme zincirinin her adımı yeni bir bağlantıdır, dolayısıyla her adım denetlenir.
/// Bu tek hamle İKİ açığı birden kapatır:
/// </para>
/// <list type="number">
/// <item><b>YÖNLENDİRME.</b> Zincirin her adımı kapıdan geçer; yönlendirmeyi KAPATMAK zorunda
/// kalmayız, yani meşru <c>http→https</c>/<c>www</c> yönlendirmeleri çalışmaya devam eder.</item>
/// <item><b>DNS REBINDING.</b> Eski yolda denetim görev KURULURKEN yapılıyordu; curl ise HER
/// ÇALIŞMADA adı yeniden çözüyordu — arada değişen bir A kaydı denetimin arkasından geçerdi.
/// Artık denetim ÇALIŞMA ANINDA yapılıyor ve
/// <see cref="OutboundAddressGuard.InspectAsync"/> adı BİR KEZ çözüp soketi TAM O ADRESLERE
/// açıyor; denetlenen adres ile bağlanılan adres aynı dizidir.</item>
/// </list>
/// <para>
/// <b>Kural burada TEKRAR YAZILMADI.</b> Aralık listesi, ad eleme ve çözümleme
/// <see cref="OutboundAddressGuard"/>'dadır; bu sınıf yalnız taşıyıcıyı kurar. Bu deponun ölçülmüş
/// hata sınıfı tam olarak "aynı listenin ikinci nüshası"dır.
/// </para>
/// <para>
/// <b>Kurulum anındaki denetim KALDIRILMADI</b> (<c>SchedulerService.EnsureTaskUrlTargetAllowedAsync</c>):
/// kullanıcıya "adres yasak" hatasını görev listesinde bir çıkış kodu olarak değil, KAYDET
/// düğmesine bastığı anda ve anlaşılır bir mesajla vermek gerekir. İkisi aynı gövdeyi çağırır.
/// </para>
/// </remarks>
public static class UrlTaskRunner
{
    /// <summary>
    /// Görevin çağırdığı komut anahtarı. TEK KAYNAK: hem komutu KURAN taraf
    /// (<c>SchedulerService.ResolveAction</c>) hem de onu KARŞILAYAN taraf
    /// (<c>AdaPanel.Api</c> içindeki <c>CronUrlKomutu</c>) buradan okur.
    /// </summary>
    /// <remarks>
    /// İkisi ayrı ayrı yazılsaydı biri değiştiğinde diğeri sessizce eşleşmeyi kaybederdi: görev
    /// kurulur, çalışır ve panel argümanı tanımadığı için WEB SUNUCUSUNU açmaya kalkardı — SYSTEM
    /// olarak ikinci bir panel örneği. Sabitin tek nüsha olması bu senaryoyu imkânsız kılar.
    /// </remarks>
    public const string Verb = "--cron-url";

    /// <summary>Görevi çalıştıracak programın dosya adı (panelin kendi yürütülebiliri).</summary>
    public const string ExecutableName = "AdaPanel.Api.exe";

    // ÇIKIŞ KODLARI: curl'ün kodlarıyla KASITLI olarak eşleştirildi. Kullanıcıların görev
    // izleme/uyarı betikleri bugün curl kodlarına bakıyor; kodları değiştirmek, açığı kapatırken
    // sessizce izlemeyi bozmak olurdu.
    /// <summary>Başarılı (curl: 0).</summary>
    public const int BasariliKod = 0;

    /// <summary>Adres biçimi kabul edilmedi (curl: 2, CURLE_FAILED_INIT).</summary>
    public const int GecersizAdresKod = 2;

    /// <summary>Ana makine adı çözülemedi (curl: 6, CURLE_COULDNT_RESOLVE_HOST).</summary>
    public const int CozulemediKod = 6;

    /// <summary>Bağlanılamadı (curl: 7, CURLE_COULDNT_CONNECT).</summary>
    public const int BaglanamadiKod = 7;

    /// <summary>HTTP durumu 400+ (curl -f: 22, CURLE_HTTP_RETURNED_ERROR).</summary>
    public const int HttpHataKod = 22;

    /// <summary>Süre aşıldı (curl: 28, CURLE_OPERATION_TIMEDOUT).</summary>
    public const int SureAsimiKod = 28;

    /// <summary>Çok fazla yönlendirme (curl: 47, CURLE_TOO_MANY_REDIRECTS).</summary>
    public const int CokYonlendirmeKod = 47;

    /// <summary>
    /// Adres panelin engel listesinde (iç ağ / geri döngü / bulut meta verisi).
    /// </summary>
    /// <remarks>
    /// <b>curl'ün ÜRETEMEYECEĞİ bir kod bilerek seçildi.</b> Bu olay curl'de var olmayan bir
    /// karardır; var olan bir curl koduna bindirilseydi ("bağlanamadı" gibi) işletici gerçek bir ağ
    /// arızası ile kasıtlı bir engeli ayırt edemezdi. Görev listesinde bu kodu gören kişi tek bir
    /// şey aramalı: adresin (ya da yönlendirmesinin) iç bir adrese bakması.
    /// </remarks>
    public const int AdresYasakKod = 93;

    /// <summary>Yönlendirme üst sınırı: .NET tarafındaki denetlenen istemciyle AYNI (curl: --max-redirs 5).</summary>
    private const int EnCokYonlendirme = 5;

    /// <summary>Tek aktarım için üst süre (curl: --max-time 300).</summary>
    private static readonly TimeSpan EnCokSure = TimeSpan.FromSeconds(300);

    /// <summary>Geçici arızada yeniden deneme sayısı (curl: --retry 2).</summary>
    private const int YenidenDenemeSayisi = 2;

    /// <summary>
    /// Görevi kuran tarafın çağırdığı yüzey: hangi program, hangi argümanlar.
    /// </summary>
    /// <remarks>
    /// Adres TIRNAK İÇİNDE ve EN SONDA duruyor. İkisi de zorunlu: panel görev listesinde URL'yi
    /// argümanların SON tırnaklı parçasından çıkarır (<c>SchedulerService.GetTasksAsync</c>).
    /// Sıra bozulsaydı URL sütunu boşalır, düzenleme ekranı yanlış dolar ve kullanıcı kaydettiğinde
    /// görev adresini KAYBEDERDİ.
    /// </remarks>
    public static (string Execute, string Arguments) BuildAction(string url, string? panelExecutable = null)
        => (panelExecutable ?? ResolvePanelExecutable(), $"{Verb} \"{url.Trim()}\"");

    /// <summary>
    /// Panelin kendi yürütülebilirinin TAM yolu.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Environment.ProcessPath</c> ÖNCE denenir çünkü panel servisi tam da o dosyadır; ad
    /// beklenenle eşleşmezse (birim testleri <c>testhost.exe</c> içinde koşar, tepsi programı
    /// başka bir süreçten çağırabilir) uygulama tabanına düşülür. İkisi de üretimde AYNI dosyayı
    /// gösterir; ayrım yalnız "hangi süreç soruyor" farkını tolere etmek içindir.
    /// </para>
    /// <para>
    /// <b>Yol GÖREVE YAZILIR, PATH'e güvenilmez.</b> Görev SYSTEM olarak koşar ve SYSTEM'in PATH'i
    /// bir kullanıcı oturumununkinden farklıdır; ayrıca PATH'ten çözülen bir program adı, PATH'e
    /// yazma hakkı olan birinin görevin ne çalıştıracağını değiştirmesi demektir.
    /// </para>
    /// </remarks>
    public static string ResolvePanelExecutable()
    {
        var surecYolu = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(surecYolu) &&
            string.Equals(Path.GetFileName(surecYolu), ExecutableName, StringComparison.OrdinalIgnoreCase))
        {
            return surecYolu;
        }

        return Path.Combine(AppContext.BaseDirectory, ExecutableName);
    }

    /// <summary>
    /// Kayıtlı bir görev eylemi <c>url</c> tipinde mi.
    /// </summary>
    /// <remarks>
    /// <b>ESKİ GÖREVLER DE TANINIR.</b> Panel güncellenmeden önce kurulmuş görevler hâlâ
    /// <c>curl.exe</c> çağırıyor. Yalnız yeni biçim tanınsaydı o görevler listede "command" tipine
    /// düşer, URL sütunu boşalır ve kullanıcı düzenleyip kaydettiğinde görev ADRESİNİ KAYBEDERDİ —
    /// yani bir güvenlik düzeltmesi, kullanıcının verisini silen bir hataya dönüşürdü. Eski görev
    /// düzenlenip kaydedildiğinde <see cref="BuildAction"/> onu yeni biçime taşır.
    /// </remarks>
    public static bool IsUrlAction(string? execute, string? arguments)
    {
        var komut = execute ?? string.Empty;

        if (komut.EndsWith("curl.exe", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return komut.EndsWith(ExecutableName, StringComparison.OrdinalIgnoreCase) &&
               (arguments ?? string.Empty).Contains(Verb, StringComparison.Ordinal);
    }

    /// <summary>
    /// Adresi DENETLENEN taşıyıcıyla çeker; gövdeyi <paramref name="cikti"/>'ya yazar ve curl
    /// uyumlu bir çıkış kodu döner.
    /// </summary>
    /// <param name="policySelector">
    /// Politika HER BAĞLANTIDA yeniden okunur (<see cref="OutboundConnectGuard.CreateHandler"/>).
    /// </param>
    /// <param name="resolver">Testte sahte çözümleyici; üretimde gerçek DNS.</param>
    public static async Task<int> RunAsync(
        string? url,
        Func<OutboundTargetPolicy> policySelector,
        TextWriter cikti,
        TextWriter hata,
        OutboundAddressGuard.OutboundHostResolver? resolver = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policySelector);
        ArgumentNullException.ThrowIfNull(cikti);
        ArgumentNullException.ThrowIfNull(hata);

        var adresMetni = (url ?? string.Empty).Trim();

        // ŞEMA BURADA DA DENETLENİR. Kurulum tarafı zaten yalnız http/https kabul ediyor
        // (SchedulerService.IsAcceptableTaskUrl); buradaki denetim onun KOPYASI değil, bu sürecin
        // KENDİ girdisinin denetimidir: bu program komut satırından da çağrılabilir ve
        // "kurulumda denetlendi" varsayımı, denetimi kuran taraftan çalıştıran tarafa taşınan
        // sessiz bir borç olurdu. curl'deki --proto "=http,https" karşılığı da budur.
        if (!Uri.TryCreate(adresMetni, UriKind.Absolute, out var adres) ||
            (adres.Scheme != Uri.UriSchemeHttp && adres.Scheme != Uri.UriSchemeHttps))
        {
            await hata.WriteLineAsync($"AdaPanel: gecersiz adres (yalniz http/https): {adresMetni}");
            return GecersizAdresKod;
        }

        using var handler = OutboundConnectGuard.CreateHandler(
            policySelector,
            resolver,
            onBlocked: (host, blocked) => hata.WriteLine(
                $"AdaPanel: giden baglanti REDDEDILDI: {host} -> {blocked} (ic ag/geri dongu/bulut meta verisi)."));

        // Yönlendirme İZLENİR (curl -L karşılığı) ama HER ADIM yukarıdaki ConnectCallback'ten
        // geçer. Üst sınır curl'deki --max-redirs 5 ile aynı.
        handler.AllowAutoRedirect = true;
        handler.MaxAutomaticRedirections = EnCokYonlendirme;

        using var istemci = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = EnCokSure
        };

        // curl kimliğini KORU: bazı siteler User-Agent'sız isteği reddeder ve görev, adres
        // değişmediği hâlde 403 vermeye başlardı.
        istemci.DefaultRequestHeaders.UserAgent.ParseAdd("AdaPanel-Cron/1.0");

        var gecikme = TimeSpan.FromSeconds(1);
        for (var deneme = 0; ; deneme++)
        {
            var (kod, yenidenDene) = await TekDenemeAsync(istemci, adres, cikti, hata, cancellationToken);
            if (kod == BasariliKod || !yenidenDene || deneme >= YenidenDenemeSayisi)
            {
                return kod;
            }

            // curl --retry ile aynı kalıp: artan bekleme. İPTAL EDİLEBİLİR bekleme kullanılıyor,
            // yoksa durdurulan bir görev beklemeyi sonuna kadar tüketirdi.
            try
            {
                await Task.Delay(gecikme, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return SureAsimiKod;
            }

            gecikme += gecikme;
        }
    }

    /// <summary>
    /// Tek bir çekme denemesi: çıkış kodu ve "bu hata geçici mi" kararı.
    /// </summary>
    /// <remarks>
    /// <b>ADRES YASAĞI ASLA YENİDEN DENENMEZ.</b> Yasak kalıcı bir karardır; yeniden denemek üç kez
    /// aynı reddi günlüğe yazmak ve görevi boşuna geciktirmekten başka bir şey yapmazdı. Aynı şekilde
    /// 4xx de geçici değildir (429 hariç — o AÇIKÇA "sonra tekrar dene" demektir).
    /// </remarks>
    private static async Task<(int Kod, bool YenidenDene)> TekDenemeAsync(
        HttpClient istemci,
        Uri adres,
        TextWriter cikti,
        TextWriter hata,
        CancellationToken cancellationToken)
    {
        try
        {
            using var yanit = await istemci.GetAsync(adres, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!yanit.IsSuccessStatusCode)
            {
                // curl -f davranışı: gövde YAZILMAZ, hata satırı yazılır, çıkış kodu 22.
                await hata.WriteLineAsync(
                    $"AdaPanel: HTTP {(int)yanit.StatusCode} {yanit.ReasonPhrase} — {adres}");

                var gecici = (int)yanit.StatusCode >= 500 ||
                             yanit.StatusCode == HttpStatusCode.RequestTimeout ||
                             yanit.StatusCode == HttpStatusCode.TooManyRequests;
                return (HttpHataKod, gecici);
            }

            // Gövde AKTARILARAK yazılır, belleğe alınmaz: bir cron adresi büyük bir dosya
            // döndürebilir ve tamamını dizeye okumak paneli bellek hatasına sokardı.
            await using var akis = await yanit.Content.ReadAsStreamAsync(cancellationToken);
            using var okuyucu = new StreamReader(akis);
            var tampon = new char[8192];
            int okunan;
            while ((okunan = await okuyucu.ReadAsync(tampon, cancellationToken)) > 0)
            {
                await cikti.WriteAsync(tampon.AsMemory(0, okunan), cancellationToken);
            }

            await cikti.FlushAsync(cancellationToken);
            return (BasariliKod, false);
        }
        catch (Exception istisna) when (OutboundBlockedException.Cikar(istisna) is { } engellenen)
        {
            // .NET, ConnectCallback'ten atılan istisnayı KENDİ HttpRequestException'ının İÇİNE sarar;
            // bu yüzden zincir taranır (gerekçe OutboundBlockedException.Cikar üzerinde).
            await hata.WriteLineAsync(
                $"AdaPanel: adres YASAK: {engellenen.Host} -> {engellenen.BlockedTarget}");
            return (AdresYasakKod, false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient.Timeout aşıldığında OperationCanceledException atılır; çağıranın iptali
            // ile karıştırılmaması için token AYRICA sorgulanıyor.
            await hata.WriteLineAsync($"AdaPanel: sure asildi ({EnCokSure.TotalSeconds:0} sn) — {adres}");
            return (SureAsimiKod, true);
        }
        catch (OperationCanceledException)
        {
            await hata.WriteLineAsync($"AdaPanel: islem iptal edildi — {adres}");
            return (SureAsimiKod, false);
        }
        catch (HttpRequestException istisna)
        {
            var kod = SinifKodu(istisna);
            await hata.WriteLineAsync($"AdaPanel: {istisna.Message} — {adres}");

            // Ağ arızası ve bağlanamama GEÇİCİ sayılır (curl --retry ile aynı); "çok yönlendirme"
            // ise sunucunun kalıcı bir yapılandırma hatasıdır, denemek yalnız gecikme üretir.
            return (kod, kod is BaglanamadiKod or CozulemediKod);
        }
    }

    /// <summary>İstisnayı curl uyumlu bir çıkış koduna çevirir.</summary>
    private static int SinifKodu(HttpRequestException istisna)
    {
        // Yönlendirme tavanı .NET'te ayrı bir hata türü değil; mesajdan değil, İÇ istisnadan
        // ayırt edilemediği için önce soket hatalarına bakılır.
        for (var mevcut = (Exception?)istisna; mevcut is not null; mevcut = mevcut.InnerException)
        {
            if (mevcut is SocketException soket)
            {
                return soket.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData
                    ? CozulemediKod
                    : BaglanamadiKod;
            }
        }

        if (istisna.Message.Contains("Ad çözülemedi", StringComparison.Ordinal))
        {
            // OutboundConnectGuard, çözülemeyen adı bu metinle bildirir (adres yasağından AYRI bir olay).
            return CozulemediKod;
        }

        if (istisna.Message.Contains("redirect", StringComparison.OrdinalIgnoreCase))
        {
            return CokYonlendirmeKod;
        }

        return BaglanamadiKod;
    }
}
