using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// GERÇEK <see cref="AuthService"/> ile giriş akışını çalıştıran ortak düzenek.
/// </summary>
/// <remarks>
/// <para>
/// Daha önce her giriş testi kendi düzeneğini kuruyordu (~80 satır bağımlılık listesi). Üçüncü
/// kopya yazılacakken tek yere alındı: giriş akışına yeni bir bağımlılık eklendiğinde tek bir yer
/// güncellenir, yoksa kopyalardan biri derlenmeyi bırakır ya da — daha kötüsü — farklı bir sahteyle
/// BAŞKA bir şey ölçmeye başlar.
/// </para>
/// <para>
/// <b>"Throwing" sahteler korundu:</b> iki adımlı doğrulama KAPALIYKEN TOTP servisi ve sır koruyucu
/// çağrılmamalıdır; çağrılırsa test sessizce geçmek yerine patlar. Gerçek servisler yalnız
/// <c>ikiAdimli: true</c> istendiğinde bağlanır.
/// </para>
/// </remarks>
internal sealed class GirisDuzenegi : IAsyncDisposable
{
    public const string DogruParola = "Str0ng!Passw0rd";

    private readonly SqliteConnection baglanti;

    private GirisDuzenegi(
        SqliteConnection baglanti,
        AdaPanelDbContext dbContext,
        AuthService servis,
        RecordingAuditService denetim,
        CountingBruteForceProtection kabaKuvvet,
        SayanParolaKarmalayici karmalayici,
        JsonFileStore dosyaDeposu,
        SessizAnahtarServisi anahtarServisi,
        SayanIsKuyrugu kuyruk,
        PanelUser kullanici,
        string? totpSirri,
        string veriKoku)
    {
        Kuyruk = kuyruk;
        AnahtarServisi = anahtarServisi;
        IlkKarma = kullanici.PasswordHash;
        this.baglanti = baglanti;
        DbContext = dbContext;
        Servis = servis;
        Denetim = denetim;
        KabaKuvvet = kabaKuvvet;
        Karmalayici = karmalayici;
        DosyaDeposu = dosyaDeposu;
        Kullanici = kullanici;
        TotpSirri = totpSirri;
        VeriKoku = veriKoku;
    }

    public AdaPanelDbContext DbContext { get; }

    public AuthService Servis { get; }

    public RecordingAuditService Denetim { get; }

    public CountingBruteForceProtection KabaKuvvet { get; }

    /// <summary>Parola karması KAÇ KEZ doğrulandı — zaman farkı ölçümünün dayanağı.</summary>
    public SayanParolaKarmalayici Karmalayici { get; }

    public JsonFileStore DosyaDeposu { get; }

    public PanelUser Kullanici { get; }

    /// <summary>Anahtar iptalinin çağrılıp çağrılmadığını sayan sahte.</summary>
    public SessizAnahtarServisi AnahtarServisi { get; }

    /// <summary>
    /// Arka plan iş kuyruğunun sahtesi. İşi ÇALIŞTIRMAZ, yalnız kaydeder — "yanıt yolu işi kendisi
    /// yapıyor mu" sorusunun deterministik cevabı budur.
    /// </summary>
    public SayanIsKuyrugu Kuyruk { get; }

    /// <summary>
    /// Düzenek kurulurkenki parola karması. <see cref="Kullanici"/> İZLENEN bir varlıktır ve
    /// serviste değiştiğinde onun üzerinde de değişir; "değişti mi" sorusu ancak bu kopyayla
    /// sorulabilir.
    /// </summary>
    public string IlkKarma { get; }

    /// <summary>İki adımlı doğrulama açıksa TOTP gizli anahtarı; kapalıysa <c>null</c>.</summary>
    public string? TotpSirri { get; }

    public string VeriKoku { get; }

