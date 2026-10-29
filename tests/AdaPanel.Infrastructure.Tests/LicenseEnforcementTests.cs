using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Licensing;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Licensing;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Lisans mekanizmasının SÖZÜNÜ tutup tutmadığını doğrular.
/// </summary>
/// <remarks>
/// <para>
/// Bir lisans mekanizmasının tehlikesi çalışmaması değil, YANLIŞ ÇALIŞMASIDIR: bir ağ kesintisinde
/// müşterinin panelini kilitlemesi, ya da tersine hiçbir zaman kısıtlamaması. Buradaki testler dört
/// vaadi kilitler:
/// </para>
/// <list type="number">
///   <item>Yapılandırılmamışken panel KISITSIZ çalışır ve hiçbir yere istek atmaz.</item>
///   <item>Sunucuya ulaşılamazsa çevrimdışı tolerans dolana kadar çalışmaya DEVAM eder.</item>
///   <item>Lisans geçersizken YALNIZ yeni kaynak kısıtlanır; durum okunabilir, mevcutlar durmaz.</item>
///   <item>Ağ hatası, daha önce alınmış "geçerli" kararını ASLA "geçersiz"e çevirmez.</item>
/// </list>
/// </remarks>
public sealed class LicenseEnforcementTests : IDisposable
{
    private readonly string dataRoot = Path.Combine(
        Path.GetTempPath(),
        "adapanel-license-tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
        catch
        {
            // Temizlik en iyi çaba; testin sonucunu etkilemez.
        }
    }

    // --- 1) YAPILANDIRILMAMIŞ: kısıt yok, ağ trafiği yok ---

    [Fact]
    public async Task Yapilandirilmamisken_panel_KISITSIZ_calisir_ve_HICBIR_yere_istek_atmaz()
    {
        var sunucu = new SahteLisansSunucusu();
        var servis = Servis(sunucu, verificationUrl: string.Empty);

        var durum = await servis.GetStatusAsync();

        Assert.Equal(LicenseState.NotConfigured, durum.State);
        Assert.False(durum.IsConfigured);
        Assert.False(durum.RestrictsNewResources);

        // Kaynak açma kapısı da açık olmalı.
        await servis.EnsureCanCreateAsync(LicensedResource.Site);
        await servis.EnsureCanCreateAsync(LicensedResource.Subscription, existingCount: 9999);

        // GİZLİLİK: yapılandırılmadıkça tek bir istek bile gitmez.
        Assert.Equal(0, sunucu.CagriSayisi);
    }

    // --- 2) ÇEVRİMDIŞI TOLERANS ---

