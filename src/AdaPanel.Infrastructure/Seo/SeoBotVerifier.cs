using System.Diagnostics;
using AdaPanel.Core.Dtos.Seo;

namespace AdaPanel.Infrastructure.Seo;

/// <summary>Kullanıcı aracısının bir motor adı taşıdığı TEK bir IP — henüz hüküm yok.</summary>
public readonly record struct SeoBotClaim(string Ip, SeoBotEngine Engine, long Requests);

/// <summary>
/// Bir IP hakkındaki hüküm. <b>Değerler dile bağımsızdır</b>; cümleyi arayüz kurar.
/// </summary>
public static class SeoBotVerdict
{
    /// <summary>Ters DNS motora ait VE ileri çözüm aynı IP'yi verdi.</summary>
    public const string Verified = "verified";

    /// <summary>Ters DNS çözüldü ama motora ait değil (ya da ileri teyit tutmadı) — kanıtlı sahte.</summary>
    public const string Spoofed = "spoofed";

    /// <summary>Ters DNS cevap vermedi. <b>Hüküm yok</b> — DNS'in susması sahtelik kanıtı değildir.</summary>
    public const string Unresolved = "unresolved";

    /// <summary>Bu motor kimliğini ters DNS ile doğrulatmıyor (yayımlanmış IP listesi kullanır).</summary>
    public const string Unsupported = "unsupported";

    /// <summary>Doğrulama istenmedi ya da bütçe dolduğu için bu IP'ye sıra gelmedi.</summary>
    public const string NotChecked = "not_checked";
}

/// <summary>
/// Ters/ileri DNS çözümü. Ayrı arayüz olmasının sebebi TEST EDİLEBİLİRLİKTİR: gerçek DNS'e bağlı
/// bir test, ağın durumuna göre rastgele kırmızı verirdi.
/// </summary>
public interface ISeoReverseDnsResolver
{
    /// <summary>IP'nin ters DNS adı; çözülemezse boş.</summary>
    Task<string> ReverseAsync(string ip, CancellationToken cancellationToken);

    /// <summary>Ana bilgisayar adının ileri çözümünden gelen IP'ler; çözülemezse boş.</summary>
    Task<IReadOnlyList<string>> ForwardAsync(string hostName, CancellationToken cancellationToken);
}

/// <summary>
/// .NET'in kendi çözücüsü. Sistemin yapılandırılmış DNS sunucusunu kullanır; panel kendi DNS
/// istemcisini KURMAZ.
/// </summary>
public sealed class SeoReverseDnsResolver : ISeoReverseDnsResolver
{
    public async Task<string> ReverseAsync(string ip, CancellationToken cancellationToken)
    {
        try
        {
            var entry = await System.Net.Dns.GetHostEntryAsync(ip, cancellationToken);
            return entry.HostName ?? string.Empty;
        }
        catch (Exception exception) when (exception is System.Net.Sockets.SocketException or ArgumentException or OperationCanceledException)
        {
            // Çözülemedi. Bu, "sahte" DEĞİL "bilinmiyor" demektir; hüküm çağırana bırakılır.
            return string.Empty;
        }
    }

    public async Task<IReadOnlyList<string>> ForwardAsync(string hostName, CancellationToken cancellationToken)
    {
        try
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(hostName, cancellationToken);
            return addresses.Select(address => address.ToString()).ToArray();
        }
        catch (Exception exception) when (exception is System.Net.Sockets.SocketException or ArgumentException or OperationCanceledException)
        {
            return [];
        }
    }
}

