using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Scheduler;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Scheduler;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// ZAMANLANMIŞ GÖREV = SYSTEM'DE KOD ÇALIŞTIRMAK. KİM SYSTEM ALIR, ÖLÇÜLÜR.
/// </summary>
/// <remarks>
/// <para>
/// <b>ÖLÇÜLEN AÇIK (dördüncü yol).</b> Terminal sunucu sahibine kısıtlandıktan, geri yükleme /
/// mağaza kurulumu / site fiziksel yolu kapatıldıktan sonra bile <c>CronController</c>'ın yönetici
/// dalı görev tipini zorlamıyordu: <c>taskType="command"</c> ile gelen komut doğrudan
/// <c>New-ScheduledTaskAction</c>'a gidiyor ve görev <c>-UserId 'SYSTEM' -RunLevel Highest</c> ile
/// kaydediliyordu. Yani sahip OLMAYAN bir panel yöneticisi tek istekle SYSTEM'de kod
/// çalıştırabiliyordu ve o yol açıkken diğer üç kapı kâğıt üstünde kalıyordu.
/// </para>
/// <para>
/// <b>Seçilen yol (A): KİMLİĞİ DÜŞÜR.</b> Özellik kimseden alınmadı; sahip olmayan yöneticinin
/// kod çalıştıran görevleri SYSTEM yerine panelde ZATEN VAR OLAN sınırlı kimlikle
/// (<c>NT AUTHORITY\LOCAL SERVICE</c>, <c>ScriptTaskPlanner.LimitedUserId</c>) kaydedilir.
/// </para>
/// <para>
/// <b>Bu dosya DAVRANIŞ ölçer, kaynak taramaz:</b> gerçek <see cref="CronController"/> çalıştırılır
/// ve zamanlayıcıya GERÇEKTEN ne gittiği okunur; kimlik kararı ise Windows'a yazılacak PowerShell
/// betiğinden (<see cref="SchedulerService.BuildCreateScript"/>) ölçülür.
/// </para>
/// </remarks>
public sealed class CronTaskIdentityTests
{
    private static readonly Guid Sahip = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid IkinciYonetici = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Kiraci = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private const string SinirliKimlik = @"NT AUTHORITY\LOCAL SERVICE";

    // =====================================================================================
    // 1) KORUMA — sahip olmayan yönetici SYSTEM ALAMAZ
    // =====================================================================================

    [Theory]
    [InlineData("command")]
    [InlineData("php")]
    [InlineData("script")]
    public async Task Sahip_OLMAYAN_yonetici_kod_calistiran_gorevi_SINIRLI_kimlikle_kurar(string tip)
    {
        var duzenek = new Duzenek();
        var denetleyici = duzenek.Denetleyici(IkinciYonetici, "Administrator");

        await denetleyici.CreateTaskAsync(Istek(tip), CancellationToken.None);

        Assert.Equal(ScheduledTaskPrivilege.Limited, duzenek.Zamanlayici.SonYetki);
    }

    /// <summary>
    /// Taklit (impersonate) oturumu tam yetki ALAMAZ — terminal ucundaki kuralın aynısı.
    /// </summary>
    [Fact]
    public async Task Taklit_edilen_oturum_SAHIBIN_kimligiyle_bile_tam_yetki_ALAMAZ()
    {
        var duzenek = new Duzenek();
        var denetleyici = duzenek.Denetleyici(Sahip, "Administrator", taklitEden: IkinciYonetici);

        await denetleyici.CreateTaskAsync(Istek("command"), CancellationToken.None);

        Assert.Equal(ScheduledTaskPrivilege.Limited, duzenek.Zamanlayici.SonYetki);
    }

