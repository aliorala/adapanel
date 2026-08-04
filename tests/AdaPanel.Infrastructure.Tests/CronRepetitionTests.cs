using AdaPanel.Infrastructure.Scheduler;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Tekrar araliginin GERI OKUNMASI.
/// <para>
/// KANITLANMIS HATA: panel yalnizca PT&lt;n&gt;M ve PT&lt;n&gt;H bicimlerini taniyordu. Oysa Windows
/// ayni sureyi kendi normallestirdigi bicimde saklar -- bu makinede olculdu:
/// 60 dakika -&gt; PT1H, 90 dakika -&gt; PT1H30M, 24 saat -&gt; P1D.
/// </para>
/// <para>
/// Taninmayan bicim saatlik-1 varsayilanina dusuyordu. Sonuc SESSIZ ve AGIR: 24 saatte bir calisan
/// bir gorevi duzenleyip kaydeden kullanici, hicbir uyari almadan onu SAATTE BIR calisir hale
/// getiriyordu -- gorev gunde 24 kat daha sik calismaya basliyordu.
/// </para>
/// </summary>
public sealed class CronRepetitionTests
{
    // Windows'un GERCEKTEN urettigi bicimler (bu makinede New-ScheduledTaskTrigger ile olculdu).
    [Theory]
    [InlineData("PT1M", 1)]
    [InlineData("PT5M", 5)]
    [InlineData("PT30M", 30)]
    [InlineData("PT1H", 60)]
    [InlineData("PT1H30M", 90)]
    [InlineData("PT2H", 120)]
    [InlineData("P1D", 1440)]
    [InlineData("PT6H", 360)]
    public void EveryFormatWindowsProduces_IsUnderstood(string iso, int beklenenDakika)
    {
        Assert.Equal(beklenenDakika, SchedulerService.ParseRepetitionMinutes(iso));
    }

    // Cozulemeyen bir deger icin SAYI UYDURULMAZ: uydurulan sayi, kullanicinin ayarini
    // duzenlemede kalici olarak bozardi.
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("P")]
    [InlineData("saatlik")]
    [InlineData("PT0M")]
    public void UnreadableValues_ProduceNoGuess(string? iso)
    {
        Assert.Null(SchedulerService.ParseRepetitionMinutes(iso));
    }

    /// <summary>
    /// GIDIS-DONUS: kullanicinin sectigi zamanlama, kaydedip geri okuduktan sonra AYNI kalmali.
    /// Bozulan sey tam olarak buydu.
    /// </summary>
    [Theory]
    [InlineData("hourly", 24, "P1D")]
    [InlineData("hourly", 6, "PT6H")]
    [InlineData("hourly", 1, "PT1H")]
    [InlineData("minute", 5, "PT5M")]
    [InlineData("minute", 90, "PT1H30M")]
    [InlineData("minute", 30, "PT30M")]
    public void Schedule_SurvivesTheRoundTripThroughWindows(string tip, int aralik, string windowsBicimi)
    {
        var dakika = SchedulerService.ParseRepetitionMinutes(windowsBicimi);
        Assert.NotNull(dakika);

        var (cozulenTip, cozulenAralik) = SchedulerService.DescribeRepetition(dakika!.Value);

        // Ayni SURE korunmali. "dakikalik 60" ile "saatlik 1" ayni zamanlamadir; onemli olan
        // gorevin ayni sıklıkta calismasi, etiketin harfiyet degil.
        var beklenenDakika = tip == "hourly" ? aralik * 60 : aralik;
        var cozulenDakika = cozulenTip == "hourly" ? cozulenAralik * 60 : cozulenAralik;
        Assert.Equal(beklenenDakika, cozulenDakika);
    }

    // Tam saat katlari SAAT olarak gosterilir: kullanici "1440 dakika" degil "24 saat" okumak ister.
    [Theory]
    [InlineData(1440, "hourly", 24)]
    [InlineData(360, "hourly", 6)]
    [InlineData(60, "hourly", 1)]
    [InlineData(90, "minute", 90)]
    [InlineData(5, "minute", 5)]
    public void WholeHours_ArePresentedAsHours(int dakika, string beklenenTip, int beklenenAralik)
    {
        var (tip, aralik) = SchedulerService.DescribeRepetition(dakika);

        Assert.Equal(beklenenTip, tip);
        Assert.Equal(beklenenAralik, aralik);
    }
}
