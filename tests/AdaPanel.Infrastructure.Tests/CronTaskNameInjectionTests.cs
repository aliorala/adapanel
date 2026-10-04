using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Scheduler;
using AdaPanel.Infrastructure.Scheduler;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// GOREV ADINDAN SYSTEM OLARAK KOD CALISTIRMA -- panelin en agir aciklarindan biriydi.
/// <para>
/// Zincir: <c>POST /api/v1/cron</c> ucu <c>Customer</c> rolune ACIK. Gorev adi hicbir dogrulamadan
/// gecmiyordu ve <c>EscapePowerShell</c> yalnizca cift tirnagi kaciriyordu. Ciktisi CIFT TIRNAKLI
/// PowerShell dizelerinin icine giriyordu; PowerShell'de cift tirnakli dize <c>$(...)</c>
/// alt-ifadesini CALISTIRIR. Panel servisi de LocalSystem olarak kosuyor. Yani bir hosting
/// musterisinin gonderdigi
/// <c>{"name": "yedek$(net localgroup administrators hax /add)"}</c>
/// sunucuda SYSTEM olarak calisiyordu.
/// </para>
/// <para>
/// Kok neden duzeltildi: ad artik TEK TIRNAKLI dizeye giriyor (tek tirnakli dizede genisleme ve
/// alt-ifade YOKTUR) ve ayrica dar bir beyaz listeden geciyor. Iki katman BAGIMSIZ: biri ileride
/// bozulsa digeri tutar.
/// </para>
/// </summary>
public sealed class CronTaskNameInjectionTests
{
    // Denetimde yazilan gercek yuk.
    private const string Yuk = "yedek$(net localgroup administrators hax /add)";

    private static CreateScheduledTaskRequest Istek(string ad) => new()
    {
        Name = ad,
        TaskType = "url",
        Url = "https://ornek.test/cron",
        ScheduleType = "hourly",
        Interval = 1
    };

    /// <summary>
    /// Kayit betigi: ad tek kayit/guncelleme cagrisinda tek tirnakli olmali.
    /// Onceki kaydi ayri silmek, sonraki hata/iptalde calisan gorevi kaybettirir.
    /// </summary>
    [Fact]
    public void KayitBetiginde_gorev_adi_alt_ifade_calistiramaz()
    {
        var betik = SchedulerService.BuildCreateScript(Istek(Yuk), plan: null);

        // Ad her gectigi yerde TEK tirnak icinde.
        Assert.Equal(1, SayHepsi(betik, $"-TaskName '{Yuk}'"));
        Assert.DoesNotContain("Unregister-ScheduledTask", betik, StringComparison.Ordinal);
        Assert.Contains("Register-ScheduledTask", betik, StringComparison.Ordinal);

        // Ve HICBIR yerde cift tirnakli baglamda degil (eski davranis buydu).
        Assert.DoesNotContain($"-TaskName \"{Yuk}\"", betik, StringComparison.Ordinal);
        Assert.DoesNotContain("`\"", betik, StringComparison.Ordinal);
    }

    [Fact]
    public void CalistirmaBetiginde_gorev_adi_alt_ifade_calistiramaz()
    {
        var betik = SchedulerService.BuildRunScript(Yuk);

        Assert.Equal(2, SayHepsi(betik, $"-TaskName '{Yuk}'"));
        Assert.DoesNotContain($"-TaskName \"{Yuk}\"", betik, StringComparison.Ordinal);
    }

    [Fact]
    public void DurdurmaBetiginde_gorev_adi_alt_ifade_calistiramaz()
    {
        var betik = SchedulerService.BuildStopScript(Yuk, @"C:\ProgramData\AdaPanel\logs\tasks\yedek.log");

        Assert.Equal(2, SayHepsi(betik, $"-TaskName '{Yuk}'"));
        Assert.DoesNotContain($"-TaskName \"{Yuk}\"", betik, StringComparison.Ordinal);
    }

