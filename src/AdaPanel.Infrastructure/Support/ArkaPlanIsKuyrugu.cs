using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Support;

/// <summary>
/// YANITI BEKLETMEMESİ gereken işlerin kuyruğu.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden var — ÖLÇÜLDÜ.</b> Kimliksiz uçlarda (parola sıfırlama isteği, dağıtım listesinden
/// çıkma isteği) yanıt gövdesi her durumda aynıydı, ama YAPILAN İŞ dala bağlıydı: adres panelde
/// KAYITLIYSA gerçek bir SMTP bağlantısı açılıyor, değilse hiçbir şey yapılmadan dönülüyordu.
/// Bu makinede ölçülen SMTP maliyeti: KAPALI bir porta bağlanma denemesi bile
/// <b>2066 ms</b>, yanıt vermeyen (karadelik) bir adrese <b>10 sn</b>'de hâlâ bekliyordu ve
/// MailKit'in varsayılan zaman aşımı <b>120000 ms</b>. Aynı yoldaki dosya işleri ise ortanca
/// <b>0,72 ms</b> (belirteç üretimi) ve <b>0,17 ms</b> (ayar okuması). Yani fark SMTP'nin
/// kendisidir ve saniyeler mertebesindedir: yanıt süresini ölçen biri, hiçbir parola bilmeden
/// "bu e-posta panelde kayıtlı mı" sorusunu cevaplatabiliyordu.
/// </para>
/// <para>
/// <b>Neden sabit gecikme DEĞİL:</b> "kısa dalı da bekletelim" çözümü doğru gecikmeyi bilmeyi
/// gerektirir ve yukarıdaki sayılar bunun imkânsız olduğunu gösteriyor — gecikme 2 sn seçilse
/// karadelik senaryosunda fark yine sızar, 120 sn seçilse panel kullanılamaz hâle gelir. İşi
/// yanıt yolundan TAMAMEN çıkarmak farkı kaynağında yok eder: iki dal da "kuyruğa yaz, dön"
/// yapar, yani ölçülecek bir fark KALMAZ.
/// </para>
/// <para>
/// <b>Neden istek kapsamı (scope) taşınmıyor:</b> iş, yanıt döndükten SONRA çalışır; o anda
/// isteğin DI kapsamı kapanmıştır ve içindeki <c>DbContext</c> atılmıştır. Bu yüzden işe
/// kapsamın kendisi değil, TAZE bir kapsamdan üretilmiş <see cref="IServiceProvider"/> verilir
/// (kalıp: <c>SetupService.RunRecommendedInstallAsync</c>).
/// </para>
/// </remarks>
public interface IArkaPlanIsKuyrugu
{
    /// <summary>İşi kuyruğa alır ve HEMEN döner.</summary>
    /// <param name="ad">Günlükte görünecek kısa ad (ör. <c>auth.password_reset</c>).</param>
    /// <param name="is">Taze bir DI kapsamının sağlayıcısıyla çalıştırılacak iş.</param>
    /// <returns>
    /// Kuyruğa alındıysa <c>true</c>; kuyruk DOLUYSA <c>false</c> — bu durumda iş HİÇ çalışmaz ve
    /// sebep günlüğe yazılır. Sessizce yutulmaz: "posta hiç gelmiyor" arızasının izi kalsın.
    /// </returns>
    bool Kuyrukla(string ad, Func<IServiceProvider, CancellationToken, Task> @is);
}

/// <inheritdoc cref="IArkaPlanIsKuyrugu"/>
/// <remarks>
/// <para>
/// <b>Neden SINIRLI (bounded) kanal:</b> kuyruğu besleyen uçlar kimliksizdir. Sınırsız bir kuyruk,
/// tek satırlık isteklerle belleği şişiren bir kaldıraç olurdu. Kapasite dolduğunda yeni iş
/// KABUL EDİLMEZ ve çağırana <c>false</c> döner — yani "aldım" denip atılmaz.
/// </para>
/// <para>
/// <b>Neden <see cref="BoundedChannelFullMode.Wait"/>, <c>DropWrite</c> DEĞİL (ölçüldü):</b>
/// <c>DropWrite</c> kipinde <c>TryWrite</c> kuyruk doluyken işi SESSİZCE atar ve yine
/// <c>true</c> döner — yani "kuyruğa alındı" der, iş hiç çalışmaz ve hiçbir yerde iz kalmaz. İlk
/// yazımda bu kip seçilmişti ve nöbetçi test (<c>Kuyruk_dolunca_is_ALINMAZ_ve_bildirilir</c>) tam
/// da bunu yakaladı. <c>Wait</c> kipinde <c>TryWrite</c> doluyken BLOKLAMADAN <c>false</c> döner;
/// çağıran gerçeği öğrenir ve sebep günlüğe yazılır.
/// </para>
/// <para>
/// <b>Neden tek tüketici:</b> işlerin çoğu SMTP gönderimidir; paralel açılan onlarca bağlantı
/// posta sunucusunda hız sınırına ve panelin gönderen itibarına zarar verir. Tek tüketici sırayı
/// korur. Tek bir işin tüm kuyruğu kilitlememesi için her işe <see cref="IsZamanAsimi"/> tavanı
/// konur (MailKit'in kendi zaman aşımıyla aynı mertebede).
/// </para>
/// </remarks>
public sealed class ArkaPlanIsKuyrugu(ILogger<ArkaPlanIsKuyrugu> logger) : IArkaPlanIsKuyrugu
{
    /// <summary>Kuyrukta bekleyebilecek en fazla iş.</summary>
    public const int Kapasite = 512;

