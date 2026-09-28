using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Ssl;
using AdaPanel.Infrastructure.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// YENİLEME TURU bağ ölçümünü GERÇEKTEN çağırıyor mu?
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SslBindingDrift"/> kararı ve Danışman'ın onu göstermesi ayrıca ölçülüyor
/// (<c>SslBaglamaSapmasiTests</c>). Burada ölçülen şey ayrı ve bu depoda defalarca yakalanmış bir
/// hata sınıfıdır: <b>koruma yazılır, kardeş yol onu çağırmaz.</b> Bu testler olmadan biri
/// <c>RenewAllAsync</c> içinden ölçüm çağrısını çıkarsa TEK BİR TEST BİLE kırmızı olmazdı — ve
/// panel yine sessizce eski sertifikayı sunmaya dönerdi.
/// </para>
/// <para>
/// win-acme kurulu OLMAYAN dal bilerek kullanılıyor: bağ ölçümü win-acme'ye bağlı değildir, IIS
/// bağı her hâlükârda okunabilir. Yani bu dal hem gerçek bir üretim durumu hem de en ucuz ölçüm
/// noktasıdır.
/// </para>
/// </remarks>
public sealed class SslYenilemeBagOlcumuTests
{
    private const string Host = "adapanel.host";
    private const string Bizimki = "01d7c463b8f8fcc9ec5b530947f18fedc1c2f82b";
    private const string Eskisi = "e549df061c4deeae143da2b18ed0b64004d7cfa3";