    /// <summary>
    /// Tek tirnakli dizeden CIKMA denemesi: tek tirnak IKILENEREK kacilir, aksi halde ad
    /// dizeden cikip komut olurdu.
    /// </summary>
    [Theory]
    [InlineData("yedek'; calc.exe; '")]
    [InlineData("a'b")]
    public void Tek_tirnak_ikilenerek_kacilir(string ad)
    {
        var betik = SchedulerService.BuildRunScript(ad);

        Assert.Contains($"-TaskName '{ad.Replace("'", "''")}'", betik, StringComparison.Ordinal);
    }

    /// <summary>
    /// Kacisin YANINDA beyaz liste de var: yuk zaten DTO'da ve ValidateRequest'te reddedilir.
    /// Tek katmana guvenmiyoruz.
    /// </summary>
    [Theory]
    [InlineData("yedek$(whoami)")]
    [InlineData("yedek`whoami`")]
    [InlineData("yedek'; calc; '")]
    [InlineData("yedek\"x")]
    [InlineData("yedek;calc")]
    [InlineData("yedek|calc")]
    [InlineData("yedek&calc")]
    [InlineData("yedek\ncalc")]
    [InlineData("")]
    public void Beyaz_liste_kabuk_karakterlerini_reddeder(string ad)
    {
        Assert.False(TaskNameRules.IsCreatable(ad));
        Assert.False(TaskNameRules.IsUsable(ad));
    }

    [Theory]
    [InlineData("gece yedek")]
    [InlineData("yedek.1")]
    [InlineData("yedek_2")]
    [InlineData("yedek-3")]
    public void Normal_adlar_serbest_kalir(string ad)
    {
        Assert.True(TaskNameRules.IsCreatable(ad));
        Assert.True(TaskNameRules.IsUsable(ad));
    }

    /// <summary>
    /// Panelin musteri gorevlerine ekledigi "u&lt;guid&gt;--" oneki desene UYMALI, yoksa panel kendi
    /// urettigi adi reddederdi (ozellik sessizce olurdu).
    /// </summary>
    [Fact]
    public void Panelin_kendi_abonelik_oneki_beyaz_listeye_uyar()
    {
        var onek = $"u{Guid.NewGuid():N}--";

        Assert.True(TaskNameRules.IsCreatable(onek + "gece yedek"));
        Assert.True(TaskNameRules.IsUsable(onek + "gece yedek"));

        // Onek AYRILARAK olculur: aksi halde kullanicinin 60 karakterlik hakki 25'e duserdi.
        Assert.True(TaskNameRules.IsCreatable(onek + new string('a', 60)));
        Assert.False(TaskNameRules.IsCreatable(onek + new string('a', 61)));
    }

    /// <summary>
    /// Var olan gorevler uzerindeki islemlerde kural bilerek GENIS: bu dogrulama yokken kurulmus
    /// "Gunluk Yedek" gibi Turkce adli gorevler panelden silinebilmeye devam etmeli. Genisleme
    /// yalnizca Unicode harf/rakam yonunde; kabuk karakterleri orada da reddedilir (ustteki test).
    /// </summary>
    [Theory]
    [InlineData("Günlük Yedek")]
    [InlineData("şğüöçİ")]
    public void Eski_Turkce_adli_gorevler_yonetilebilir_kalir(string ad)
    {
        Assert.True(TaskNameRules.IsUsable(ad));
        Assert.False(TaskNameRules.IsCreatable(ad)); // yenisi bu adla KURULAMAZ
    }

    /// <summary>
    /// Islem uclarinin HEPSI beyaz listeden gecmeli. Biri unutulursa kacisin tek savunma kalmasi
    /// demektir -- bu dosyada zaten bir kez oyle olmustu.
    /// </summary>
    [Fact]
    public async Task Butun_islem_uclari_gorev_adini_dogrular()
    {
        var servis = new SchedulerService(
            new HicCagrilmayanSiteServisi(),
            Microsoft.Extensions.Options.Options.Create(new AdaPanel.Infrastructure.Options.PanelRuntimeOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SchedulerService>.Instance);

        await Reddedildi(() => servis.RunTaskAsync(Yuk));
        await Reddedildi(() => servis.DeleteTaskAsync(Yuk));
        await Reddedildi(() => servis.SetTaskEnabledAsync(Yuk, true));
        await Reddedildi(() => servis.GetTaskStatusAsync(Yuk));
        await Reddedildi(() => servis.GetTaskOutputAsync(Yuk));
        await Reddedildi(() => servis.StopTaskAsync(Yuk));
    }

    /// <summary>
    /// SINIF KAPATMA: tehlikeli kacis fonksiyonu kaynaktan TAMAMEN kalkmali. Geri gelirse, bu
    /// testlerin hepsi gecerken yeni bir cagri yerinde aciklik yeniden dogar.
    /// </summary>
    [Fact]
    public void Tehlikeli_kacis_fonksiyonu_kaynakta_kalmadi()
    {
        var scheduler = OkuKaynak("src", "AdaPanel.Infrastructure", "Scheduler", "SchedulerService.cs");
        var runner = OkuKaynak("src", "AdaPanel.Infrastructure", "Support", "LimitedIdentityRunner.cs");

        // "(" ile arayoruz: tanim ve cagri kalmamali. Fonksiyonun ADI, neden kaldirildigini
        // anlatan yorumlarda gecmeye devam ediyor -- o kasitli, cunku bir daha eklenmemeli.
        Assert.DoesNotContain("EscapePowerShell(", scheduler, StringComparison.Ordinal);
        Assert.DoesNotContain("Cift(", runner, StringComparison.Ordinal);

        // icacls cagrilari da tek tirnakli olmali: yol kismen kiraciddan turer.
        Assert.DoesNotContain("icacls.exe \"\"", scheduler, StringComparison.Ordinal);
        Assert.DoesNotContain("icacls.exe \"\"", runner, StringComparison.Ordinal);
    }

    // --- duzenek ---

    private static async Task Reddedildi(Func<Task> islem)
    {
        var hata = await Assert.ThrowsAsync<AppException>(islem);
        Assert.Equal("cron.invalid_name", hata.MessageKey);
        Assert.Equal(400, hata.HttpStatusCode);
    }

    private static int SayHepsi(string metin, string parca)
    {
        var adet = 0;
        var index = 0;
        while ((index = metin.IndexOf(parca, index, StringComparison.Ordinal)) >= 0)
        {
            adet++;
            index += parca.Length;
        }

        return adet;
    }

    private static string OkuKaynak(params string[] parcalar)
    {
        var kok = AppContext.BaseDirectory;
        while (kok is not null && !File.Exists(Path.Combine(kok, "AdaPanel.sln")))
        {
            kok = Path.GetDirectoryName(kok);
        }

        Assert.NotNull(kok);
        return File.ReadAllText(Path.Combine([kok!, .. parcalar]));
    }

    /// <summary>Ad dogrulamasi IIS'e HIC gitmeden calismali; buraya dusen cagri testi bozar.</summary>
    private sealed class HicCagrilmayanSiteServisi : AdaPanel.Core.Interfaces.IIisSiteService
    {
        public Task<IReadOnlyCollection<AdaPanel.Core.Dtos.Sites.SiteDto>> GetSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AdaPanel.Core.Dtos.Sites.SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AdaPanel.Core.Dtos.Sites.SiteDto> CreateSiteAsync(AdaPanel.Core.Dtos.Sites.CreateSiteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AdaPanel.Core.Dtos.Sites.SiteDto> UpdatePhysicalPathAsync(string siteName, string physicalPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AdaPanel.Core.Dtos.Sites.SiteDto> StartSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AdaPanel.Core.Dtos.Sites.SiteDto> StopSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AdaPanel.Core.Dtos.Sites.SiteDto> AddBindingAsync(string siteName, AdaPanel.Core.Dtos.Sites.SiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AdaPanel.Core.Dtos.Sites.SiteDto> RemoveBindingAsync(string siteName, AdaPanel.Core.Dtos.Sites.RemoveSiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<AdaPanel.Core.Dtos.Sites.SiteDto>> ImportExistingSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
