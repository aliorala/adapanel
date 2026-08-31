using System.Net;
using System.Net.Security;
using System.Net.Sockets;

namespace AdaPanel.Infrastructure.Support;

/// <summary>
/// Adres denetimini SOKET AÇILMADAN HEMEN ÖNCE uygulayan taşıyıcı katman.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden var — <see cref="OutboundAddressGuard"/> tek başına yetmiyordu.</b> O kapı isteği
/// atmadan ÖNCE adın nereye çözüldüğünü ölçer. İki delik ölçülmüş biçimde açıktı:
/// </para>
/// <list type="number">
/// <item><b>YÖNLENDİRME.</b> <c>services.AddHttpClient()</c> ile üretilen VARSAYILAN istemcide
/// <c>AllowAutoRedirect</c> .NET varsayılanı olan <c>true</c>'dur (yalnız adlı
/// <c>AdaPanel.LocalHealth</c> ve <c>DbToolsProxy</c> istemcileri <c>false</c> yapıyordu). Yani
/// saldırgan kendi sunucusunu hedef verir, o sunucu <c>302 Location: http://169.254.169.254/</c>
/// döner ve panel iç ağa gider — kapı doğrulama anında "adres temiz" demiş olduğu hâlde.</item>
/// <item><b>DNS REBINDING.</b> Doğrulama ile isteğin atıldığı an arasında A kaydı değişebilir.
/// Doğrulamada genel bir adres görünür, bağlantıda 127.0.0.1'e gidilir.</item>
/// </list>
/// <para>
/// <b>Kalıp icat edilmedi:</b> <c>Cluster/ClusterHttpClientFactory</c> bunu zaten yapıyordu; burası
/// o kalıbın TEK KAYNAĞA çıkarılmış hâlidir ve küme de artık burayı çağırır. Kural (aralık listesi,
/// ad eleme, çözümleme) burada TEKRAR YAZILMAZ; <see cref="OutboundAddressGuard.InspectAsync"/>
/// çağrılır. Bu deponun ölçülmüş hata sınıfı tam olarak "aynı listenin ikinci nüshası"dır.
/// </para>
/// <para>
/// <b>Her bağlantı AYRI denetlenir.</b> Yönlendirme zincirinin her adımı yeni bir bağlantıdır ve
/// her biri buradan geçer; bu yüzden yönlendirmeyi kapatmak ZORUNDA değiliz (kapatsaydık meşru
/// <c>http→https</c> ve bölge yönlendirmesi yapan S3/WebDAV hedefleri kırılırdı). Havuzdan
/// yeniden kullanılan bağlantı zaten denetlenmiş bir adrese gider;
/// <see cref="SocketsHttpHandler.PooledConnectionLifetime"/> 2 dakikaya çekilerek rebinding
/// penceresi bu süreyle sınırlanır.
/// </para>
/// <para>
/// <b>Vekil KULLANILMAZ</b> (<c>UseProxy = false</c>). Sistem vekili devredeyse soket her zaman
/// vekile gider ve hedef ana makine adı vekile METİN olarak iletilir — yani bu denetim hiçbir şey
/// ölçmezdi. Bu yalnız DENETLENEN istemciler için geçerlidir; panelin kendi sabit adreslerini
/// kullanan varsayılan istemci (bileşen indirme, GitHub sürüm çözümleme) vekili kullanmaya devam
/// eder, yoksa vekil arkasındaki bir sunucuda panel kendini güncelleyemez hâle gelirdi.
/// </para>
/// </remarks>
public static class OutboundConnectGuard
{
    /// <summary>
    /// KULLANICIDAN ADRES ALAN istekler için adlı istemci
    /// (<c>IHttpClientFactory.CreateClient(OutboundConnectGuard.GuardedClientName)</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden varsayılan istemci denetlenmiyor:</b> panelin kendi sabit adresleri kullanıcıdan
    /// gelmez — bileşen indirme (components.json), GitHub sürüm çözümleme, phpMyAdmin vekili,
    /// Stalwart yönetim API'si, sağlık kontrolü ve küme. Bunları denetime sokmak hiçbir açığı
    /// kapatmaz ama bileşen indirmeyi ve panelin kendini güncellemesini kırma riski taşır
    /// (yönlendirme zinciri, CDN, vekil). Ayrım kasıtlıdır ve nöbetçi testleri vardır.
    /// </para>
    /// </remarks>
    public const string GuardedClientName = "AdaPanel.GuardedOutbound";