    /// <summary>
    /// Yetkinin SONUCU: Windows'a yazılan betikte principal SYSTEM değil, sınırlı hesaptır.
    /// Kararın niyette kalmadığını, kaydedilen görevde göründüğünü ölçer.
    /// </summary>
    [Fact]
    public void Sinirli_yetkide_KAYDEDILEN_gorev_SYSTEM_degildir()
    {
        var istek = Istek("command");
        var plan = SchedulerService.PlanForLimitedIdentity(istek, ScheduledTaskPrivilege.Limited);

        Assert.NotNull(plan);
        Assert.Equal(SinirliKimlik, plan!.PrincipalUserId);

        var betik = SchedulerService.BuildCreateScript(istek, plan);

        Assert.Contains($"-UserId '{SinirliKimlik}'", betik, StringComparison.Ordinal);
        Assert.Contains("-RunLevel Limited", betik, StringComparison.Ordinal);
        Assert.DoesNotContain("-UserId 'SYSTEM'", betik, StringComparison.Ordinal);
        Assert.DoesNotContain("-RunLevel Highest", betik, StringComparison.Ordinal);
    }

    /// <summary>
    /// Komut ve argümanlar AYNEN korunur: kimlik düşer, görev değişmez. (Aksi hâlde "koruma"
    /// kullanıcının kurduğu görevi sessizce başka bir göreve çevirirdi.)
    /// </summary>
    [Fact]
    public void Kimlik_duserken_gorevin_KENDISI_degismez()
    {
        var istek = Istek("command");
        var plan = SchedulerService.PlanForLimitedIdentity(istek, ScheduledTaskPrivilege.Limited)!;

        Assert.Equal(@"C:\araclar\bakim.exe", plan.Execute);
        Assert.Equal("--gece", plan.Arguments);
    }

    // =====================================================================================
    // 2) FAZLA KAPATMA NÖBETÇİLERİ
    //
    // "Hepsini reddet" / "hepsini düşür" çözümü yukarıdaki testlerin hepsini yeşil gösterir ve
    // paneli işlevsiz bırakır. Aşağıdakiler o çözümü KIRMIZI yakar.
    // =====================================================================================

    /// <summary>NÖBETÇİ: sunucu sahibi HÂLÂ istediği görevi SYSTEM olarak kurabilir.</summary>
    [Theory]
    [InlineData("command")]
    [InlineData("php")]
    [InlineData("script")]
    public async Task Sunucu_sahibi_HALA_tam_yetkili_gorev_kurabilir(string tip)
    {
        var duzenek = new Duzenek();
        var denetleyici = duzenek.Denetleyici(Sahip, "Administrator");

        await denetleyici.CreateTaskAsync(Istek(tip), CancellationToken.None);

        Assert.Equal(ScheduledTaskPrivilege.Full, duzenek.Zamanlayici.SonYetki);
        Assert.Equal(tip, duzenek.Zamanlayici.SonIstek!.TaskType);
    }

    /// <summary>
    /// NÖBETÇİ: tam yetkide kaydedilen görev bugünkü gibi SYSTEM'dir — düşürme HERKESE
    /// uygulanmıyor.
    /// </summary>
    [Fact]
    public void Tam_yetkide_gorev_SYSTEM_olarak_kaydedilir()
    {
        var istek = Istek("command");

        Assert.Null(SchedulerService.PlanForLimitedIdentity(istek, ScheduledTaskPrivilege.Full));

        var betik = SchedulerService.BuildCreateScript(istek, plan: null);

        Assert.Contains("-UserId 'SYSTEM'", betik, StringComparison.Ordinal);
        Assert.Contains("-RunLevel Highest", betik, StringComparison.Ordinal);
    }