/// <summary>
/// SAHTE BOT DOĞRULAMASI — ileri teyitli ters DNS (FCrDNS).
/// </summary>
/// <remarks>
/// <para>
/// <b>NEDEN KULLANICI ARACISI TEK BAŞINA HİÇBİR ŞEY KANITLAMAZ:</b> <c>User-Agent</c> istemcinin
/// yazdığı serbest bir metindir. İçerik hırsızları ve zafiyet tarayıcıları "Googlebot" yazarak
/// hız sınırlarını ve bot engellerini aşar. Panel bu başlığa bakıp "Googlebot 12.000 kez geldi"
/// deseydi, kullanıcı gerçekte bir kazıyıcıya ait trafiği SEO başarısı sanardı.
/// </para>
/// <para>
/// <b>ÜÇ ADIMLI KURAL</b> (Google, Bing, Yandex, Baidu ve Apple'ın kendi tarif ettiği yöntem):
/// </para>
/// <list type="number">
/// <item>IP'nin TERS kaydı çözülür.</item>
/// <item>Ad, motorun resmi son ekiyle bitmeli (<c>.googlebot.com</c> gibi; nokta ile başlar ki
/// <c>evilgooglebot.com</c> geçmesin).</item>
/// <item>O adın İLERİ çözümü BAŞLANGIÇTAKİ IP'yi vermeli. Bu adım atlanamaz: ters DNS kaydını
/// saldırgan kendi ağında istediği gibi yazabilir, ileri kaydı ise yazamaz.</item>
/// </list>
/// <para>
/// <b>NE ZAMAN HÜKÜM VERİLMEZ:</b> ters DNS cevap vermezse (<c>unresolved</c>), motor bu yöntemi
/// desteklemiyorsa (<c>unsupported</c>) ya da bütçe dolduysa (<c>not_checked</c>). Bu üçü "sahte"
/// SAYILMAZ. Ölçülmemiş bir şeyi iddia etmek, kullanıcıyı var olmayan bir saldırı için engelleme
/// kuralı yazmaya iterdi.
/// </para>
/// </remarks>
public sealed class SeoBotVerifier(ISeoReverseDnsResolver resolver)
{
    /// <summary>
    /// Adayları sırayla doğrular ve özet üretir.
    /// </summary>
    /// <param name="claims">En çok istek atandan başlayarak sıralı adaylar.</param>
    /// <param name="verify">
    /// Kapalıysa HİÇ ağ isteği yapılmaz ve her aday <c>not_checked</c> olur; rapor bunu
    /// <c>Performed=false</c> ile açıkça söyler.
    /// </param>
    public async Task<SeoLogVerificationDto> VerifyAsync(
        IReadOnlyList<SeoBotClaim> claims,
        bool verify,
        CancellationToken cancellationToken)
    {
        if (!verify || claims.Count == 0)
        {
            return new SeoLogVerificationDto
            {
                Performed = false,
                CandidateIps = claims.Count,
                UndeterminedIps = claims.Count
            };
        }

        var saat = Stopwatch.StartNew();
        var hukumler = new List<SeoLogBotIdentityDto>();
        var dogrulanan = 0;
        var sahte = 0;
        var belirsiz = 0;
        var sorulan = 0;

        foreach (var claim in claims)
        {
            // ÜÇ AYRI KAPI: iptal, toplam bütçe, IP sayısı. Tek kapı yeterli olmazdı — cevap
            // vermeyen bir DNS sunucusunda "25 IP" sınırı 25 x 2 saniye eder.
            if (cancellationToken.IsCancellationRequested ||
                saat.Elapsed >= SeoLogLimits.VerificationBudget ||
                sorulan >= SeoLogLimits.MaxVerifiedIps)
            {
                // SORULMADIĞI DA KAYDA GEÇER. Sessizce atlamak, listeyi "hepsi bu kadar" gibi
                // gösterip bütçenin dolduğunu gizlemek olurdu.
                belirsiz++;
                hukumler.Add(Hukum(claim, SeoBotVerdict.NotChecked, string.Empty));
                continue;
            }

            if (claim.Engine.ReverseDnsSuffixes.Count == 0)
            {
                belirsiz++;
                hukumler.Add(Hukum(claim, SeoBotVerdict.Unsupported, string.Empty));
                continue;
            }

            sorulan++;
            using var sure = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            sure.CancelAfter(SeoLogLimits.ReverseDnsTimeout);

            var ad = await resolver.ReverseAsync(claim.Ip, sure.Token);
            if (string.IsNullOrWhiteSpace(ad))
            {
                belirsiz++;
                hukumler.Add(Hukum(claim, SeoBotVerdict.Unresolved, string.Empty));
                continue;
            }

            if (!SeoBotCatalog.MatchesReverseDns(claim.Engine, ad))
            {
                sahte++;
                hukumler.Add(Hukum(claim, SeoBotVerdict.Spoofed, ad));
                continue;
            }

            // İLERİ TEYİT: ters kayıt doğru görünüyor, ama gerçekten o mu?
            //
            // KENDİ ZAMAN AŞIMIYLA: ters sorgunun kalanını kullansaydı, ters sorgu 1,9 saniye
            // sürdüğünde ileri sorguya 0,1 saniye kalırdı ve GERÇEK Googlebot sistematik olarak
            // "doğrulanamadı" çıkardı — yani doğrulama, yavaş DNS'te işe yaramaz olurdu.
            using var ileriSure = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            ileriSure.CancelAfter(SeoLogLimits.ReverseDnsTimeout);
            var ileri = await resolver.ForwardAsync(ad, ileriSure.Token);
            if (ileri.Contains(claim.Ip, StringComparer.OrdinalIgnoreCase))
            {
                dogrulanan++;
                hukumler.Add(Hukum(claim, SeoBotVerdict.Verified, ad));
            }
            else if (ileri.Count == 0)
            {
                // İleri çözüm cevap vermedi: ters kayıt doğru olsa bile teyit YOK. "Doğrulandı"
                // demek, zincirin yarısını atlayıp tam kanıt iddia etmek olurdu.
                belirsiz++;
                hukumler.Add(Hukum(claim, SeoBotVerdict.Unresolved, ad));
            }
            else
            {
                sahte++;
                hukumler.Add(Hukum(claim, SeoBotVerdict.Spoofed, ad));
            }
        }

        return new SeoLogVerificationDto
        {
            Performed = true,
            CandidateIps = claims.Count,
            CheckedIps = sorulan,
            VerifiedIps = dogrulanan,
            SpoofedIps = sahte,
            UndeterminedIps = belirsiz,
            // Sahteler ÖNCE: ekranda ilk görülmesi gereken kalem odur.
            //
            // ÜST SINIR SORULAN IP SAYISINDAN BÜYÜK OLMALI: sorulan IP sınırı zaten
            // MaxVerifiedIps'tir; liste onunla AYNI sayıda kesilseydi "sıra gelmedi" satırları
            // hiçbir zaman görünmez ve bütçenin dolduğu ekranda kaybolurdu.
            Identities = hukumler
                .OrderByDescending(item => item.Verdict == SeoBotVerdict.Spoofed)
                .ThenByDescending(item => item.Requests)
                .Take(SeoLogLimits.MaxVerifiedIps + SeoLogLimits.TopCount)
                .ToArray()
        };
    }

    private static SeoLogBotIdentityDto Hukum(SeoBotClaim claim, string verdict, string reverseDns) => new()
    {
        Ip = claim.Ip,
        EngineKey = claim.Engine.Key,
        Requests = claim.Requests,
        Verdict = verdict,
        ReverseDns = reverseDns
    };
}
