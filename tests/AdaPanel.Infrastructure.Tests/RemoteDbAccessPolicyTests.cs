using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Infrastructure.Database;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Uzak erisim POLITIKASI: varsayilan kapali, "her yer" acik onay ister, guvenlik duvarina
/// sessizce dokunulmaz.
/// </summary>
public sealed class RemoteDbAccessPolicyTests
{
    private static UpdateDatabaseUserRemoteAccessRequest Istek(
        string mode,
        string[]? origins = null,
        bool confirmAnywhere = false) =>
        new()
        {
            Engine = "mariadb",
            UserName = "site_wp",
            Mode = mode,
            AllowedOrigins = origins,
            ConfirmAnywhere = confirmAnywhere
        };

    // ---------------------------------------------------------------------------------
    // Varsayilan kapali
    // ---------------------------------------------------------------------------------

    /// <summary>Istek nesnesinin kendi varsayilani KAPALI olmali: unutulan alan kapi acmasin.</summary>
    [Fact]
    public void RequestDefaults_AreClosedAndUnconfirmed()
    {
        var istek = new UpdateDatabaseUserRemoteAccessRequest();

        Assert.Equal("closed", istek.Mode);
        Assert.False(istek.ConfirmAnywhere);
        Assert.False(istek.ApplyFirewall);
    }

    /// <summary>Kapali kip hicbir kaynak uretmez.</summary>
    [Fact]
    public void ClosedMode_ProducesNoOrigins()
    {
        Assert.Empty(RemoteDbAccessService.ResolveOrigins(RemoteAccessMode.Closed, Istek("closed")));
    }

    /// <summary>
    /// TANINMAYAN kip sessizce varsayilana DUSMEZ. Dusseydi, "anywhre" gibi bir yazim hatasi
    /// kullaniciya "her yere actim" dedirtirken sunucuda kapali kalabilir (ya da tersi) olurdu:
    /// ekranda gorulen ile sunucudaki gercek ayrisirdi.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("anywhre")]
    [InlineData("ALL")]
    [InlineData("open")]
    public void UnknownMode_IsRejected(string mode)
    {
        var hata = Assert.Throws<AppException>(() => RemoteDbAccessService.ParseMode(mode));
        Assert.Equal("database.remote_mode_invalid", hata.MessageKey);
    }

    /// <summary>Bilinen kipler (buyuk/kucuk harf duyarsiz) dogru cozulur.</summary>
    [Theory]
    [InlineData("closed", "Closed")]
    [InlineData("allowlist", "Allowlist")]
    [InlineData("ANYWHERE", "Anywhere")]
    [InlineData("  anywhere  ", "Anywhere")]
    public void KnownModes_AreParsed(string metin, string beklenen)
    {
        Assert.Equal(beklenen, RemoteDbAccessService.ParseMode(metin).ToString());
    }

    // ---------------------------------------------------------------------------------
    // "Her yer" acik onay ister
    // ---------------------------------------------------------------------------------

    /// <summary>Onaysiz "her yer" reddedilir.</summary>
    [Fact]
    public void AnywhereMode_WithoutConfirmation_IsRejected()
    {
        var hata = Assert.Throws<AppException>(
            () => RemoteDbAccessService.ResolveOrigins(RemoteAccessMode.Anywhere, Istek("anywhere")));

        Assert.Equal("database.remote_anywhere_not_confirmed", hata.MessageKey);
    }

    [Fact]
    public void AnywhereMode_WithConfirmation_IsAllowed()
    {
        var origins = RemoteDbAccessService.ResolveOrigins(
            RemoteAccessMode.Anywhere,
            Istek("anywhere", confirmAnywhere: true));

        Assert.True(Assert.Single(origins).IsAnywhere);
    }