    public static async Task<GirisDuzenegi> OlusturAsync(
        int kilitDakika = 0,
        bool ikiAdimli = false,
        bool aktif = true,
        string kullaniciAdi = "admin")
    {
        var baglanti = new SqliteConnection("Data Source=:memory:");
        await baglanti.OpenAsync();
        var secenekler = new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(baglanti).Options;
        var dbContext = new AdaPanelDbContext(secenekler);
        await dbContext.Database.EnsureCreatedAsync();

        var karmalayici = new SayanParolaKarmalayici(new PasswordHasher<PanelUser>());
        var totpServisi = new TotpService();
        var sir = ikiAdimli ? totpServisi.GenerateSecret() : null;

        var kullanici = new PanelUser
        {
            Id = Guid.NewGuid(),
            UserName = kullaniciAdi,
            Email = $"{kullaniciAdi}@ornek.test",
            Role = UserRole.Administrator,
            PreferredLanguage = "tr",
            IsActive = aktif,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            IsTwoFactorEnabled = ikiAdimli,
            TwoFactorSecretProtected = sir,
            LockedUntilUtc = kilitDakika == 0 ? null : DateTime.UtcNow.AddMinutes(kilitDakika)
        };

        kullanici.PasswordHash = karmalayici.HashPassword(kullanici, DogruParola);
        dbContext.Users.Add(kullanici);
        await dbContext.SaveChangesAsync();

        var veriKoku = Path.Combine(Path.GetTempPath(), $"adapanel-giris-{Guid.NewGuid():N}");
        Directory.CreateDirectory(veriKoku);
        var calismaSecenekleri = Microsoft.Extensions.Options.Options.Create(
            new PanelRuntimeOptions { DataRoot = veriKoku, LoginLockoutMinutes = 15 });
        var dosyaDeposu = new JsonFileStore(calismaSecenekleri);

        var denetim = new RecordingAuditService();
        var kabaKuvvet = new CountingBruteForceProtection();
        var anahtarServisi = new SessizAnahtarServisi();
        var kuyruk = new SayanIsKuyrugu();
        var servis = new AuthService(
            dbContext,
            karmalayici,
            ikiAdimli ? new GecirgenSirKoruyucu() : new ThrowingSecretProtector(),
            ikiAdimli ? totpServisi : new ThrowingTotpService(),
            denetim,
            kabaKuvvet,
            new ThrowingPreferencesService(),
            kuyruk,
            new ThrowingCustomerScopeService(),
            anahtarServisi,
            dosyaDeposu,
            calismaSecenekleri,
            new SahipsizSunucuSahibiServisi(),
            NullLogger<AuthService>.Instance);

        return new GirisDuzenegi(
            baglanti, dbContext, servis, denetim, kabaKuvvet, karmalayici, dosyaDeposu,
            anahtarServisi, kuyruk, kullanici, sir, veriKoku);
    }

    public async Task<PanelUser> TazeleAsync()
    {
        DbContext.ChangeTracker.Clear();
        return await DbContext.Users.FirstAsync(kullanici => kullanici.Id == Kullanici.Id);
    }

    public async ValueTask DisposeAsync()
    {
        await DbContext.DisposeAsync();
        await baglanti.DisposeAsync();

        // GEÇİCİ KLASÖR SİLİNMİYOR. Bu depoda bir ajanın "kendi artıklarımı temizledim" demesi
        // gerçek verinin yok olmasıyla sonuçlandı; test kodunda da aynı alışkanlığı üretmiyoruz.
        // Klasör %TEMP% altında, birkaç kilobayt ve işletim sistemi tarafından zaten toplanıyor.
    }
}

/// <summary>
/// Parola karmasının KAÇ KEZ doğrulandığını sayan sarmalayıcı.
/// </summary>
/// <remarks>
/// Zaman farkını kronometreyle ölçmek makineye bağlı ve kırılgandır; ölçülmek istenen şey zaten
/// "iki yol AYNI işi yapıyor mu" sorusudur. Sayaç bunu belirleyici (deterministik) biçimde
/// cevaplar: olmayan kullanıcı yolunda doğrulama sayısı 0'a düşerse, zaman farkı geri gelmiş
/// demektir.
/// </remarks>
internal sealed class SayanParolaKarmalayici(IPasswordHasher<PanelUser> ic) : IPasswordHasher<PanelUser>
{
    public int DogrulamaSayisi { get; private set; }

    public string HashPassword(PanelUser user, string password) => ic.HashPassword(user, password);

    public PasswordVerificationResult VerifyHashedPassword(PanelUser user, string hashedPassword, string providedPassword)
    {
        DogrulamaSayisi++;
        return ic.VerifyHashedPassword(user, hashedPassword, providedPassword);
    }
}

/// <summary>Testte şifreleme yok: sır ne verildiyse o döner (DPAPI testte kullanılamaz).</summary>
internal sealed class GecirgenSirKoruyucu : ISecretProtector
{
    public string Protect(string plaintext) => plaintext;

    public string Unprotect(string protectedValue) => protectedValue;
}

/// <summary>
/// Anahtar servisinin SESSİZ sürümü: parola yazan yollar anahtarları düşürmek zorunda ve
/// <see cref="ThrowingApiTokenService"/> orada testi patlatırdı. Yalnız "kaç kez çağrıldı" tutar.
/// </summary>
internal sealed class SessizAnahtarServisi : IApiTokenService
{
    public int IptalCagrisi { get; private set; }

    public Task<IReadOnlyList<Core.Dtos.Ai.ApiTokenDto>> GetForUserAsync(Guid userId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<Core.Dtos.Ai.CreatedApiTokenDto> CreateAsync(Guid userId, Core.Dtos.Ai.CreateApiTokenRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task RevokeAsync(Guid userId, Guid tokenId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<int> RevokeAllForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        IptalCagrisi++;
        return Task.FromResult(0);
    }

    public Task<PanelApiToken?> ValidateAsync(string presentedToken, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
