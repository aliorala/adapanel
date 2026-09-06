using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// ARKA PLAN İŞ KUYRUĞUNUN DAVRANIŞI.
/// </summary>
/// <remarks>
/// <para>
/// Kuyruk, kimlik uçlarındaki zamanlama sızıntısının kapatılma biçimidir: iş yanıt yolundan
/// çıkarılır. O yüzden burada ölçülen üç şey var ve üçü de KRONOMETRESİZ ölçülüyor (duvar saatine
/// bakan test CI'da kararsızdır):
/// </para>
/// <list type="number">
/// <item><description><c>Kuyrukla</c> çağıranı BEKLETMEZ — iş henüz bitmemişken geri döner.</description></item>
/// <item><description>Kuyruk dolduğunda iş SESSİZCE yutulmaz; çağırana <c>false</c> döner.</description></item>
/// <item><description>Bir işin hatası kuyruğu DÜŞÜRMEZ; sıradaki iş çalışır.</description></item>
/// </list>
/// </remarks>
public sealed class ArkaPlanIsKuyruguTests
{
    /// <summary>
    /// Çağıran BEKLETİLMEZ. Kapı (gate) kullanılıyor, süre değil: iş kapıda kilitliyken
    /// <c>Kuyrukla</c> dönmüş olmalı. Bu, "gönderim yanıtı bekletiyor mu" sorusunun deterministik
    /// hâlidir.
    /// </summary>
    [Fact]
    public async Task Kuyrukla_cagirani_BEKLETMEZ()
    {
        using var kapi = new ManualResetEventSlim(false);
        using var basladi = new ManualResetEventSlim(false);
        using var bitti = new ManualResetEventSlim(false);
        await using var duzenek = IsciDuzenegi.Baslat();

        // İş, kapı açıldığında bu bayrağı okur. Kuyruk çağıranı BEKLETMİYORSA bayrak o an TRUE
        // olmuş olmalı; iş yanıt yolunda ÇALIŞTIRILSAYDI (kuyruk kalkarsa) çağıran kapıda kilitlenir
        // ve bayrağı hiç yazamazdı — yani bu iddia, düzeltmenin kaldırılmasına duyarlıdır.
        var kuyruklaDondu = false;
        var isGordu = false;

        var alindi = duzenek.Kuyruk.Kuyrukla("test.bekletme", (_, _) =>
        {
            basladi.Set();
            kapi.Wait(TimeSpan.FromSeconds(30));
            isGordu = Volatile.Read(ref kuyruklaDondu);
            bitti.Set();
            return Task.CompletedTask;
        });

        Volatile.Write(ref kuyruklaDondu, true);

        Assert.True(alindi);
        Assert.True(basladi.Wait(TimeSpan.FromSeconds(30)), "Isci isi hic almadi.");
        kapi.Set();
        Assert.True(bitti.Wait(TimeSpan.FromSeconds(30)), "Is bitmedi.");
        Assert.True(isGordu, "Kuyrukla, is bitene kadar cagirani BEKLETTI.");
    }

    /// <summary>İş GERÇEKTEN çalışır ve KENDİ DI kapsamını alır (isteğin kapsamı değil).</summary>
    [Fact]
    public async Task Isci_isi_KENDI_kapsaminda_calistirir()
    {
        using var bitti = new ManualResetEventSlim(false);
        await using var duzenek = IsciDuzenegi.Baslat();
        IsaretliServis? gorulen = null;

        duzenek.Kuyruk.Kuyrukla("test.kapsam", (saglayici, _) =>
        {
            gorulen = saglayici.GetRequiredService<IsaretliServis>();
            bitti.Set();
            return Task.CompletedTask;
        });

        Assert.True(bitti.Wait(TimeSpan.FromSeconds(30)), "Is calismadi.");
        Assert.NotNull(gorulen);
    }

    /// <summary>
    /// KUYRUK DOLUYSA İŞ ALINMAZ ve bu ÇAĞIRANA SÖYLENİR. "Aldım" deyip atmak, tam da kapatmaya
    /// çalıştığımız sessiz başarısızlık olurdu.
    /// </summary>
    [Fact]
    public void Kuyruk_dolunca_is_ALINMAZ_ve_bildirilir()
    {
        var kuyruk = new ArkaPlanIsKuyrugu(NullLogger<ArkaPlanIsKuyrugu>.Instance);

        // İşçi YOK: kimse tüketmiyor, kapasite dolana kadar yazılır.
        for (var i = 0; i < ArkaPlanIsKuyrugu.Kapasite; i++)
        {
            Assert.True(kuyruk.Kuyrukla("test.dolum", (_, _) => Task.CompletedTask));
        }

        Assert.False(kuyruk.Kuyrukla("test.tasma", (_, _) => Task.CompletedTask));
    }

    /// <summary>Bir işin hatası kuyruğu düşürmez; sıradaki iş çalışır.</summary>
    [Fact]
    public async Task Bir_isin_hatasi_sonraki_isi_ENGELLEMEZ()
    {
        using var ikinciBitti = new ManualResetEventSlim(false);
        await using var duzenek = IsciDuzenegi.Baslat();

        duzenek.Kuyruk.Kuyrukla("test.patlayan", (_, _) => throw new InvalidOperationException("bilerek"));
        duzenek.Kuyruk.Kuyrukla("test.sonraki", (_, _) =>
        {
            ikinciBitti.Set();
            return Task.CompletedTask;
        });

        Assert.True(ikinciBitti.Wait(TimeSpan.FromSeconds(30)), "Patlayan is kuyrugu dusurdu.");
    }

    /// <summary>Kuyruk + işçi + gerçek bir DI kabı.</summary>
    private sealed class IsciDuzenegi : IAsyncDisposable
    {
        private readonly ServiceProvider saglayici;
        private readonly ArkaPlanIsKuyruguWorker isci;

        private IsciDuzenegi(ServiceProvider saglayici, ArkaPlanIsKuyrugu kuyruk, ArkaPlanIsKuyruguWorker isci)
        {
            this.saglayici = saglayici;
            this.isci = isci;
            Kuyruk = kuyruk;
        }

        public ArkaPlanIsKuyrugu Kuyruk { get; }

        public static IsciDuzenegi Baslat()
        {
            var koleksiyon = new ServiceCollection();
            koleksiyon.AddScoped<IsaretliServis>();
            var saglayici = koleksiyon.BuildServiceProvider();

            var kuyruk = new ArkaPlanIsKuyrugu(NullLogger<ArkaPlanIsKuyrugu>.Instance);
            var isci = new ArkaPlanIsKuyruguWorker(
                kuyruk,
                saglayici.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<ArkaPlanIsKuyruguWorker>.Instance);
            isci.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            return new IsciDuzenegi(saglayici, kuyruk, isci);
        }

        public async ValueTask DisposeAsync()
        {
            await isci.StopAsync(CancellationToken.None);
            isci.Dispose();
            await saglayici.DisposeAsync();
        }
    }

    /// <summary>Kapsamdan çözülebildiği görülsün diye var olan boş servis.</summary>
    private sealed class IsaretliServis;
}