    /// <summary>
    /// Denetlenen bir işleyici üretir.
    /// </summary>
    /// <param name="policySelector">
    /// Politika HER BAĞLANTIDA yeniden okunur. Sabit bir değer alsaydık
    /// <c>Panel:AllowInternalOutboundTargets</c> ayarı değiştiğinde önbellekteki işleyici eski
    /// kararla yaşamaya devam ederdi — "bir yerde düzeltildi, öteki yol sessizce farklı davranıyor"
    /// hatasının tam da kendisi.
    /// </param>
    /// <param name="resolver">Testte sahte çözümleyici; üretimde gerçek DNS.</param>
    /// <param name="onBlocked">Reddedilen bağlantıyı günlüğe yazan taraf (ad, engellenen hedef).</param>
    /// <param name="verifyTlsCertificate">
    /// <c>false</c> yalnız çağıranın BİLEREK sertifika doğrulamasını kapattığı yollarda
    /// (Plesk/cPanel keşfi kendi imzalı sertifika kullanır). Adres denetimi bundan etkilenmez.
    /// </param>
    public static SocketsHttpHandler CreateHandler(
        Func<OutboundTargetPolicy> policySelector,
        OutboundAddressGuard.OutboundHostResolver? resolver = null,
        Action<string, string>? onBlocked = null,
        bool verifyTlsCertificate = true)
    {
        ArgumentNullException.ThrowIfNull(policySelector);

        var cozumleyici = resolver ?? OutboundAddressGuard.SystemResolver;

        var handler = new SocketsHttpHandler
        {
            // Yönlendirme İZLENİR ama HER ADIM aşağıdaki geri çağrıdan geçer; gerekçe sınıf
            // açıklamasında. Kapatmak meşru hedefleri kırardı, denetimsiz bırakmak açığın kendisiydi.
            AllowAutoRedirect = true,
            // Zincir uzunluğu sınırlı: yönlendirme halkası bir yükleme işini sonsuza kadar
            // döndürebilirdi (.NET varsayılanı 50).
            MaxAutomaticRedirections = 5,
            UseProxy = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = (context, cancellationToken) => ConnectAsync(
                context.DnsEndPoint,
                policySelector(),
                cozumleyici,
                onBlocked,
                cancellationToken)
        };

        if (!verifyTlsCertificate)
        {
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            };
        }

