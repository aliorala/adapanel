using System.Reflection;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Security;
using AdaPanel.Infrastructure.Subscriptions;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Dalga 3 (R3): abonelik askısı posta kutularını ve MariaDB/MySQL hesaplarını gerçekten kapatır;
/// yalnız ÖNCE AÇIK olanı geri açar; kapatamadığını "kapattım" demez.
/// </summary>
public sealed class SubscriptionSuspensionMailDbTests : IDisposable
{
    private readonly string kok = Path.Combine(Path.GetTempPath(), "adapanel-w3-aski-" + Guid.NewGuid().ToString("N"));

    public SubscriptionSuspensionMailDbTests() => Directory.CreateDirectory(kok);

    public void Dispose()
    {
        try { Directory.Delete(kok, recursive: true); } catch { /* temizlik zorunlu değil */ }
    }

    private sealed class Duzenek
    {
        public required CustomerScopeService Kapsamlar { get; init; }
        public SubscriptionResourceSuspender Kapatici { get; set; } = null!;
        public Dictionary<string, bool?> Kutular { get; } = new(StringComparer.OrdinalIgnoreCase); // adres -> etkin
        public HashSet<string> KapanmayanKutular { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool KutularOlculemedi { get; set; }
        public bool PostaDesteklemiyor { get; set; }
        public List<(string User, string Host, bool Locked)> Kilitler { get; } = [];
        public List<DatabaseUserLockDto> Hesaplar { get; } = [];
        public List<string> SorulanVeritabanlari { get; } = [];
    }