    /// <summary>
    /// NÖBETÇİ: sahip olmayan yöneticinin MEŞRU görevleri kurulmaya devam eder — istek
    /// REDDEDİLMEZ, alanları da temizlenmez. Reddeden bir çözüm (B yolu) burada kırmızı yanar.
    /// </summary>
    [Fact]
    public async Task Sahip_OLMAYAN_yoneticinin_gorevi_REDDEDILMEZ()
    {
        var duzenek = new Duzenek();
        var denetleyici = duzenek.Denetleyici(IkinciYonetici, "Administrator");

        var yanit = await denetleyici.CreateTaskAsync(Istek("command"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(yanit.Result);
        Assert.Equal("gece-bakim", duzenek.Zamanlayici.SonIstek!.Name);
        Assert.Equal(@"C:\araclar\bakim.exe", duzenek.Zamanlayici.SonIstek.Command);
        Assert.Equal("--gece", duzenek.Zamanlayici.SonIstek.Arguments);
    }

    /// <summary>
    /// NÖBETÇİ: KİRACININ görevi kırılmaz. Müşteri/bayi zaten yalnız <c>url</c> kurabiliyor ve
    /// <c>url</c> düşürme kapsamının DIŞINDA (programı ve argümanları panel kurar — panelin
    /// denetlenen yürütücüsü; keyfi kod değil) — yani kiracının bugünkü görevlerinin hiçbiri kimlik
    /// değiştirmez.
    /// </summary>
    [Fact]
    public async Task Kiracinin_url_gorevi_AYNEN_calisir()
    {
        var duzenek = new Duzenek();
        var denetleyici = duzenek.Denetleyici(Kiraci, "Customer");

        var yanit = await denetleyici.CreateTaskAsync(
            new CreateScheduledTaskRequest
            {
                Name = "gece-tetik",
                TaskType = "url",
                Url = "https://ornek.test/cron",
                ScheduleType = "daily",
                TimeOfDay = "03:00"
            },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(yanit.Result);
        Assert.Equal("url", duzenek.Zamanlayici.SonIstek!.TaskType);

        // Ve kimlik kararı url'i HİÇ kapsamıyor: yetki ne olursa olsun plan (yani düşürme) yok.
        Assert.Null(SchedulerService.PlanForLimitedIdentity(duzenek.Zamanlayici.SonIstek, ScheduledTaskPrivilege.Limited));
        Assert.False(SchedulerService.RequiresLimitedIdentity("url", "system", ScheduledTaskPrivilege.Limited));
    }

    /// <summary>
    /// NÖBETÇİ: kullanıcının KENDİ seçtiği sınırlı kimlik (site betiği) eskisi gibi çalışır ve
    /// site seçilmeden istenirse eskisi gibi hata verir — o kural düşürmeyle karışmadı.
    /// </summary>
    [Fact]
    public void Kullanicinin_SECTIGI_sinirli_kimlik_eski_kuraliyla_kalir()
    {
        Assert.True(SchedulerService.RequiresLimitedIdentity("script", "limited", ScheduledTaskPrivilege.Full));
        Assert.True(SchedulerService.RequiresLimitedIdentity("script", "apppool", ScheduledTaskPrivilege.Full));
        Assert.True(SchedulerService.IsExplicitLimitedRunAs("limited"));
        Assert.False(SchedulerService.IsExplicitLimitedRunAs("system"));
    }

    // =====================================================================================
    // 3) DENETİM KAYDI — yazılan kimlik ÖLÇÜLEN kimliktir
    // =====================================================================================

    /// <summary>
    /// Kayıt, görevin GERÇEKTEN hangi hesapla koştuğunu yazar (Windows'tan geri okunan değer),
    /// istekteki "system" dizesini değil. Aksi hâlde denetim kaydı panelin yaptığının tersini
    /// söylerdi.
    /// </summary>
    [Fact]
    public async Task Denetim_kaydi_GERI_OKUNAN_kimligi_yazar()
    {
        var duzenek = new Duzenek();
        duzenek.Zamanlayici.DonecekRunAs = "limited";
        var denetleyici = duzenek.Denetleyici(IkinciYonetici, "Administrator");

        await denetleyici.CreateTaskAsync(Istek("command"), CancellationToken.None);

        var kayit = Assert.Single(duzenek.Denetim.Kayitlar);
        Assert.Equal("cron.created", kayit.Action);
        Assert.Contains("identity=LOCAL SERVICE", kayit.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sahibin_gorevinde_kayit_SYSTEM_yazar()
    {
        var duzenek = new Duzenek();
        duzenek.Zamanlayici.DonecekRunAs = "system";
        var denetleyici = duzenek.Denetleyici(Sahip, "Administrator");

        await denetleyici.CreateTaskAsync(Istek("command"), CancellationToken.None);

        Assert.Contains("identity=SYSTEM", Assert.Single(duzenek.Denetim.Kayitlar).Details, StringComparison.Ordinal);
    }

    // =====================================================================================
    // 4) BİTMEYEN KABUK DENETİMİ — güvenlik sınırı DEĞİL, hata koruması
    //
    // Denetim "cmd.exe /c <herhangi bir şey>"i GEÇİRİR ve geçirmesi doğrudur: burada sorulan soru
    // "bu komut tehlikeli mi" değil, "bu görev bitecek mi". Yetki sınırı yukarıdaki kimlik
    // kararındadır. Eksik olan, denetimin KENDİ işini tam yapmamasıydı.
    // =====================================================================================

    [Theory]
    [InlineData("cmd.exe", "/k whoami")]
    [InlineData("cmd.exe", "/K C:\\betik\\bakim.bat")]
    [InlineData("powershell.exe", "-NoExit -Command Get-Date")]
    [InlineData("powershell.exe", "-NoLogo -NoExit")]
    [InlineData("pwsh.exe", "-NoExit")]
    public void Komuttan_SONRA_ACIK_KALAN_kabuk_da_reddedilir(string komut, string argumanlar)
    {
        Assert.True(SchedulerService.IsNonTerminatingShell(komut, argumanlar));
    }

    /// <summary>
    /// NÖBETÇİ: bayrak yalnız KABUĞUN KENDİ bölümünde aranır. <c>xcopy /K</c> gerçek ve meşru bir
    /// görevdir; satırda "/K" geçtiği için reddeden bir çözüm burada kırmızı yanar.
    /// </summary>
    [Theory]
    [InlineData("cmd.exe", "/c xcopy C:\\a C:\\b /K /E")]
    [InlineData("cmd.exe", "/c \"C:\\site\\bot.bat\"")]
    [InlineData("powershell.exe", "-NoProfile -File \"C:\\betik\\temizlik.ps1\" -NoExit")]
    [InlineData("powershell.exe", "-Command \"& { Get-Date }\"")]
    public void Biten_kabuk_gorevleri_HALA_kabul_edilir(string komut, string argumanlar)
    {
        Assert.False(SchedulerService.IsNonTerminatingShell(komut, argumanlar));
    }

    // =====================================================================================
    // düzenek
    // =====================================================================================

    private static CreateScheduledTaskRequest Istek(string tip) => new()
    {
        Name = "gece-bakim",
        TaskType = tip,
        Command = @"C:\araclar\bakim.exe",
        Arguments = "--gece",
        ScheduleType = "daily",
        TimeOfDay = "03:00"
    };

    private sealed class Duzenek
    {
        public KaydedenZamanlayici Zamanlayici { get; } = new();

        public RecordingAuditService Denetim { get; } = new();

        public CronController Denetleyici(Guid aktorId, string rol, Guid? taklitEden = null)
        {
            var denetleyici = new CronController(
                Zamanlayici, new SessizKapsam(), new SabitSahiplik(Sahip), Denetim);

            List<Claim> talepler =
            [
                new(ClaimTypes.NameIdentifier, aktorId.ToString()),
                new(ClaimTypes.Name, "aktor"),
                new(ClaimTypes.Role, rol)
            ];

            if (taklitEden is { } taklit)
            {
                talepler.Add(new Claim("impersonator_id", taklit.ToString()));
            }

            denetleyici.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(talepler, CookieAuthenticationDefaults.AuthenticationScheme))
                }
            };

            return denetleyici;
        }
    }

    /// <summary>Görevi KURMAZ, yalnız kendisine NE GÖNDERİLDİĞİNİ kaydeder.</summary>
    private sealed class KaydedenZamanlayici : ISchedulerService
    {
        public CreateScheduledTaskRequest? SonIstek { get; private set; }

        public ScheduledTaskPrivilege? SonYetki { get; private set; }

        /// <summary>Windows'tan GERİ OKUNAN kimliğin taklidi (denetim kaydı bunu yazmalı).</summary>
        public string DonecekRunAs { get; set; } = "system";

        public Task<ScheduledTaskDto> CreateTaskAsync(
            CreateScheduledTaskRequest request,
            ScheduledTaskPrivilege privilege,
            CancellationToken cancellationToken = default)
        {
            SonIstek = request;
            SonYetki = privilege;
            return Task.FromResult(new ScheduledTaskDto
            {
                Name = request.Name,
                TaskType = request.TaskType,
                RunAs = DonecekRunAs,
                State = "Ready"
            });
        }

        public Task<IReadOnlyCollection<ScheduledTaskDto>> GetTasksAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<ScheduledTaskDto>>([]);

        public Task DeleteTaskAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RunTaskAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ScheduledTaskStatusDto> GetTaskStatusAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ScheduledTaskOutputDto> GetTaskOutputAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TaskEnvironmentDto> GetEnvironmentAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MachinePathMeasurement> AddPythonToMachinePathAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetTaskEnabledAsync(string name, bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StopScheduledTaskResultDto> StopTaskAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> RepairTaskSettingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> RemoveTasksUnderRootAsync(string siteRootPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Sahibi TEST belirler; sahiplik ÇÖZÜMLEMESİ ServerOwnerTerminalTests'te ölçülüyor.</summary>
    private sealed class SabitSahiplik(Guid sahip) : IServerOwnerService
    {
        public Task<Guid?> GetServerOwnerIdAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<Guid?>(sahip);

        public Task<bool> IsServerOwnerAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(userId == sahip);
    }

    /// <summary>
    /// Kiracı dalının ihtiyaç duyduğu iki soruyu bilir (ebeveyn aboneliği + kota); gerisi bilerek
    /// patlar ki çözüm beklenmedik bir yoldan veri toplarsa test sessizce geçmesin.
    /// </summary>
    private sealed class SessizKapsam : ICustomerScopeService
    {
        public Task<Guid?> GetParentSubscriptionAsync(Guid userId, CancellationToken cancellationToken = default)
            => Task.FromResult<Guid?>(null);

        // 0 = sınırsız: kota davranışı bu dosyanın konusu değil.
        public Task<CustomerScope> GetScopeAsync(Guid userId, CancellationToken cancellationToken = default)
            => Task.FromResult(new CustomerScope([], 0, 0, 0, 0, 0));

        public Task<IReadOnlyCollection<string>?> GetSubUserSectionsAsync(Guid userId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<string>?>(null);

        public Task<IReadOnlyCollection<string>> GetAssignedSitesAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetScopeAsync(Guid userId, IEnumerable<string> siteNames, int maxSites, int maxDiskMb, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetExtraLimitsAsync(Guid userId, int maxDatabases, int maxMailboxes, int maxScheduledTasks, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveUserAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveSiteFromAllScopesAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> GetAllowedMailDomainsAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DiskUsage> GetDiskUsageMbAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetOwnerAsync(Guid userId, Guid? ownerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid?> GetOwnerAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetOwnedUserIdsAsync(Guid ownerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> GetResellerSitesAsync(Guid resellerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> GetResellerMailDomainsAsync(Guid resellerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid?> GetSiteOwnerUserIdAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetParentSubscriptionAsync(Guid userId, Guid? parentUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetSubUserIdsAsync(Guid parentUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetSubUserSectionsAsync(Guid userId, IReadOnlyCollection<string>? sections, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetPlanAsync(Guid userId, string? planId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetExpiryAsync(Guid userId, DateTime? expiresAtUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SuspendAsync(Guid userId, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ResumeAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetSuspendedUserIdsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetSubscriptionUserIdsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