        return handler;
    }

    /// <summary>
    /// Adı ÇÖZER, çıkan adresleri denetler ve yalnız temizse soketi açar.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ayrı ve <c>public</c> olması test edilebilirlik içindir: <c>SocketsHttpConnectionContext</c>
    /// dışarıdan üretilemez, yani geri çağrı gövdesi ancak burada sınanabilir (aynı gerekçe
    /// <c>SeoHttpTransport.ConnectAsync</c>'te de yazılı).
    /// </para>
    /// <para>
    /// <b>TEK çözümleme:</b> denetlenen adresler ile bağlanılan adresler AYNI dizidir. İkinci bir
    /// çözümleme yapılsaydı, iki çözümleme arasında değişen bir kayıt (kapatmaya çalıştığımız şey)
    /// denetimi yeniden anlamsız kılardı.
    /// </para>
    /// </remarks>
    /// <param name="blockedMessage">
    /// Reddedilen bağlantının istisna metni. Küme kendi sözleşmesini
    /// (<c>cluster.address_forbidden</c>) taşır: o metin merkez panoda teşhis olarak GÖRÜNÜR ve
    /// "bağlanamadım" ile "adres yasak" ayrımını yapan tek şeydir. Verilmezse okunabilir bir
    /// varsayılan kullanılır.
    /// </param>
    public static async ValueTask<Stream> ConnectAsync(
        DnsEndPoint endpoint,
        OutboundTargetPolicy policy,
        OutboundAddressGuard.OutboundHostResolver resolver,
        Action<string, string>? onBlocked,
        CancellationToken cancellationToken,
        string? blockedMessage = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(resolver);

        var inceleme = await OutboundAddressGuard.InspectAsync(
            endpoint.Host,
            resolver,
            policy,
            cancellationToken);

        if (inceleme.BlockedTarget is not null)
        {
            onBlocked?.Invoke(endpoint.Host, inceleme.BlockedTarget);
            throw new OutboundBlockedException(endpoint.Host, inceleme.BlockedTarget, blockedMessage);
        }

        if (inceleme.Addresses.Length == 0)
        {
            // Çözülemeyen ada bağlanılamaz. Politika "çözülemedi = geçir" dese bile (müşteri cron
            // adresi gibi anonim yollarda öyle), burada bağlanacak bir adres yoktur; hata
            // ÇÖZÜMLEME hatasıdır, yasak değil — ikisini karıştırmak teşhisi bozardı.
            throw new HttpRequestException($"Ad çözülemedi: {endpoint.Host}");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            // SADECE denetlenen adreslere bağlanılır; ad ikinci kez çözülmez.
            await socket.ConnectAsync(inceleme.Addresses, endpoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

/// <summary>
/// Bağlantı ANINDA reddedilen hedef.
/// </summary>
/// <remarks>
/// <see cref="HttpRequestException"/> türetilmesi bilinçli: çağıran yollar zaten HTTP hatalarını
/// yakalıyor, yani bu istisna hiçbir yerde "yakalanmamış" kalmaz. Ayrı bir tür olması ise çağıranın
/// "ağ arızası" ile "adres yasak" arasını ayırıp kullanıcıya doğru hatayı (ve
/// <c>Panel:AllowInternalOutboundTargets</c> ipucunu) verebilmesi içindir.
/// </remarks>
public sealed class OutboundBlockedException(string host, string blockedTarget, string? message = null)
    : HttpRequestException(
        message ?? $"Giden bağlantı REDDEDİLDİ: {host} -> {blockedTarget} (iç ağ/geri döngü/bulut meta verisi).")
{
    /// <summary>İstenen ana makine adı.</summary>
    public string Host { get; } = host;

    /// <summary>Adın çözüldüğü ENGELLİ adres (ya da adın kendisi).</summary>
    public string BlockedTarget { get; } = blockedTarget;

    /// <summary>
    /// İstisna zincirinin İÇİNDEN bu istisnayı çıkarır (yoksa <c>null</c>).
    /// </summary>
    /// <remarks>
    /// <b>ÖLÇÜLDÜ — bu metot olmadan çağrı yerleri sessizce yanlış davranıyordu:</b> .NET,
    /// <c>ConnectCallback</c>'ten atılan istisnayı KENDİ <see cref="HttpRequestException"/>'ının
    /// İÇİNE sarar (<c>HttpConnectionPool.ConnectToTcpHostAsync</c>). Yani
    /// <c>catch (OutboundBlockedException)</c> yazan bir çağrı yeri HİÇ tetiklenmez; hata "ağ
    /// arızası" gibi görünür ve kullanıcı adres yasağını asla öğrenemez. Bu yüzden çağrı yerleri
    /// zinciri bu metotla tarar.
    /// </remarks>
    public static OutboundBlockedException? Cikar(Exception? exception)
    {
        for (var mevcut = exception; mevcut is not null; mevcut = mevcut.InnerException)
        {
            if (mevcut is OutboundBlockedException blocked)
            {
                return blocked;
            }
        }

        return null;
    }
}