    [Fact]
    public async Task Aktivasyon_sirasinda_sunucuya_ULASILAMASA_bile_anahtar_saklanir_ve_tolerans_baslar()
    {
        // Kurulum anında ağ yoksa müşteri lisansını girip çalışmaya başlayabilmeli.
        var sunucu = new SahteLisansSunucusu { Ulasilabilir = false };
        var saat = new SabitSaat(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var servis = Servis(sunucu, saat: saat);

        var durum = await servis.ActivateAsync("ADA-TEST-0001");

        Assert.True(durum.HasKey);
        Assert.Equal("0001", durum.KeySuffix);
        Assert.Equal(LicenseState.Grace, durum.State);
        Assert.False(durum.RestrictsNewResources);
        await servis.EnsureCanCreateAsync(LicensedResource.Site);
    }

    [Fact]
    public async Task Cevrimdisi_tolerans_DOLANA_KADAR_panel_calismaya_DEVAM_eder()
    {
        var sunucu = new SahteLisansSunucusu();
        var saat = new SabitSaat(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var servis = Servis(sunucu, saat: saat, offlineGraceDays: 14);

        await servis.ActivateAsync("ADA-TEST-0001");

        // Sunucu düşüyor; 13 gün sonra bile panel kısıtsız.
        sunucu.Ulasilabilir = false;
        saat.Ilerlet(TimeSpan.FromDays(13));

        var durum = await servis.GetStatusAsync();
        Assert.False(durum.RestrictsNewResources);
        Assert.Equal(LicenseState.Grace, durum.State);
        await servis.EnsureCanCreateAsync(LicensedResource.Site);
    }

    [Fact]
    public async Task Cevrimdisi_tolerans_DOLUNCA_YENI_kaynak_kisitlanir()
    {
        var sunucu = new SahteLisansSunucusu();
        var saat = new SabitSaat(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var servis = Servis(sunucu, saat: saat, offlineGraceDays: 14);

        await servis.ActivateAsync("ADA-TEST-0001");

        sunucu.Ulasilabilir = false;
        saat.Ilerlet(TimeSpan.FromDays(15));

        var durum = await servis.GetStatusAsync();
        Assert.Equal(LicenseState.GraceExpired, durum.State);
        Assert.True(durum.RestrictsNewResources);

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.EnsureCanCreateAsync(LicensedResource.Site));
        Assert.Equal("license.offline_grace_expired", hata.MessageKey);
        Assert.Equal(402, hata.HttpStatusCode);
    }

    // --- 3) GEÇERSİZ LİSANS: yalnız yeni kaynak kısıtlanır ---

    [Fact]
    public async Task Gecersiz_lisansta_YENI_kaynak_kisitlanir_ama_durum_hala_OKUNABILIR()
    {
        var sunucu = new SahteLisansSunucusu();
        var saat = new SabitSaat(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var servis = Servis(sunucu, saat: saat);

        await servis.ActivateAsync("ADA-TEST-0001");

        // Sunucu sonradan anahtarı iptal etti (ödeme iadesi, kötüye kullanım).
        sunucu.Karar = LicenseVerdicts.Invalid;
        var durum = await servis.RefreshAsync();

        Assert.Equal(LicenseState.Invalid, durum.State);
        Assert.True(durum.RestrictsNewResources);

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.EnsureCanCreateAsync(LicensedResource.Database));
        Assert.Equal("license.invalid", hata.MessageKey);

        // AMA panel hâlâ konuşuyor: durum sorgusu PATLAMAZ, anahtar bilgisi görünür.
        // (Müşteri sorunu görüp çözebilmeli; kilitlenmiş bir panel destek çağrısıdır.)
        var tekrar = await servis.GetStatusAsync();
        Assert.True(tekrar.HasKey);
        Assert.Equal(LicenseState.Invalid, tekrar.State);
    }

    [Fact]
    public async Task Suresi_dolan_lisans_YENI_kaynagi_kisitlar()
    {
        var sunucu = new SahteLisansSunucusu { BitisUtc = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc) };
        var saat = new SabitSaat(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var servis = Servis(sunucu, saat: saat);

        await servis.ActivateAsync("ADA-TEST-0001");
        Assert.False((await servis.GetStatusAsync()).RestrictsNewResources);

        // Bitiş tarihi geçti; sunucuya sormaya bile gerek yok.
        saat.Ilerlet(TimeSpan.FromDays(45));
        sunucu.Ulasilabilir = false;

        var durum = await servis.GetStatusAsync();
        Assert.Equal(LicenseState.Expired, durum.State);
        Assert.True(durum.RestrictsNewResources);
    }

    // --- 4) AĞ HATASI BİR KARAR DEĞİLDİR ---

    [Fact]
    public async Task Ag_hatasi_onceki_GECERLI_karari_gecersize_CEVIRMEZ()
    {
        // Bu, mekanizmanın en tehlikeli hatası olurdu: lisans sunucusu bakıma girdiğinde
        // bütün müşterilerin paneli "geçersiz lisans" demeye başlardı.
        var sunucu = new SahteLisansSunucusu();
        var saat = new SabitSaat(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var servis = Servis(sunucu, saat: saat);

        await servis.ActivateAsync("ADA-TEST-0001");

        sunucu.Ulasilabilir = false;
        saat.Ilerlet(TimeSpan.FromHours(24));
        var durum = await servis.RefreshAsync();

        Assert.NotEqual(LicenseState.Invalid, durum.State);
        Assert.False(durum.RestrictsNewResources);
        await servis.EnsureCanCreateAsync(LicensedResource.Mailbox);
    }

    [Fact]
    public async Task Sunucu_anahtari_REDDEDERSE_anahtar_saklanmaz()
    {
        var sunucu = new SahteLisansSunucusu { Karar = LicenseVerdicts.Invalid };
        var servis = Servis(sunucu);

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.ActivateAsync("YANLIS-ANAHTAR"));
        Assert.Equal("license.invalid", hata.MessageKey);

        var durum = await servis.GetStatusAsync();
        Assert.False(durum.HasKey);
        Assert.Equal(LicenseState.Unlicensed, durum.State);
    }

    // --- SKU / kademe limiti ---

    [Fact]
    public async Task SKU_abonelik_limiti_UYGULANIR()
    {
        var sunucu = new SahteLisansSunucusu { EnFazlaAbonelik = 2 };
        var servis = Servis(sunucu);

        await servis.ActivateAsync("ADA-TEST-0001");

        // İki abonelikli pakette birinciyi açmak serbest, üçüncüsü değil.
        await servis.EnsureCanCreateAsync(LicensedResource.Subscription, existingCount: 1);

        var hata = await Assert.ThrowsAsync<AppException>(
            () => servis.EnsureCanCreateAsync(LicensedResource.Subscription, existingCount: 2));
        Assert.Equal("license.subscription_limit", hata.MessageKey);

        // Limit YALNIZ aboneliğe aittir; site/DB açmayı engellemez.
        await servis.EnsureCanCreateAsync(LicensedResource.Site, existingCount: 9999);
    }

    [Fact]
    public async Task Limitsiz_SKU_hicbir_aboneligi_engellemez()
    {
        var sunucu = new SahteLisansSunucusu { EnFazlaAbonelik = 0 };
        var servis = Servis(sunucu);

        await servis.ActivateAsync("ADA-TEST-0001");
        await servis.EnsureCanCreateAsync(LicensedResource.Subscription, existingCount: 100_000);
    }

    // --- Anahtar saklama disiplini ---

    [Fact]
    public async Task Anahtar_diske_DUZ_METIN_yazilmaz()
    {
        var sunucu = new SahteLisansSunucusu();
        var servis = Servis(sunucu);

        await servis.ActivateAsync("ADA-GIZLI-ANAHTAR-1234");

        var durumDosyasi = Path.Combine(dataRoot, "config", "license", "state.json");
        Assert.True(File.Exists(durumDosyasi));

        var icerik = await File.ReadAllTextAsync(durumDosyasi);
        Assert.DoesNotContain("ADA-GIZLI-ANAHTAR-1234", icerik, StringComparison.Ordinal);
        // Gösterim için yalnız son dört karakter durur.
        Assert.Contains("1234", icerik, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deaktivasyon_anahtari_siler()
    {
        var sunucu = new SahteLisansSunucusu();
        var servis = Servis(sunucu);

        await servis.ActivateAsync("ADA-TEST-0001");
        var durum = await servis.DeactivateAsync();

        Assert.False(durum.HasKey);
        Assert.Equal(LicenseState.Unlicensed, durum.State);
        Assert.True(durum.RestrictsNewResources);
    }

    // --- SAF KURAL (LicenseDecision) doğrudan ---

    [Fact]
    public void Bozuk_durum_dosyasi_musteriyi_CEZALANDIRMAZ()
    {
        // Anahtar var ama ne doğrulama ne de giriş zamanı biliniyor (dosya elle bozulmuş).
        // Bizim veri hatamız yüzünden müşterinin paneli kısıtlanmamalı.
        var kayit = new LicenseRecord { IsConfigured = true, ProtectedKey = "x" };
        var karar = LicenseDecision.Evaluate(kayit, 14, 12, DateTime.UtcNow);

        Assert.Equal(LicenseState.Grace, karar.State);
        Assert.False(karar.RestrictsNewResources);
    }

    [Fact]
    public void Yapilandirilmamis_durum_HER_ZAMAN_kisitsizdir()
    {
        // Diskteki eski "invalid" kararı bile lisanslama kapalıyken hiçbir şey ifade etmemeli;
        // aksi hâlde ayarı kapatmak paneli kurtarmazdı.
        var kayit = new LicenseRecord
        {
            IsConfigured = false,
            ProtectedKey = "x",
            LastVerdict = LicenseVerdicts.Invalid,
            ExpiresAtUtc = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };

        var karar = LicenseDecision.Evaluate(kayit, 14, 12, DateTime.UtcNow);
        Assert.Equal(LicenseState.NotConfigured, karar.State);
        Assert.False(karar.RestrictsNewResources);
    }

    [Fact]
    public void Durum_JSON_uzerinde_state_alani_DIZGI_olarak_doner()
    {
        // Ölçüldü: panelde genel bir JsonStringEnumConverter kaydı YOK, yani bu enum varsayılan
        // olarak SAYI (0..6) yazılırdı ve arayüz "state === 3" gibi anlamsız bir karşılaştırma
        // yapmak zorunda kalırdı. Dönüştürücü tipin üzerine kondu; bu test onun kalmasını sağlar.
        var json = System.Text.Json.JsonSerializer.Serialize(
            new Core.Dtos.Licensing.LicenseStatusDto { State = LicenseState.GraceExpired },
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Contains("\"state\":\"GraceExpired\"", json, StringComparison.Ordinal);
    }

    // --- YAPISAL GÜVENCE: lisans mekanizması ÜRETİMDEKİ kaynakları durduramaz ---

    [Fact]
    public void Lisans_kodu_site_durdurma_veya_askiya_alma_YAPMAZ()
    {
        // Bu, ürün kararının koda gömülmüş hâlidir: lisans mekanizması bir fidye aracı değildir.
        // Biri gelecekte "geçersiz lisansta siteleri durdur" eklemek isterse burada yakalanır.
        var kok = DepoKoku();
        var lisansKlasoru = Path.Combine(kok, "src", "AdaPanel.Infrastructure", "Licensing");
        var dosyalar = Directory.GetFiles(lisansKlasoru, "*.cs", SearchOption.AllDirectories);

        // Tarama bozulursa sessizce geçmesin.
        Assert.True(dosyalar.Length >= 6, $"Lisans kaynak taraması bozuk: {dosyalar.Length} dosya bulundu ({lisansKlasoru}).");

        string[] yasakli =
        [
            "IIisSiteService",
            "StopSiteAsync",
            "ISubscriptionEnforcementService",
            "SuspendAsync",
            "DeleteSiteAsync",
            "IDatabaseService",
            "IMailService"
        ];

        var ihlaller = new List<string>();
        foreach (var dosya in dosyalar)
        {
            var icerik = File.ReadAllText(dosya);
            foreach (var terim in yasakli)
            {
                if (icerik.Contains(terim, StringComparison.Ordinal))
                {
                    ihlaller.Add($"{Path.GetFileName(dosya)} -> {terim}");
                }
            }
        }

        Assert.True(
            ihlaller.Count == 0,
            "Lisans kodu üretimdeki kaynaklara dokunuyor; bu bir barındırma paneli, fidye aracı değil:\n  "
                + string.Join("\n  ", ihlaller));
    }

    [Fact]
    public async Task Deactivation_waits_for_pending_activation_and_cannot_be_undone_by_its_reply()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new SahteLisansSunucusu { BeforeReply = async token =>
        { entered.TrySetResult(); await release.Task.WaitAsync(token); } };
        var first = Servis(server);
        var second = Servis(server); // Different DI scope, same persisted license.
        var activate = first.ActivateAsync("ADA-TEST-0001");
        Task<LicenseStatusDto>? deactivate = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            deactivate = second.DeactivateAsync();
            Assert.False(deactivate.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await activate.WaitAsync(TimeSpan.FromSeconds(5));
            if (deactivate is not null) await deactivate.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.False((await second.GetStatusAsync()).HasKey);
    }

    [Fact]
    public async Task Canceled_waiting_license_operation_does_not_modify_the_active_key()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new SahteLisansSunucusu { BeforeReply = async token =>
        { entered.TrySetResult(); await release.Task.WaitAsync(token); } };
        var service = Servis(server);
        var activate = service.ActivateAsync("ADA-TEST-0001");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancellation = new CancellationTokenSource();
            var deactivate = service.DeactivateAsync(cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => deactivate);
        }
        finally { release.TrySetResult(); await activate.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.True((await service.GetStatusAsync()).HasKey);
        Assert.False((await service.DeactivateAsync()).HasKey);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("invalid")]
    [InlineData("expired")]
    public async Task New_offline_key_does_not_inherit_previous_keys_verdict_or_entitlements(string previousVerdict)
    {
        var now = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var clock = new SabitSaat(now);
        var server = new SahteLisansSunucusu { EnFazlaAbonelik = 2, BitisUtc = now.AddDays(1) };
        var service = Servis(server, saat: clock);
        await service.ActivateAsync("FIXTURE-OLD-0001");
        server.Karar = previousVerdict;
        await service.RefreshAsync();
        clock.Ilerlet(TimeSpan.FromDays(3));
        server.Ulasilabilir = false;
        var status = await service.ActivateAsync("FIXTURE-NEW-0002");
        Assert.Equal(LicenseState.Grace, status.State);
        Assert.True(status.HasKey);
        Assert.Equal("0002", status.KeySuffix);
        Assert.False(status.RestrictsNewResources);
        Assert.Null(status.LastVerifiedAtUtc);
        Assert.Null(status.ExpiresAtUtc);
        Assert.Equal(string.Empty, status.Sku);
        Assert.Equal(0, status.MaxSubscriptions);
        Assert.Equal(now.AddDays(17), status.OfflineGraceEndsAtUtc);
        var reloaded = await Servis(server, saat: clock).GetStatusAsync();
        Assert.Equal("0002", reloaded.KeySuffix);
        Assert.Equal(LicenseState.Grace, reloaded.State);
    }

    [Fact]
    public async Task First_offline_key_is_saved_with_its_own_grace_start()
    {
        var now = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var server = new SahteLisansSunucusu { Ulasilabilir = false };
        var service = Servis(server, saat: new SabitSaat(now));
        var status = await service.ActivateAsync("FIXTURE-FIRST-0001");
        Assert.True(status.HasKey);
        Assert.Equal(LicenseState.Grace, status.State);
        Assert.Equal(now.AddDays(14), status.OfflineGraceEndsAtUtc);
        Assert.Null(status.LastVerifiedAtUtc);
    }

    [Theory]
    [InlineData("valid", LicenseState.GraceExpired)]
    [InlineData("invalid", LicenseState.Invalid)]
    [InlineData("expired", LicenseState.Expired)]
    public async Task Reentering_same_offline_key_preserves_its_proof_and_does_not_restart_grace(string verdict, LicenseState expected)
    {
        var now = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var clock = new SabitSaat(now);
        var server = new SahteLisansSunucusu { EnFazlaAbonelik = 2, BitisUtc = now.AddDays(90) };
        var service = Servis(server, saat: clock);
        await service.ActivateAsync("FIXTURE-SAME-0001");
        server.Karar = verdict;
        await service.RefreshAsync();
        clock.Ilerlet(TimeSpan.FromDays(15));
        server.Ulasilabilir = false;
        var status = await service.ActivateAsync("  FIXTURE-SAME-0001  ");
        Assert.Equal(expected, status.State);
        Assert.True(status.HasKey);
        Assert.True(status.RestrictsNewResources);
        Assert.Equal(now, status.LastVerifiedAtUtc);
        Assert.Equal(now.AddDays(90), status.ExpiresAtUtc);
        Assert.Equal("pro", status.Sku);
        Assert.Equal(2, status.MaxSubscriptions);
        var store = new LicenseStateStore(new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = dataRoot })), new TersinirKoruyucu());
        Assert.Equal(now, (await store.ReadAsync(default)).KeyStoredAtUtc);
    }

    // --- düzenek ---

    private LicenseService Servis(
        SahteLisansSunucusu sunucu,
        string verificationUrl = "https://lisans.ornek.test/verify",
        int offlineGraceDays = 14,
        SabitSaat? saat = null)
    {
        var runtime = Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = dataRoot });
        var fileStore = new JsonFileStore(runtime);
        var lisansSecenekleri = Microsoft.Extensions.Options.Options.Create(new LicenseOptions
        {
            VerificationUrl = verificationUrl,
            ProductCode = "adapanel",
            OfflineGraceDays = offlineGraceDays,
            VerifyIntervalHours = 12
        });

