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

    [Fact]
    public async Task GetStatus_WhenMemcachedAnswersOnItsPort_ReportsRunning()
    {
        BilesenKaydet("memcached", "AdaMemcached");
        using var sahte = new SahteMemcached(11211);
        if (!sahte.Dinliyor)
        {
            // 11211 baska bir sey tarafindan tutuluyorsa bu testin olcecegi sey kalmaz.
            // Sessizce gecmektense durumu ACIKCA yaz: "olcemedik" ile "sorun yok" ayni sey degildir.
            Assert.Fail("11211 portu baska bir surec tarafindan kullaniliyor; test olcum yapamadi.");
        }

        var servis = new CacheService(dbContext);
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

        var servis = new CacheService(dbContext);
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
