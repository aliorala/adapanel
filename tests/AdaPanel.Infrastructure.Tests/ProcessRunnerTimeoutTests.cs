using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Zaman aşımına uğrayan dış araç çağrıları.
/// <para>
/// Zaman aşımı yolu daha önce süreci öldürüp okuma görevlerini <b>beklemeden</b> dönüyordu. Görevler
/// süreç boruları üzerinde çalıştığı için, dönüşle birlikte borular kapanıyor ve görevler hiç
/// gözlemlenmeden düşüyordu — zaman aşımına uğrayan her çağrı (win-acme, git clone, DB CLI) bir
/// sahipsiz görev ve tutulan boru kaynağı bırakıyordu. Ayrıca aracın zaman aşımından ÖNCE yazdığı
/// çıktı da atılıyordu; oysa asıl sebep çoğu zaman orada yazılıdır.
/// </para>
/// </summary>
public sealed class ProcessRunnerTimeoutTests
{
    [Fact]
    public async Task Zaman_asiminda_minus_bir_ve_acik_bir_mesaj_doner()
    {
        var sonuc = await ProcessRunner.RunAsync(
            "cmd.exe",
            "/c ping -n 30 127.0.0.1",
            timeout: TimeSpan.FromSeconds(2));

        Assert.Equal(-1, sonuc.ExitCode);
        Assert.Contains("timed out", sonuc.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Zaman_asimindan_ONCE_yazilan_cikti_KAYBOLMAZ()
    {
        // Araç bir şey yazıp sonra takılıyor. Eski davranışta bu çıktı atılıyordu ve kullanıcı
        // yalnız "zaman aşımı" görüyordu — takılmanın sebebini gösteren satır elde kalmıyordu.
        var sonuc = await ProcessRunner.RunAsync(
            "cmd.exe",
            "/c echo ADAPANEL-ISARET & ping -n 30 127.0.0.1",
            timeout: TimeSpan.FromSeconds(3));

        Assert.Equal(-1, sonuc.ExitCode);
        Assert.Contains("ADAPANEL-ISARET", sonuc.StandardOutput, StringComparison.OrdinalIgnoreCase);
        // Zaman aşımı mesajı da DURMALI: kısmi çıktı, işin bittiği anlamına gelmez.
        Assert.Contains("timed out", sonuc.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Zaman_asimi_isteği_ASKIYA_ALMAZ()
    {
        // Temizlik adımının kendisi beklemeye dönüşmemeli: 2 saniyelik sınır, temizlikle birlikte
        // makul bir sürede tamamlanmalı. (Tavan cömert tutuldu ki yavaş makinede sallanmasın.)
        var basla = DateTime.UtcNow;

        await ProcessRunner.RunAsync("cmd.exe", "/c ping -n 30 127.0.0.1", timeout: TimeSpan.FromSeconds(2));

        Assert.True(
            DateTime.UtcNow - basla < TimeSpan.FromSeconds(20),
            "Zaman aşımı yolu makul sürede dönmeliydi.");
    }
}