        return new LicenseService(
            lisansSecenekleri,
            new LicenseStateStore(fileStore, new TersinirKoruyucu()),
            new SabitParmakIzi(),
            sunucu,
            saat ?? new SabitSaat(DateTime.UtcNow));
    }

    private static string DepoKoku()
    {
        var klasor = AppContext.BaseDirectory;
        for (var derinlik = 0; derinlik < 8; derinlik++)
        {
            if (File.Exists(Path.Combine(klasor, "AdaPanel.sln")))
            {
                return Path.GetFullPath(klasor);
            }

            klasor = Path.Combine(klasor, "..");
        }

        throw new DirectoryNotFoundException("Depo koku bulunamadi.");
    }

    /// <summary>Ayarlanabilir lisans sunucusu; kaç kez çağrıldığını da SAYAR (gizlilik testi için).</summary>
    private sealed class SahteLisansSunucusu : ILicenseVerificationClient
    {
        public bool Ulasilabilir { get; set; } = true;
        public string Karar { get; set; } = LicenseVerdicts.Valid;
        public int EnFazlaAbonelik { get; set; }
        public DateTime? BitisUtc { get; set; }
        public int CagriSayisi { get; private set; }
        public Func<CancellationToken, Task>? BeforeReply { get; init; }

        public async Task<LicenseVerificationOutcome> VerifyAsync(
            LicenseVerificationRequest request,
            CancellationToken cancellationToken)
        {
            CagriSayisi++;
            if (BeforeReply is not null) await BeforeReply(cancellationToken);

            if (!Ulasilabilir)
            {
                return new LicenseVerificationOutcome(null, "timeout");
            }

            return new LicenseVerificationOutcome(
                new LicenseVerificationResponse
                {
                    Status = Karar,
                    Sku = "pro",
                    MaxSubscriptions = EnFazlaAbonelik,
                    ExpiresAtUtc = BitisUtc
                },
                string.Empty);
        }
    }

    /// <summary>Testte gerçek DPAPI kullanılamaz; tersinir ama DÜZ OLMAYAN bir dönüşüm yeter.</summary>
    private sealed class TersinirKoruyucu : ISecretProtector
    {
        public string Protect(string plaintext) =>
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext));

        public string Unprotect(string protectedValue) =>
            System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue));
    }

    private sealed class SabitParmakIzi : IMachineFingerprintProvider
    {
        public string Get() => new('a', 64);

        public string GetSource() => "test";
    }

    /// <summary>İleri sarılabilen saat: çevrimdışı toleransı gerçek zamanda beklemeden ölçmek için.</summary>
    private sealed class SabitSaat(DateTime baslangicUtc) : TimeProvider
    {
        private DateTimeOffset simdi = new(baslangicUtc, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => simdi;

        public void Ilerlet(TimeSpan sure) => simdi = simdi.Add(sure);
    }
}
