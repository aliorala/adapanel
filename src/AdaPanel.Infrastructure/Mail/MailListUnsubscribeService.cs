using System.Security.Cryptography;
using System.Text;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Mail;

/// <summary>
/// Dağıtım listesinden SELF-SERVİS çıkma (Plesk/Mailman paritesi): iki adımlı, e-posta ile onaylı.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden e-posta onaylı ve neden aboneye özel tek-tık DEĞİL:</b> Stalwart'ın fan-out'u iletiyi
/// kişiselleştirmiyor — teslim edilen iletide <c>List-Unsubscribe</c> başlığı YOK ve alıcıya özel
/// tek şey teslim anında eklenen <c>Delivered-To</c> (canlı ölçüldü, Stalwart 0.16.8). Bu yüzden
/// RFC 8058 tek-tık bağlantısı için gereken ABONEYE ÖZEL belirteç iletiye konulamıyor. Kalan
/// yapılabilir biçim liste başına genel bir bağlantı + e-posta onayıdır; onay olmasaydı bağlantıyı
/// gören herkes BAŞKASINI listeden atabilirdi.
/// </para>
/// <para>
/// <b>Bu sınıf KİMLİKSİZ bir uçtan çağrılır.</b> Tasarımın tamamı bunun sonucudur: adres varlığı
/// sızdırılmaz, belirteç kriptografik rastgeledir ve sunucuda yalnız ÖZETİ durur, tek kullanımlıktır,
/// süreleri vardır ve aynı adrese art arda posta gönderilmesi engellenir.
/// </para>
/// </remarks>
public sealed class MailListUnsubscribeService(
    JsonFileStore fileStore,
    IMailService mailService,
    INotificationService notificationService,
    ILogger<MailListUnsubscribeService> logger)
{
    internal const string StorePath = "config/mail-list-unsubscribe.json";

    /// <summary>
    /// Belirtecin ömrü. Kısa tutulur: bağlantı e-posta kutusunda kalıcı olarak durur ve kutuya
    /// sonradan erişen biri (paylaşılan kutu, çalınmış cihaz) onu kullanabilir.
    /// </summary>
    internal static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(24);

    /// <summary>
    /// Aynı (liste, adres) çifti için iki onay postası arasındaki asgari süre.
    /// </summary>
    /// <remarks>
    /// <b>Neden var:</b> uç kimliksizdir ve E-POSTA GÖNDERİR. Bekleme süresi olmasaydı saldırgan,
    /// kurbanın adresini yazıp isteği tekrarlayarak paneli bir spam silahına çevirebilirdi — üstelik
    /// postalar panelin KENDİ alan adından gideceği için itibar kaybı da panelin olurdu.
    /// </remarks>
    internal static readonly TimeSpan ResendCooldown = TimeSpan.FromMinutes(15);

    /// <summary>Depoda tutulan bekleyen istek. Belirtecin KENDİSİ değil, ÖZETİ saklanır.</summary>
    internal sealed class PendingUnsubscribe
    {
        public string ListAddress { get; set; } = string.Empty;

        public string Subscriber { get; set; } = string.Empty;

        public DateTimeOffset CreatedAtUtc { get; set; }

        public DateTimeOffset ExpiresAtUtc { get; set; }
    }

    /// <summary>
    /// Birinci adım. Adres gerçekten aboneyse onay postası gönderir; DEĞİLSE hiçbir şey yapmaz —
    /// her iki durumda da çağırana AYNI şey döner (bkz. denetleyici).
    /// </summary>
    /// <returns>Yalnız günlük/denetim için: onay postası gerçekten gönderildi mi.</returns>
    public async Task<bool> RequestAsync(
        string listAddress,
        string subscriber,
        string confirmUrlTemplate,
        CancellationToken cancellationToken = default)
    {
        var list = NormalizeAddress(listAddress);
        var email = NormalizeAddress(subscriber);
        if (list.Length == 0 || email.Length == 0)
        {
            return false;
        }

        // Adresin abone OLUP OLMADIĞI burada belirlenir ama çağırana YANSITILMAZ.
        if (!await IsSubscriberAsync(list, email, cancellationToken))
        {
            return false;
        }

        // SMTP yapılandırılmamışsa onay postası GÖNDERİLEMEZ. Bu durumda belirteç üretip depoya
        // yazmak, kullanıcıya asla ulaşmayacak bir bağlantı üretmek olurdu; üstelik bekleme
        // süresini de tüketip sonraki denemeleri engellerdi. Sebep AÇIKÇA günlüğe yazılır —
        // "özellik sessizce çalışmıyor" bu depodaki en pahalı arıza sınıfı.
        if (!await notificationService.IsEmailConfiguredAsync(cancellationToken))
        {
            logger.LogWarning(
                "Dağıtım listesinden çıkma onayı gönderilemedi: panelde SMTP yapılandırılmamış ({List}).",
                list);
            return false;
        }

        var store = await ReadStoreAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        Prune(store, now);

        if (!ShouldSendConfirmation(store, list, email, now))
        {
            // Bekleme süresi dolmadı: SESSİZCE geçilir. Çağırana yine aynı cevap döner, yani
            // saldırgan "bekleme devrede" bilgisini de ölçemez.
            logger.LogInformation("Liste abonelikten çıkma onayı bekleme süresi nedeniyle gönderilmedi ({List}).", list);
            return false;
        }

        var token = CreateToken();
        store[HashToken(token)] = new PendingUnsubscribe
        {
            ListAddress = list,
            Subscriber = email,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.Add(TokenLifetime)
        };

        await fileStore.WriteAsync(StorePath, store, cancellationToken);

        var url = BuildConfirmUrl(confirmUrlTemplate, token);
        await notificationService.SendEmailToAsync(
            email,
            $"Abonelikten cikma onayi / Unsubscribe confirmation: {list}",
            BuildConfirmationBody(list, email, url),
            cancellationToken);

        return true;
    }

    /// <summary>
    /// İkinci adım: belirteci harcar ve aboneyi listeden çıkarır.
    /// </summary>
    public async Task<MailListUnsubscribeResultDto> ConfirmAsync(string token, CancellationToken cancellationToken = default)
    {
        var trimmed = (token ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return Failed();
        }

        var store = await ReadStoreAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        Prune(store, now);

        var key = HashToken(trimmed);
        if (!store.TryGetValue(key, out var pending) || IsExpired(pending, now))
        {
            // Süresi geçmiş kayıt Prune ile zaten düştü; yine de burada AYRICA reddedilir ki
            // budama yapılmadığı bir yolda süresi dolmuş belirteç kabul edilmesin.
            store.Remove(key);
            await fileStore.WriteAsync(StorePath, store, cancellationToken);
            return Failed();
        }

        // TEK KULLANIMLIK: belirteç, çıkarma denemesinden ÖNCE düşürülür. Sonra düşürülseydi,
        // çıkarma hata verdiğinde belirteç kullanılabilir kalır ve tekrar tekrar denenebilirdi.
        store.Remove(key);
        await fileStore.WriteAsync(StorePath, store, cancellationToken);

        await mailService.SetMailListRecipientsAsync(
            new SetMailListRecipientsRequest
            {
                Address = pending.ListAddress,
                Remove = [pending.Subscriber]
            },
            cancellationToken);

        return new MailListUnsubscribeResultDto
        {
            Removed = true,
            List = pending.ListAddress,
            Message = "mail.unsubscribe_done"
        };
    }

    private static MailListUnsubscribeResultDto Failed() => new()
    {
        Removed = false,
        List = string.Empty,
        Message = "mail.unsubscribe_invalid_token"
    };

    /// <summary>Adres, listenin GÜNCEL abone kümesinde mi? (Panelin kopyası değil, sunucu.)</summary>
    private async Task<bool> IsSubscriberAsync(string list, string email, CancellationToken cancellationToken)
    {
        var domain = DomainOf(list);
        if (domain.Length == 0)
        {
            return false;
        }

        try
        {
            var workspace = await mailService.GetWorkspaceAsync(domain, cancellationToken);
            var target = workspace.Lists.FirstOrDefault(item =>
                string.Equals(item.Address, list, StringComparison.OrdinalIgnoreCase));

            return target is not null
                && target.Recipients.Any(item => string.Equals(item, email, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception)
        {
            // Posta sunucusu okunamıyorsa "abone değil" sayılır: kimliksiz bir uçtan gelen isteğe
            // karşılık, emin olmadan posta göndermeyiz. Sessizce yutulmaz, günlüğe yazılır.
            logger.LogWarning(exception, "Abonelik doğrulanamadı ({List}).", list);
            return false;
        }
    }

    // --- Saf yardımcılar (test edilebilir çekirdek) ---

    /// <summary>
    /// Yeni onay postası gönderilmeli mi? Aynı (liste, adres) için bekleme süresi dolmadıysa hayır.
    /// </summary>
    internal static bool ShouldSendConfirmation(
        IReadOnlyDictionary<string, PendingUnsubscribe> store,
        string list,
        string email,
        DateTimeOffset now)
    {
        foreach (var entry in store.Values)
        {
            if (string.Equals(entry.ListAddress, list, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Subscriber, email, StringComparison.OrdinalIgnoreCase)
                && now - entry.CreatedAtUtc < ResendCooldown)
            {
                return false;
            }
        }

        return true;
    }

    internal static bool IsExpired(PendingUnsubscribe pending, DateTimeOffset now) =>
        now >= pending.ExpiresAtUtc;

    /// <summary>Süresi dolmuş kayıtları atar: depo, kullanılmayan belirteçlerle sınırsız büyümemeli.</summary>
    internal static void Prune(Dictionary<string, PendingUnsubscribe> store, DateTimeOffset now)
    {
        foreach (var key in store.Where(pair => IsExpired(pair.Value, now)).Select(pair => pair.Key).ToList())
        {
            store.Remove(key);
        }
    }

    /// <summary>
    /// 256 bit kriptografik rastgele belirteç (URL-güvenli base64).
    /// </summary>
    /// <remarks>
    /// <c>Guid</c> ya da <c>Random</c> KULLANILMAZ: ikisi de tahmin edilebilirdir ve bu belirteç,
    /// kimliksiz bir uçta bir aboneyi listeden düşürmeye yeten tek kanıttır.
    /// </remarks>
    internal static string CreateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    /// <summary>
    /// Belirtecin depoda saklanan biçimi: SHA-256 özeti.
    /// </summary>
    /// <remarks>
    /// Belirtecin kendisi saklansaydı, panel yapılandırma dosyalarını okuyabilen biri (yedek,
    /// hatalı ACL) her aboneyi listeden düşürebilirdi. Özet, doğrulamaya yeter ama üretmeye yetmez.
    /// </remarks>
    internal static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Onay adresini şablondan kurar; <c>{token}</c> yer tutucusu değiştirilir.</summary>
    internal static string BuildConfirmUrl(string template, string token) =>
        (template ?? string.Empty).Contains("{token}", StringComparison.Ordinal)
            ? template!.Replace("{token}", Uri.EscapeDataString(token), StringComparison.Ordinal)
            : $"{(template ?? string.Empty).TrimEnd('?', '&')}?token={Uri.EscapeDataString(token)}";

    internal static string NormalizeAddress(string? value) =>
        (value ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();

    internal static string DomainOf(string address)
    {
        var at = address.LastIndexOf('@');
        return at < 0 || at == address.Length - 1 ? string.Empty : address[(at + 1)..];
    }

    private static string BuildConfirmationBody(string list, string email, string url) =>
        $"""
        {email} adresinin {list} dagitim listesinden cikarilmasi istendi.
        Unsubscribe from {list} was requested for {email}.

        Onaylamak icin / To confirm:
        {url}

        Bu istegi siz yapmadiysaniz bu iletiyi yok sayin; hicbir sey degismez.
        If you did not request this, ignore this message; nothing will change.
        """;

    private async Task<Dictionary<string, PendingUnsubscribe>> ReadStoreAsync(CancellationToken cancellationToken)
    {
        var stored = await fileStore.ReadAsync<Dictionary<string, PendingUnsubscribe>>(StorePath, new(), cancellationToken);
        return stored is null
            ? new Dictionary<string, PendingUnsubscribe>(StringComparer.Ordinal)
            : new Dictionary<string, PendingUnsubscribe>(stored, StringComparer.Ordinal);
    }
}