    private Duzenek Kur(string sahipDb = "uye_db")
    {
        var siteler = new PlanLimitsW2bTests.DurumluSiteler(Path.Combine(kok, "siteler"));
        siteler.Ekle("a.test", "Started");
        var store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = kok }));
        var kapsamlar = new CustomerScopeService(store, new AskiDenetimKaydiDurustlukTests.PlansizServis(), new AskiDenetimKaydiDurustlukTests.TavansizServis(), siteler);
        var d = new Duzenek { Kapsamlar = kapsamlar };

        var posta = Fake<IMailService>((metot, args) => metot.Name switch
        {
            nameof(IMailService.GetWorkspaceAsync) => Task.FromResult(new MailWorkspaceDto
            {
                Installed = true,
                Configured = true,
                AccountsMeasured = !d.KutularOlculemedi,
                Accounts = d.Kutular.Select(item => new MailAccountDto { Address = item.Key, Active = item.Value }).ToList()
            }),
            nameof(IMailService.SetAccountActiveAsync) => KutuAyarla(d, (SetMailAccountActiveRequest)args![0]!),
            _ => throw new NotSupportedException(metot.Name)
        });
        var sahiplik = Fake<IDatabaseOwnershipService>((metot, args) => metot.Name switch
        {
            nameof(IDatabaseOwnershipService.GetOwnedAsync) => Task.FromResult<IReadOnlyCollection<(string Engine, string DatabaseName)>>([("mariadb", sahipDb)]),
            _ => throw new NotSupportedException(metot.Name)
        });
        var veritabani = Fake<IDatabaseService>((metot, args) => metot.Name switch
        {
            nameof(IDatabaseService.GetUserLocksAsync) => GetLocks(d, (IReadOnlyCollection<string>)args![1]!),
            nameof(IDatabaseService.SetUserLockedAsync) => KilitAyarla(d, (string)args![1]!, (string)args[2]!, (bool)args[3]!),
            _ => throw new NotSupportedException(metot.Name)
        });

        d.Kapatici = new SubscriptionResourceSuspender(kapsamlar, siteler, NullLogger<SubscriptionResourceSuspender>.Instance,
            null, null, posta, veritabani, sahiplik);
        return d;
    }

    private static Task<IReadOnlyCollection<DatabaseUserLockDto>> GetLocks(Duzenek d, IReadOnlyCollection<string> adlar)
    {
        d.SorulanVeritabanlari.AddRange(adlar);
        return Task.FromResult<IReadOnlyCollection<DatabaseUserLockDto>>(d.Hesaplar.ToArray());
    }

    private static Task KilitAyarla(Duzenek d, string user, string host, bool kilitli)
    {
        d.Kilitler.Add((user, host, kilitli));
        var index = d.Hesaplar.FindIndex(item => item.UserName == user);
        var eski = d.Hesaplar[index];
        d.Hesaplar[index] = new DatabaseUserLockDto { Engine = eski.Engine, UserName = eski.UserName, Host = eski.Host, Exclusive = eski.Exclusive, Locked = kilitli };
        return Task.CompletedTask;
    }

    private static Task<MailWorkspaceDto> KutuAyarla(Duzenek d, SetMailAccountActiveRequest istek)
    {
        if (d.PostaDesteklemiyor) throw new NotSupportedException("mailenable");
        if (d.KapanmayanKutular.Contains(istek.Address)) throw new InvalidOperationException("kutu islenemedi: " + istek.Address);
        d.Kutular[istek.Address] = istek.Active;
        return Task.FromResult(new MailWorkspaceDto());
    }

    private static string[] Sites => ["a.test"];

    [Fact]
    public async Task Aski_posta_kutularini_kapatir_ve_yalniz_ONCE_ACIK_olanlari_geri_acar()
    {
        var d = Kur();
        var kullanici = Guid.NewGuid();
        await d.Kapsamlar.SetScopeAsync(kullanici, ["a.test"], 5, 1024);
        d.Kutular["acik@a.test"] = true;
        d.Kutular["ellekapali@a.test"] = false; // musteri/yonetici elle askiya almis

        var rapor = await d.Kapatici.CloseAsync(kullanici, Sites, default);

        Assert.False(d.Kutular["acik@a.test"]);
        Assert.False(d.Kutular["ellekapali@a.test"]);
        Assert.Contains("1 mailbox(es)", rapor.Extras, StringComparison.Ordinal);
        var durum = await d.Kapsamlar.GetSuspensionStateAsync(kullanici);
        Assert.DoesNotContain("mail", durum!.Unsupported);   // artik "desteklenmiyor" DEGIL
        Assert.Contains("mail:acik@a.test", durum.OpenBefore);
        Assert.DoesNotContain("mail:ellekapali@a.test", durum.OpenBefore);

        await d.Kapatici.RestoreAsync(kullanici, default);

        Assert.True(d.Kutular["acik@a.test"]);
        Assert.False(d.Kutular["ellekapali@a.test"]); // elle askiya alinan kutu ACILMAZ
        Assert.Null(await d.Kapsamlar.GetSuspensionStateAsync(kullanici));
    }

    [Fact]
    public async Task Kapatilamayan_kutu_BEKLEYEN_adim_olur_basari_denmez_ve_yeniden_denemede_kapanir()
    {
        var d = Kur();
        var kullanici = Guid.NewGuid();
        await d.Kapsamlar.SetScopeAsync(kullanici, ["a.test"], 5, 1024);
        d.Kutular["x@a.test"] = true;
        d.KapanmayanKutular.Add("x@a.test");

        var rapor = await d.Kapatici.CloseAsync(kullanici, Sites, default);

        Assert.True(rapor.HasPending);
        Assert.Contains("NOT DISABLED", rapor.Extras, StringComparison.Ordinal);
        Assert.Contains("mail:x@a.test", rapor.Extras, StringComparison.Ordinal);
        Assert.True(d.Kutular["x@a.test"]);

        d.KapanmayanKutular.Clear();
        var tekrar = await d.Kapatici.RetryAsync(kullanici, shouldBeClosed: true, Sites, default);
        Assert.NotNull(tekrar);
        Assert.False(tekrar!.HasPending);
        Assert.False(d.Kutular["x@a.test"]);
    }

    [Fact]
    public async Task Kutular_olculemezse_kapatildi_DENMEZ_bekleyen_adim_olur()
    {
        var d = Kur();
        var kullanici = Guid.NewGuid();
        await d.Kapsamlar.SetScopeAsync(kullanici, ["a.test"], 5, 1024);
        d.Kutular["x@a.test"] = true;
        d.KutularOlculemedi = true;

        var rapor = await d.Kapatici.CloseAsync(kullanici, Sites, default);

        Assert.True(rapor.HasPending);
        Assert.True(d.Kutular["x@a.test"]);
        Assert.Contains("mail:*", rapor.Extras, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MailEnable_gibi_desteklemeyen_sunucuda_kutu_desteklenmiyor_olarak_kaydedilir()
    {
        var d = Kur();
        var kullanici = Guid.NewGuid();
        await d.Kapsamlar.SetScopeAsync(kullanici, ["a.test"], 5, 1024);
        d.Kutular["x@a.test"] = true;
        d.PostaDesteklemiyor = true;

        var rapor = await d.Kapatici.CloseAsync(kullanici, Sites, default);

        var durum = await d.Kapsamlar.GetSuspensionStateAsync(kullanici);
        Assert.Contains("mail", durum!.Unsupported);
        Assert.DoesNotContain("mail:x@a.test", durum.OpenBefore);
        Assert.Contains("CANNOT BE SUSPENDED", rapor.Extras, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Veritabani_hesaplari_kilitlenir_yalniz_abonelik_veritabanlari_sorulur_paylasilan_hesaba_dokunulmaz()
    {
        var d = Kur(sahipDb: "uye_db");
        var kullanici = Guid.NewGuid();
        await d.Kapsamlar.SetScopeAsync(kullanici, ["a.test"], 5, 1024);
        d.Hesaplar.Add(new() { Engine = "mariadb", UserName = "uye_u", Host = "localhost", Exclusive = true, Locked = false });
        d.Hesaplar.Add(new() { Engine = "mariadb", UserName = "ortak_u", Host = "localhost", Exclusive = false, Locked = false });
        d.Hesaplar.Add(new() { Engine = "mariadb", UserName = "elle_u", Host = "localhost", Exclusive = true, Locked = true });

        var rapor = await d.Kapatici.CloseAsync(kullanici, Sites, default);

        Assert.Equal(["uye_db"], d.SorulanVeritabanlari);                 // baska kiracinin veritabani sorulmaz
        Assert.Equal([("uye_u", "localhost", true)], d.Kilitler);           // paylasilan ve elle kilitli hesaba dokunulmadi
        var durum = await d.Kapsamlar.GetSuspensionStateAsync(kullanici);
        Assert.Contains("dbuser:mariadb|uye_u|localhost", durum!.OpenBefore);
        Assert.DoesNotContain("dbuser:mariadb|elle_u|localhost", durum.OpenBefore);
        Assert.Contains(durum.Unsupported, item => item.Contains("ortak_u", StringComparison.Ordinal)); // paylasilan: adiyla raporlanir
        Assert.Contains("1 database account(s)", rapor.Extras, StringComparison.Ordinal);

        d.Kilitler.Clear();
        await d.Kapatici.RestoreAsync(kullanici, default);

        Assert.Equal([("uye_u", "localhost", false)], d.Kilitler);          // yalniz onceden ACIK olan acilir
        Assert.True(d.Hesaplar.Single(item => item.UserName == "elle_u").Locked);
    }

    [Fact]
    public async Task Servisler_yoksa_posta_ve_veritabani_eskisi_gibi_Unsupported_kaydedilir()
    {
        var siteler = new PlanLimitsW2bTests.DurumluSiteler(Path.Combine(kok, "siteler2"));
        siteler.Ekle("a.test", "Started");
        var store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = kok }));
        var kapsamlar = new CustomerScopeService(store, new AskiDenetimKaydiDurustlukTests.PlansizServis(), new AskiDenetimKaydiDurustlukTests.TavansizServis(), siteler);
        var kullanici = Guid.NewGuid();
        await kapsamlar.SetScopeAsync(kullanici, ["a.test"], 5, 1024);
        var kapatici = new SubscriptionResourceSuspender(kapsamlar, siteler, NullLogger<SubscriptionResourceSuspender>.Instance);

        await kapatici.CloseAsync(kullanici, Sites, default);

        var durum = await kapsamlar.GetSuspensionStateAsync(kullanici);
        Assert.Contains("mail", durum!.Unsupported);
        Assert.Contains("database", durum.Unsupported);
    }

    private static T Fake<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var stub = DispatchProxy.Create<T, PlanLimitsW2bTests.Proxy>();
        ((PlanLimitsW2bTests.Proxy)(object)stub).Handler = handler;
        return stub;
    }
}