    /// <summary>Tek bir işin çalışabileceği en uzun süre; aşarsa iptal edilir ve günlüğe yazılır.</summary>
    public static readonly TimeSpan IsZamanAsimi = TimeSpan.FromMinutes(3);

    private readonly Channel<(string Ad, Func<IServiceProvider, CancellationToken, Task> Is)> kanal =
        Channel.CreateBounded<(string, Func<IServiceProvider, CancellationToken, Task>)>(
            new BoundedChannelOptions(Kapasite)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });

    internal ChannelReader<(string Ad, Func<IServiceProvider, CancellationToken, Task> Is)> Okuyucu => kanal.Reader;

    public bool Kuyrukla(string ad, Func<IServiceProvider, CancellationToken, Task> @is)
    {
        ArgumentNullException.ThrowIfNull(@is);

        if (kanal.Writer.TryWrite((ad, @is)))
        {
            return true;
        }

        // DÜŞEN İŞ SESSİZ KALMAZ. Bu satır olmasaydı kuyruk dolduğunda parola sıfırlama postası
        // hiç gönderilmez ve hiçbir yerde iz kalmazdı — bu depodaki en pahalı arıza sınıfı.
        logger.LogError(
            "Arka plan isi KUYRUGA ALINAMADI (kuyruk dolu, kapasite {Kapasite}): {Is}. Is CALISMADI.",
            Kapasite,
            ad);
        return false;
    }
}

/// <summary>
/// <see cref="ArkaPlanIsKuyrugu"/>'ndaki işleri sırayla, her biri KENDİ DI kapsamında çalıştırır.
/// </summary>
/// <remarks>
/// Kayıt yapılmazsa kuyruk dolar ve hiçbir iş çalışmaz; bu yüzden kayıt
/// <c>ServiceCollectionExtensions</c>'da diğer <see cref="BackgroundService"/>'lerle aynı yerde.
/// </remarks>
public sealed class ArkaPlanIsKuyruguWorker(
    ArkaPlanIsKuyrugu kuyruk,
    IServiceScopeFactory scopeFactory,
    ILogger<ArkaPlanIsKuyruguWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var (ad, @is) in kuyruk.Okuyucu.ReadAllAsync(stoppingToken))
            {
                await CalistirAsync(ad, @is, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Panel kapanıyor: bekleyen işler bir sonraki açılışta yeniden ÜRETİLİR (kuyruk
            // bellektedir, kalıcı değildir). Kalıcı kuyruk bu iş için gereğinden ağır olurdu;
            // en kötü durumda kullanıcı sıfırlama postasını yeniden ister.
        }
    }

    private async Task CalistirAsync(string ad, Func<IServiceProvider, CancellationToken, Task> @is, CancellationToken stoppingToken)
    {
        using var zamanAsimi = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        zamanAsimi.CancelAfter(ArkaPlanIsKuyrugu.IsZamanAsimi);

        try
        {
            await using var kapsam = scopeFactory.CreateAsyncScope();
            await @is(kapsam.ServiceProvider, zamanAsimi.Token);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning("Arka plan isi panel kapanirken yarida kesildi: {Is}.", ad);
        }
        catch (OperationCanceledException)
        {
            logger.LogError(
                "Arka plan isi ZAMAN ASIMINA ugradi ({Dakika} dk) ve iptal edildi: {Is}.",
                ArkaPlanIsKuyrugu.IsZamanAsimi.TotalMinutes,
                ad);
        }
        catch (Exception hata)
        {
            // Bir işin hatası kuyruğu DÜŞÜRMEZ; sıradaki iş çalışmaya devam eder.
            logger.LogError(hata, "Arka plan isi basarisiz oldu: {Is}.", ad);
        }
    }
}
