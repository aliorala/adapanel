using System.Net;
using System.Net.Sockets;
using System.Text;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Cache;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// F1 — "Memcached Başlat hiç çalışamaz, Durdur yalan söyler".
/// <para>
/// Depoda aynı bileşen için İKİ ÇELİŞEN SÖZLEŞME vardı: kurulum (ComponentBootstrapper)
/// memcached'i ONSTART ZAMANLANMIŞ GÖREV olarak kuruyor — çünkü mingw memcached.exe Windows Servis
/// Denetim Protokolü'nü uygulamaz — ama <c>CacheService</c> durumu <c>Get-Service</c> ile
/// okuyordu. O adda bir servis hiç var olmadığı için panel, memcached ÇALIŞIRKEN bile HER ZAMAN
/// "Durduruldu" gösteriyordu.
/// </para>
/// <para>
/// Bu test gerçeği ÖLÇER: memcached protokolünü konuşan canlı bir dinleyici varken panel
/// "Running" demeli. MUTASYON: durum okuması <c>Get-Service</c>'e döndürülürse test düşer.
/// </para>
/// </summary>
public sealed class MemcachedRuntimeTruthTests : IDisposable
{
    private readonly SqliteConnection baglanti;
    private readonly AdaPanelDbContext dbContext;

    public MemcachedRuntimeTruthTests()
    {
        baglanti = new SqliteConnection("Filename=:memory:");
        baglanti.Open();
        dbContext = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(baglanti).Options);
        dbContext.Database.EnsureCreated();
    }

    /// <summary>
    /// URETIM VARSAYILANI 11211 KALMALI.
    /// </summary>
    /// <remarks>
    /// Testler artik bos port kullaniyor (asagidaki BosPortBul). Bu, gercek portu SERBEST birakti:
    /// biri varsayilani degistirse hicbir test kirmizi yanmazdi ve panel memcached'i yanlis porttan
    /// yoklamaya baslardi -- calisan bir bilesen "Durduruldu" gorunurdu. Kancanin kendisi bir
    /// kor nokta acmasin diye varsayilan ayrica sabitlenir.
    /// </remarks>
    [Fact]
    public void Uretim_sonda_portu_varsayilani_11211()
    {
        Assert.Equal(11211, CacheService.MemcachedDefaultPort);
        Assert.Equal(11211, new CacheService(dbContext).MemcachedProbePort);
    }

    /// <summary>
    /// Isletim sisteminden BOS bir port ister ve numarasini dondurur.
    /// </summary>
    /// <remarks>
    /// GERCEK 11211 KULLANILMIYOR, ve bu bilincli: testler once o portu paylasiyordu ve sonuc KODA
    /// DEGIL ORTAMA baglıydi -- makinede gercek bir memcached varsa ya da kardes testin soketi
    /// henuz kapanmadiysa "kimse cevap vermiyor" testi Running gorup dusuyordu. Tam kosumda
    /// kirmizi, tek basina yesil. Rastgele kirmizi yanan bir nobetci bir sure sonra "yine o test"
    /// diye gormezden gelinir; guvenilmez koruma, olmayandan beterdir. Port 0 ile baglanmak,
    /// isletim sisteminin o an BOS oldugunu garanti ettigi bir port verir.
    /// </remarks>
    private static int BosPortBul()
    {
        var dinleyici = new TcpListener(IPAddress.Loopback, 0);
        dinleyici.Start();
        var port = ((IPEndPoint)dinleyici.LocalEndpoint).Port;
        dinleyici.Stop();
        return port;
    }

    [Fact]
    public async Task GetStatus_WhenMemcachedAnswersOnItsPort_ReportsRunning()
    {
        BilesenKaydet("memcached", "AdaMemcached");
        var port = BosPortBul();
        using var sahte = new SahteMemcached(port);
        Assert.True(sahte.Dinliyor, $"sahte memcached {port} portuna baglanamadi.");

        var servis = new CacheService(dbContext) { MemcachedProbePort = port };
        var durum = await servis.GetStatusAsync();

        Assert.True(durum.Memcached.IsInstalled);
        Assert.Equal("Running", durum.Memcached.Status);
    }

    /// <summary>
    /// Port susuyorsa durum "Stopped" olmalı — ama bu, servis kaydına değil ÖLÇÜME dayanmalı.
    /// </summary>
    [Fact]
    public async Task GetStatus_WhenNothingAnswers_ReportsStopped()
    {
        BilesenKaydet("memcached", "AdaMemcached");

        // Bos port: kimsenin dinlemedigi KESIN olan bir numara. Gercek 11211'e bakmak, makinede
        // memcached kurulu oldugunda bu testi hatali kirmiziya dusururdu.
        var servis = new CacheService(dbContext) { MemcachedProbePort = BosPortBul() };
        var durum = await servis.GetStatusAsync();

        Assert.True(durum.Memcached.IsInstalled);
        Assert.Equal("Stopped", durum.Memcached.Status);
    }

    /// <summary>Bileşen hiç kurulu değilse "NotInstalled"; port sondası bunu değiştirmemeli.</summary>
    [Fact]
    public async Task GetStatus_WhenComponentIsMissing_ReportsNotInstalled()
    {
        var servis = new CacheService(dbContext);
        var durum = await servis.GetStatusAsync();

        Assert.False(durum.Memcached.IsInstalled);
        Assert.Equal("NotInstalled", durum.Memcached.Status);
    }

    /// <summary>
    /// GÖREV ADI VARSAYILMAZ, OKUNUR. Kurulum manifestteki serviceName'i InstalledComponent'e
    /// yazar; panel bu adı kullanmalı ki bootstrapper adı değiştirdiğinde iki taraf ayrışmasın.
    /// </summary>
    [Fact]
    public async Task GetStatus_UsesTheUnitNameRecordedByTheInstaller()
    {
        BilesenKaydet("memcached", "OzelMemcachedGorevi");

        var servis = new CacheService(dbContext);
        var durum = await servis.GetStatusAsync();

        Assert.Equal("OzelMemcachedGorevi", durum.Memcached.ServiceName);
    }

    private void BilesenKaydet(string bilesenId, string birimAdi)
    {
        dbContext.InstalledComponents.Add(new InstalledComponent
        {
            ComponentId = bilesenId,
            DisplayName = bilesenId,
            Version = "1.6.8",
            Type = ComponentType.Zip,
            InstallDir = Path.Combine(Path.GetTempPath(), "ada-test-memcached"),
            ServiceName = birimAdi,
            Status = ComponentInstallStatus.Installed
        });
        dbContext.SaveChanges();
    }

    public void Dispose()
    {
        dbContext.Dispose();
        baglanti.Dispose();
    }

    /// <summary>
    /// memcached metin protokolünün tek komutluk taklidi: <c>version</c> → <c>VERSION 1.6.8</c>.
    /// Kurulum doğrulaması (ComponentBootstrapper) da tam olarak bu sondayı kullanıyor.
    /// </summary>
    private sealed class SahteMemcached : IDisposable
    {
        private readonly TcpListener? dinleyici;
        private readonly CancellationTokenSource iptal = new();

        public SahteMemcached(int port)
        {
            try
            {
                dinleyici = new TcpListener(IPAddress.Loopback, port);
                dinleyici.Start();
                Dinliyor = true;
                _ = Task.Run(DonguAsync);
            }
            catch (SocketException)
            {
                dinleyici = null;
                Dinliyor = false;
            }
        }

        public bool Dinliyor { get; }

        private async Task DonguAsync()
        {
            while (!iptal.IsCancellationRequested)
            {
                try
                {
                    using var istemci = await dinleyici!.AcceptTcpClientAsync(iptal.Token);
                    await using var akis = istemci.GetStream();

                    var tampon = new byte[64];
                    _ = await akis.ReadAsync(tampon, iptal.Token);
                    await akis.WriteAsync(Encoding.ASCII.GetBytes("VERSION 1.6.8\r\n"), iptal.Token);
                    await akis.FlushAsync(iptal.Token);
                }
                catch
                {
                    return;
                }
            }
        }

        public void Dispose()
        {
            iptal.Cancel();
            try { dinleyici?.Stop(); } catch { /* en iyi caba */ }
            iptal.Dispose();
        }
    }
}