    /// <summary>
    /// Bağ ESKİ sertifikada kalmışsa yenileme turu bunu ÖLÇER ve kayda YAZAR.
    /// </summary>
    /// <remarks>
    /// Canlı arızanın birebir kurgusu: kayıt yenilenmiş sertifikayı (<c>Bizimki</c>) gösteriyor,
    /// IIS bağı ise eskisini (<c>Eskisi</c>) taşıyor.
    /// </remarks>
    [Fact]
    public async Task Bag_eski_sertifikada_kalmissa_olculur_ve_kayda_yazilir()
    {
        using var alan = new YenilemeAlani();
        await alan.KayitYazAsync(Bizimki, bagliMi: null, damga: null);

        var sonuc = await alan.Servis(Bag(Eskisi)).RenewAllAsync();

        Assert.True(sonuc.BindingsMeasured);
        Assert.Equal(1, sonuc.BindingDriftCount);

        var kayit = await alan.KaydiOkuAsync();
        Assert.False(kayit.IsCertificateBound);
        Assert.NotNull(kayit.BindingCheckedAtUtc);
        Assert.Contains(Eskisi, kayit.BindingDetail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// FAZLA KAPATMA NÖBETÇİSİ: bağ DOĞRU sertifikadaysa sapma bildirilmez.
    /// </summary>
    /// <remarks>
    /// Bu test olmadan "her turda sapma say" çözümü yeşil görünür ve panel her yenilemeden sonra
    /// kritik uyarı üretirdi — kapatılan yalancı alarmın aynısı.
    /// </remarks>
    [Fact]
    public async Task Bag_dogru_sertifikadaysa_sapma_bildirilmez()
    {
        using var alan = new YenilemeAlani();
        await alan.KayitYazAsync(Bizimki, bagliMi: null, damga: null);

        var sonuc = await alan.Servis(Bag(Bizimki)).RenewAllAsync();

        Assert.True(sonuc.BindingsMeasured);
        Assert.Equal(0, sonuc.BindingDriftCount);

        var kayit = await alan.KaydiOkuAsync();
        Assert.True(kayit.IsCertificateBound);
        Assert.NotNull(kayit.BindingCheckedAtUtc);
    }

    /// <summary>
    /// BAĞ LİSTESİ OKUNAMAZSA kayda DOKUNULMAZ — önceki GERÇEK ölçüm silinmez.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Geçici bir IIS/PowerShell arızası, daha önce ölçülmüş bir sapmayı sıfırlarsa panel tam da
    /// arızanın sürdüğü sırada susar. Aynı disiplin <c>SslStateReconciler</c> sözleşmesinin
    /// 1. maddesinde yazılı: ölçüm yapılamadıysa kayda dokunulmaz.
    /// </para>
    /// <para>
    /// İki başarısızlık biçimi de sınanıyor: okuyucu <c>null</c> döner (envanter yok) ve okuyucu
    /// fırlatır (PowerShell düştü). İkisi de aynı sonucu vermeli.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bag_okunamazsa_onceki_olcum_silinmez(bool firlatsin)
    {
        using var alan = new YenilemeAlani();
        var oncekiDamga = DateTime.UtcNow.AddHours(-3);
        await alan.KayitYazAsync(Bizimki, bagliMi: false, damga: oncekiDamga);

        Func<CancellationToken, Task<IReadOnlyCollection<IisHttpsBinding>?>> okuyucu = firlatsin
            ? _ => throw new InvalidOperationException("PowerShell dustu")
            : _ => Task.FromResult<IReadOnlyCollection<IisHttpsBinding>?>(null);

        var sonuc = await alan.Servis(okuyucu).RenewAllAsync();

        // ÖLÇÜLEMEDİ: sayı 0 ama bu "bağlar doğru" DEMEK DEĞİL — bayrak bu ayrımı taşır.
        Assert.False(sonuc.BindingsMeasured);
        Assert.Equal(0, sonuc.BindingDriftCount);

        var kayit = await alan.KaydiOkuAsync();
        Assert.False(kayit.IsCertificateBound);
        Assert.Equal(oncekiDamga, kayit.BindingCheckedAtUtc!.Value, TimeSpan.FromSeconds(1));
    }

    private static Func<CancellationToken, Task<IReadOnlyCollection<IisHttpsBinding>?>> Bag(string parmakIzi)
        => _ => Task.FromResult<IReadOnlyCollection<IisHttpsBinding>?>(new[]
        {
            new IisHttpsBinding
            {
                SiteName = Host,
                BindingInformation = $"*:443:{Host}",
                Thumbprint = parmakIzi,
                CertificateReadable = true,
                StoreName = "WebHosting"
            }
        });

    private sealed class YenilemeAlani : IDisposable
    {
        private readonly SqliteConnection _baglanti;

        public YenilemeAlani()
        {
            Kok = Path.Combine(Path.GetTempPath(), $"adapanel-ssl-yenileme-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Kok);
            DosyaDeposu = new JsonFileStore(
                Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Kok }));

            _baglanti = new SqliteConnection("DataSource=:memory:");
            _baglanti.Open();
            Baglam = new AdaPanelDbContext(
                new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(_baglanti).Options);
            Baglam.Database.EnsureCreated();
        }

        public string Kok { get; }

        public JsonFileStore DosyaDeposu { get; }

        public AdaPanelDbContext Baglam { get; }

        public Task KayitYazAsync(string parmakIzi, bool? bagliMi, DateTime? damga)
            => DosyaDeposu.WriteAsync("config/ssl.json", new[]
            {
                new SslCertificateDto
                {
                    HostName = Host,
                    SiteName = Host,
                    Thumbprint = parmakIzi,
                    Port = 443,
                    NotAfterUtc = DateTime.UtcNow.AddDays(54),
                    IsCertificateBound = bagliMi,
                    BindingCheckedAtUtc = damga
                }
            });

        public async Task<SslCertificateDto> KaydiOkuAsync()
        {
            var kayitlar = await DosyaDeposu.ReadAsync("config/ssl.json", Array.Empty<SslCertificateDto>());
            return Assert.Single(kayitlar);
        }

        /// <summary>
        /// win-acme kaydı YOK: <c>RenewAllAsync</c> "kurulu degil" dalına düşer ve bağ ölçümünü
        /// yine çağırmalıdır. Kullanılmayan bağımlılıklar <c>null!</c> — kullanılırsa test patlar,
        /// yani sessizce yeşil kalmaz.
        /// </summary>
        public WinAcmeSslService Servis(Func<CancellationToken, Task<IReadOnlyCollection<IisHttpsBinding>?>> okuyucu)
            => new(Baglam, null!, null!, DosyaDeposu, null!,
                NullLogger<WinAcmeSslService>.Instance, okuyucu);

        // Geçici klasör BİLEREK silinmiyor (depoda silme yasağı); bağlantı kapatılıyor.
        public void Dispose() => _baglanti.Dispose();
    }
}
