using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Firewall;
using AdaPanel.Infrastructure.Firewall;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// "Panel kendi erisimini kesiyor" sinifinin kapatilmasi.
/// <para>
/// BULUNAN ACIK: guvenlik duvari ucunda TEK dogrulama port araliginin 1-65535 olmasiydi. Yani
/// panelin KENDI portu (varsayilan formda 8890 YAZILI geliyordu ve "Izin ver" ile "Engelle"
/// dugmeleri yan yanaydi), uzak masaustu, 80 ve 443 engellenebiliyordu.
/// </para>
/// <para>
/// Windows'ta engelleme kurali izin kuralini EZER; ustelik panelde kural SILME yolu hic yoktu.
/// Yani tek yanlis tiktan sonra panele bir daha ulasilamiyor ve icerden geri alinamiyordu --
/// cikis yolu yalnizca sunucu konsoluydu. Ayni yonetici 3389'u da engellemisse o da kapaniyordu.
/// </para>
/// </summary>
public sealed class SelfLockoutGuardTests
{
    // Gunlukcu bu testlerde ONEMSIZ: burada dogrulanan sey, korunan portun REDDEDILMESI (kural hic
    // yazilmadan atilan bir AppException). Yutan catch bloklarina hic girilmez.
    private static readonly FirewallService Servis = new(NullLogger<FirewallService>.Instance);

    /// <summary>Panelin kendi portu engellenemez.</summary>
    [Fact]
    public async Task BlockingThePanelsOwnPort_IsRefused()
    {
        var istek = new UpdateFirewallRuleRequest { Port = 8890, Protocol = "TCP", RuleName = "deneme" };

        var hata = await Assert.ThrowsAsync<AppException>(() => Servis.BlockPortAsync(istek, panelPort: 8890));

        Assert.Equal("firewall.protected_port", hata.MessageKey);
    }

    /// <summary>Panel baska bir portta dinliyorsa KORUNAN da o porttur (sabit 8890 degil).</summary>
    [Fact]
    public async Task TheProtectedPort_FollowsWhereThePanelActuallyListens()
    {
        var istek = new UpdateFirewallRuleRequest { Port = 9443, Protocol = "TCP", RuleName = "deneme" };

        var hata = await Assert.ThrowsAsync<AppException>(() => Servis.BlockPortAsync(istek, panelPort: 9443));

        Assert.Equal("firewall.protected_port", hata.MessageKey);
    }

    /// <summary>
    /// Uzak masaustu ve web portlari da korunur: 3389 kapanirsa sunucuya uzaktan girisin SON yolu
    /// da kapanir; 80/443 kapanirsa barindirilan butun siteler birden duser.
    /// </summary>
    [Theory]
    [InlineData(3389)]
    [InlineData(80)]
    [InlineData(443)]
    public async Task BlockingCriticalPorts_IsRefused(int port)
    {
        var istek = new UpdateFirewallRuleRequest { Port = port, Protocol = "TCP", RuleName = "deneme" };

        var hata = await Assert.ThrowsAsync<AppException>(() => Servis.BlockPortAsync(istek, panelPort: 8890));

        Assert.Equal("firewall.protected_port", hata.MessageKey);
    }

    /// <summary>
    /// Koruma, mesru kullanimi ENGELLEMEMELI: siradan bir portu engellemek gecerli bir istektir.
    /// (Burada gercekten kural yazilmaya calisilir; bu makinede yonetici haklari olmayabilir, o
    /// yuzden yalnizca "korumali port" hatasi ALMADIGIMIZI dogrularız.)
    /// </summary>
    [Fact]
    public async Task BlockingAnOrdinaryPort_IsNotRefusedByTheGuard()
    {
        var istek = new UpdateFirewallRuleRequest { Port = 45678, Protocol = "TCP", RuleName = "AdaPanel Block TCP 45678" };

        try
        {
            await Servis.BlockPortAsync(istek, panelPort: 8890);
        }
        catch (AppException hata)
        {
            Assert.NotEqual("firewall.protected_port", hata.MessageKey);
        }
    }

    /// <summary>
    /// Panel YALNIZ kendi kurallarini silebilir: yoneticinin ya da Windows'un kurdugu kurallari
    /// panelden silmek, sorumlulugu disindaki bir sistemi sessizce degistirmek olurdu.
    /// </summary>
    [Fact]
    public async Task RemovingAForeignRule_IsRefused()
    {
        var hata = await Assert.ThrowsAsync<AppException>(() => Servis.RemoveRuleAsync("Core Networking - DHCP (In)"));

        Assert.Equal("firewall.rule_not_managed", hata.MessageKey);
    }

    [Fact]
    public async Task RemovingARuleWithoutAName_IsRefused()
    {
        var hata = await Assert.ThrowsAsync<AppException>(() => Servis.RemoveRuleAsync("   "));

        Assert.Equal("firewall.invalid_rule_name", hata.MessageKey);
    }

    /// <summary>
    /// Kaynak sozlesmesi: kurtarma kolu (recover-access.flag) panelin ENGELLEME kurallarini da
    /// temizlemeli. Kural yazip silememek tek yonlu bir kapiydi.
    /// </summary>
    [Fact]
    public void TheAccessRecoveryFlag_AlsoClearsBlockRules()
    {
        var kaynak = OkuKaynak("src", "AdaPanel.Api", "Program.cs");

        Assert.Contains("ClearManagedBlockRulesAsync", kaynak, StringComparison.Ordinal);
    }

    private static string OkuKaynak(params string[] parts)
    {
        var klasor = AppContext.BaseDirectory;
        for (var derinlik = 0; derinlik < 8; derinlik++)
        {
            var aday = Path.Combine(new[] { klasor }.Concat(parts).ToArray());
            if (File.Exists(aday))
            {
                return File.ReadAllText(aday);
            }

            klasor = Path.Combine(klasor, "..");
        }

        throw new FileNotFoundException(string.Join('/', parts));
    }
}
