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

/// <summary>
/// Tek calismanin sure siniri.
/// <para>
/// CANLI OLAY: 0.2.66'da her goreve sabit 4 saatlik bir sinir koymustum. Kullanicinin uzun suren
/// gorevi bu sinira takildi ve "Sure sinirina takildi, sonlandirildi" ile ORTASINDA olduruldu --
/// kullanici da hakli olarak "cronlarda sure olmasin" dedi.
/// </para>
/// <para>
/// Sinir artik gorev basina secilir ve varsayilani SINIRSIZ. Gerekce: sinir koymamanin riski
/// takilan bir calismanin sonraki turlari engellemesidir, ama panel artik takilan calismayi GORUP
/// tek dugmeyle durdurabiliyor -- yani o risk gorunur ve geri alinabilir. Yarida kesilen bir is
/// ise geri alinamaz.
/// </para>
/// </summary>
public sealed class CronTimeLimitTests
{
    private static string Script(int timeLimitHours) => SchedulerService.BuildCreateScript(
        new AdaPanel.Core.Dtos.Scheduler.CreateScheduledTaskRequest
        {
            Name = "gorev",
            TaskType = "url",
            Url = "https://ornek.test/cron",
            ScheduleType = "hourly",
            Interval = 1,
            TimeLimitHours = timeLimitHours
        },
        plan: null);

    // Windows'ta sinirsiz PT0S ile ifade edilir; TimeSpan::Zero tam olarak onu uretir (olculdu).
    [Fact]
    public void ZeroHours_MeansNoLimitAtAll()
    {
        Assert.Contains("-ExecutionTimeLimit ([TimeSpan]::Zero)", Script(0), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(24)]
    public void AChosenLimit_IsWrittenAsGiven(int saat)
    {
        Assert.Contains($"-ExecutionTimeLimit (New-TimeSpan -Hours {saat})", Script(saat), StringComparison.Ordinal);
    }

    // Gecersiz deger sessizce bir SINIR uretmemeli: negatif bir sayi yuzunden gorevin aninda
    // oldurulmesi, kullanicinin hic beklemedigi bir davranis olurdu.
    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    public void NegativeValues_FallBackToNoLimitInsteadOfAnAbsurdOne(int saat)
    {
        Assert.Contains("-ExecutionTimeLimit ([TimeSpan]::Zero)", Script(saat), StringComparison.Ordinal);
    }

    // Geri okuma: 24 saat P1D olarak saklanir. "PT<n>H" disindaki bicimleri kacirmamak icin ayni
    // ISO 8601 cozumleyicisi kullanilir.
    [Theory]
    [InlineData("PT4H", 4)]
    [InlineData("P1D", 24)]
    [InlineData("PT12H", 12)]
    public void StoredLimits_AreReadBackCorrectly(string iso, int beklenenSaat)
    {
        var dakika = SchedulerService.ParseRepetitionMinutes(iso);

        Assert.NotNull(dakika);
        Assert.Equal(beklenenSaat, dakika!.Value / 60);
    }

    // PT0S = sinirsiz; cozumleyici bunu "sifir" olarak degil, DEGER YOK olarak dondurmeli ki
    // panel 0 (sinirsiz) gostersin.
    [Fact]
    public void UnlimitedIsReadBackAsUnlimited()
    {
        Assert.Null(SchedulerService.ParseRepetitionMinutes("PT0S"));
    }
}
