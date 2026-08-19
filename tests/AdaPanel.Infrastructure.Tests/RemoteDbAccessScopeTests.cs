using System.Reflection;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Database;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// KAPSAM KACAGI: bir kiracinin BASKA bir kiracinin veritabani kullanicisi icin uzak erisim
/// acabilmesi.
/// <para>
/// Bu, panelin en pahali hata sinifidir: musteri A, musteri B'nin veritabani hesabini internete
/// acabilseydi, B'nin verisi B hicbir sey yapmadan disariya acilmis olurdu. Sahiplik
/// IDatabaseOwnershipService'ten OKUNUR; ad benzerligine (musteri1_ oneki gibi) bakilmaz.
/// </para>
/// </summary>
public sealed class RemoteDbAccessScopeTests
{
    private static readonly Guid Kiraci = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid BaskaKiraci = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>
    /// Kapsam denetimi servisin private metodudur ve DIS DUNYADAN cagrilmasi gerekmez; testin
    /// dogrulamasi gereken sey KARARIN KENDISIDIR. Yansima ile dogrudan o karar sinanir; boylece
    /// test canli bir MariaDB'ye ya da HTTP yiginina bagli olmaz.
    /// </summary>
    private static async Task KapsamDenetleAsync(
        IDatabaseOwnershipService ownership,
        string[] databases,
        Guid actor,
        bool isAdministrator)
    {
        var service = (RemoteDbAccessService)Activator.CreateInstance(
            typeof(RemoteDbAccessService),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [null!, null!, ownership, null!, null!, null!, null!, null!],
            culture: null)!;

        var method = typeof(RemoteDbAccessService).GetMethod(
            "EnsureOwnershipAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        try
        {
            await (Task)method.Invoke(service, ["mariadb", databases, actor, isAdministrator, CancellationToken.None])!;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }

    /// <summary>ACIGIN KENDISI: baskasinin veritabanina baglı hesap icin ayar degistirilemez.</summary>
    [Fact]
    public async Task Tenant_CannotChangeRemoteAccessForAnotherTenantsDatabaseUser()
    {
        var ownership = new SahteSahiplik { ["mariadb|kurban_db"] = BaskaKiraci };

        var hata = await Assert.ThrowsAsync<AppException>(
            () => KapsamDenetleAsync(ownership, ["kurban_db"], Kiraci, isAdministrator: false));

        Assert.Equal("database.not_owned", hata.MessageKey);
    }

    /// <summary>Kendi veritabani icin ayar degistirebilir (denetim asiri kisitlayici degil).</summary>
    [Fact]
    public async Task Tenant_CanChangeRemoteAccessForItsOwnDatabaseUser()
    {
        var ownership = new SahteSahiplik { ["mariadb|benim_db"] = Kiraci };

        await KapsamDenetleAsync(ownership, ["benim_db"], Kiraci, isAdministrator: false);
    }

    /// <summary>
    /// ORTAK hesap: kullanici hem kendi hem baskasinin veritabanina yetkiliyse ayar
    /// DEGISTIRILEMEZ. Tek bir yabanci veritabani bile yeter; aksi halde kiraci, ortak hesabin
    /// host kumesini degistirerek digerinin veritabanina disaridan yol acardi.
    /// </summary>
    [Fact]
    public async Task SharedUser_TouchingAnotherTenantsDatabase_IsRejected()
    {
        var ownership = new SahteSahiplik
        {
            ["mariadb|benim_db"] = Kiraci,
            ["mariadb|komsu_db"] = BaskaKiraci
        };

        var hata = await Assert.ThrowsAsync<AppException>(
            () => KapsamDenetleAsync(ownership, ["benim_db", "komsu_db"], Kiraci, isAdministrator: false));

        Assert.Equal("database.not_owned", hata.MessageKey);
    }

    /// <summary>
    /// SAHIBI BILINMEYEN veritabani da reddedilir. "Kayit yoksa serbest" demek, sahiplik
    /// kaydi yazilmadan olusmus (disaridan gelmis, elle yaratilmis) her veritabanini herkese
    /// acik hale getirirdi.
    /// </summary>
    [Fact]
    public async Task DatabaseWithUnknownOwner_IsRejected()
    {
        var hata = await Assert.ThrowsAsync<AppException>(
            () => KapsamDenetleAsync(new SahteSahiplik(), ["sahipsiz_db"], Kiraci, isAdministrator: false));

        Assert.Equal("database.not_owned", hata.MessageKey);
    }

    /// <summary>
    /// Hicbir veritabanina yetkili OLMAYAN hesap da reddedilir: kapsami olcemedigimiz bir hesap
    /// icin "sahibi bu kiraci" varsayilamaz. Kapaliya dusmek burada guvenli yondur.
    /// </summary>
    [Fact]
    public async Task UserWithNoDatabases_IsRejectedInsteadOfAssumedOwned()
    {
        var hata = await Assert.ThrowsAsync<AppException>(
            () => KapsamDenetleAsync(new SahteSahiplik(), [], Kiraci, isAdministrator: false));

        Assert.Equal("database.not_owned", hata.MessageKey);
    }

    /// <summary>Yonetici muaftir; aksi halde admin kendi panelinde is yapamazdi.</summary>
    [Fact]
    public async Task Administrator_IsExemptFromOwnershipCheck()
    {
        var ownership = new SahteSahiplik { ["mariadb|kurban_db"] = BaskaKiraci };

        await KapsamDenetleAsync(ownership, ["kurban_db"], Kiraci, isAdministrator: true);
    }

    /// <summary>
    /// AD BENZERLIGI sahiplik DEGILDIR. Kiraci veritabanini "kiraci1_" onekiyle adlandirdi diye
    /// ayni onekli baskasinin veritabanina erisemez -- sahiplik yalnizca kayittan okunur.
    /// </summary>
    [Fact]
    public async Task SimilarDatabaseName_DoesNotImplyOwnership()
    {
        var ownership = new SahteSahiplik
        {
            ["mariadb|kiraci1_site"] = Kiraci,
            ["mariadb|kiraci1_baskasinin"] = BaskaKiraci
        };

        var hata = await Assert.ThrowsAsync<AppException>(
            () => KapsamDenetleAsync(ownership, ["kiraci1_baskasinin"], Kiraci, isAdministrator: false));

        Assert.Equal("database.not_owned", hata.MessageKey);
    }

    private sealed class SahteSahiplik : Dictionary<string, Guid>, IDatabaseOwnershipService
    {
        public SahteSahiplik()
            : base(StringComparer.OrdinalIgnoreCase)
        {
        }

        public Task<Guid?> GetOwnerAsync(string engine, string databaseName, CancellationToken cancellationToken = default)
            => Task.FromResult(TryGetValue($"{engine}|{databaseName}", out var owner) ? owner : (Guid?)null);

        public Task RecordAsync(string engine, string databaseName, Guid ownerUserId, string? siteName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RemoveAsync(string engine, string databaseName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RemoveBySiteAsync(string siteName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<int> CountForOwnerAsync(Guid ownerUserId, CancellationToken cancellationToken = default)
            => Task.FromResult(0);

        public Task<IReadOnlyCollection<(string Engine, string DatabaseName)>> GetOwnedAsync(Guid ownerUserId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<(string, string)>>([]);

        public Task<IReadOnlyCollection<(string Engine, string DatabaseName, Guid OwnerUserId, string SiteName)>> GetAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<(string, string, Guid, string)>>([]);
    }
}