    /// <summary>
    /// ONAY KAPISINI ATLATMA DENEMESI: kullanici "liste" kipini secip listeye 0.0.0.0/0 yazarak
    /// ayni sonuca ulasmaya calisirsa yine onay istenir. Kapi kipe degil SONUCA bakar.
    /// </summary>
    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("%")]
    [InlineData("::/0")]
    [InlineData("1.2.3.4/0")]
    public void AllowlistContainingAnywhere_StillRequiresConfirmation(string kaynak)
    {
        var hata = Assert.Throws<AppException>(() => RemoteDbAccessService.ResolveOrigins(
            RemoteAccessMode.Allowlist,
            Istek("allowlist", [kaynak])));

        Assert.Equal("database.remote_anywhere_not_confirmed", hata.MessageKey);
    }

    /// <summary>Listede "her yer" gizliyse ve onay verilmisse gecer (kullanici ne yaptigini beyan etti).</summary>
    [Fact]
    public void AllowlistContainingAnywhere_PassesWhenConfirmed()
    {
        var origins = RemoteDbAccessService.ResolveOrigins(
            RemoteAccessMode.Allowlist,
            Istek("allowlist", ["88.1.2.3", "0.0.0.0/0"], confirmAnywhere: true));

        Assert.Contains(origins, origin => origin.IsAnywhere);
    }

    // ---------------------------------------------------------------------------------
    // Liste kipi
    // ---------------------------------------------------------------------------------

    /// <summary>Kaynaksiz "liste" kipi anlamsizdir; sessizce kapaliya donusturulmez.</summary>
    [Fact]
    public void AllowlistWithoutOrigins_IsRejected()
    {
        foreach (var origins in new[] { null, Array.Empty<string>(), new[] { "  ", "" } })
        {
            var hata = Assert.Throws<AppException>(
                () => RemoteDbAccessService.ResolveOrigins(RemoteAccessMode.Allowlist, Istek("allowlist", origins)));

            Assert.Equal("database.remote_origin_required", hata.MessageKey);
        }
    }

    /// <summary>Coklu kaynak: eklenen ozelligin asil talebi (tek IP yerine liste/CIDR).</summary>
    [Fact]
    public void Allowlist_AcceptsMultipleIpsAndCidrs()
    {
        var origins = RemoteDbAccessService.ResolveOrigins(
            RemoteAccessMode.Allowlist,
            Istek("allowlist", ["88.1.2.3", "10.0.0.0/8", "192.168.1.%"]));

        Assert.Equal(["88.1.2.3", "10.0.0.0/8", "192.168.1.%"], origins.Select(item => item.Canonical));
    }

    // ---------------------------------------------------------------------------------
    // Guvenlik duvari adresleri kullanici listelerinin BIRLESIMIDIR
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Kimsenin uzak erisimi yoksa acilacak adres YOKTUR -> kural kaldirilir. Boylece "son uzak
    /// kullaniciyi kapattim ama port hala acik" durumu olusamaz.
    /// </summary>
    [Fact]
    public void FirewallAddresses_AreEmptyWhenNoUserHasRemoteAccess()
    {
        var state = new RemoteDbAccessService.RemoteDbState();
        state.Users["mariadb|site_wp"] = new RemoteDbAccessService.RemoteUserAccessState
        {
            Mode = "closed",
            Origins = ["88.1.2.3"]
        };

        Assert.Empty(RemoteDbAccessService.CollectFirewallAddresses("mariadb", state));
    }

    /// <summary>Kural, kullanicilarin izin verdigi adreslerin BIRLESIMIDIR; panel fazladan adres acmaz.</summary>
    [Fact]
    public void FirewallAddresses_AreTheUnionOfUserAllowlists()
    {
        var state = new RemoteDbAccessService.RemoteDbState();
        state.Users["mariadb|a_db"] = new RemoteDbAccessService.RemoteUserAccessState
        {
            Mode = "allowlist",
            Origins = ["88.1.2.3"]
        };
        state.Users["mariadb|b_db"] = new RemoteDbAccessService.RemoteUserAccessState
        {
            Mode = "allowlist",
            Origins = ["10.0.0.0/8", "88.1.2.3"]
        };

        Assert.Equal(["88.1.2.3", "10.0.0.0/8"], RemoteDbAccessService.CollectFirewallAddresses("mariadb", state));
    }

    /// <summary>BASKA MOTORUN kullanicisi bu motorun portunu actiramaz.</summary>
    [Fact]
    public void FirewallAddresses_DoNotLeakAcrossEngines()
    {
        var state = new RemoteDbAccessService.RemoteDbState();
        state.Users["postgresql|pg_user"] = new RemoteDbAccessService.RemoteUserAccessState
        {
            Mode = "allowlist",
            Origins = ["88.1.2.3"]
        };

        Assert.Empty(RemoteDbAccessService.CollectFirewallAddresses("mariadb", state));
        Assert.Equal(["88.1.2.3"], RemoteDbAccessService.CollectFirewallAddresses("postgresql", state));
    }

    /// <summary>Tek bir "her yer" varsa liste anlamsizdir: kural zaten her yeri kapsar.</summary>
    [Fact]
    public void FirewallAddresses_CollapseToAnyWhenSomeoneIsOpenToTheWorld()
    {
        var state = new RemoteDbAccessService.RemoteDbState();
        state.Users["mariadb|a_db"] = new RemoteDbAccessService.RemoteUserAccessState
        {
            Mode = "allowlist",
            Origins = ["88.1.2.3"]
        };
        state.Users["mariadb|b_db"] = new RemoteDbAccessService.RemoteUserAccessState
        {
            Mode = "anywhere",
            Origins = ["%"]
        };

        Assert.Equal(["Any"], RemoteDbAccessService.CollectFirewallAddresses("mariadb", state));
    }

    /// <summary>
    /// Kayitta BOZUK bir deger digerlerinin erisimini kapatmamali: o satir atlanir, kural yine
    /// yazilir. Aksi halde tek bozuk kayit butun musterilerin uzak erisimini dusururdu.
    /// </summary>
    [Fact]
    public void FirewallAddresses_SkipCorruptEntriesInsteadOfFailing()
    {
        var state = new RemoteDbAccessService.RemoteDbState();
        state.Users["mariadb|a_db"] = new RemoteDbAccessService.RemoteUserAccessState
        {
            Mode = "allowlist",
            Origins = ["bozuk deger", "88.1.2.3"]
        };

        Assert.Equal(["88.1.2.3"], RemoteDbAccessService.CollectFirewallAddresses("mariadb", state));
    }
}
