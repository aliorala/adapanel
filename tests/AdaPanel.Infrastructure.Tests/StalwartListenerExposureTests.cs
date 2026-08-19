using AdaPanel.Infrastructure.Bootstrap;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// AĞ YÜZEYİ — Stalwart dinleyicilerinin hangisi dışarıya açılır?
/// </summary>
/// <remarks>
/// Denetim bulgusu: kurulum sonrası yapılandırma, 8080 DIŞINDAKİ HER dinleyiciye
/// <c>0.0.0.0:port</c> bind'i ekliyordu. Stalwart'ın varsayılanları arasında ManageSieve (4190) ve
/// düz metin POP3 (110) da vardır; yani temiz bir kurulum bu iki servisi de tüm dünyaya açıyordu.
/// Panel bunların hiçbirini dışarıdan kullanmaz (Roundcube ve panel aynı makinededir).
/// </remarks>
public sealed class StalwartListenerExposureTests
{
    [Theory]
    [InlineData(25)]    // MX / gelen posta
    [InlineData(143)]   // IMAP STARTTLS
    [InlineData(465)]   // SMTPS
    [InlineData(587)]   // Submission STARTTLS
    [InlineData(993)]   // IMAPS
    [InlineData(995)]   // POP3S
    public void Kamuya_acik_posta_portlari_IPv4_e_de_baglanir(int port)
    {
        var sonuc = ComponentBootstrapper.ResolveStalwartListenerBinds([$"[::]:{port}"]);

        Assert.Contains($"0.0.0.0:{port}", sonuc);
        Assert.Contains($"[::]:{port}", sonuc);
    }

    [Theory]
    [InlineData(4190)]  // ManageSieve — panel kullanmıyor, Roundcube eklentisi kaldırılmıştı
    [InlineData(110)]   // düz metin POP3 — Stalwart zaten LOGIN'i reddeder
    [InlineData(8080)]  // yönetim/JMAP — panel localhost üzerinden yönetir
    public void Kamuya_acik_OLMAYAN_portlar_loopback_e_kisitlanir(int port)
    {
        var sonuc = ComponentBootstrapper.ResolveStalwartListenerBinds([$"[::]:{port}"]);

        Assert.Equal(new[] { $"127.0.0.1:{port}", $"[::1]:{port}" }.OrderBy(x => x, StringComparer.Ordinal), sonuc);
        Assert.DoesNotContain($"0.0.0.0:{port}", sonuc);
        Assert.DoesNotContain($"[::]:{port}", sonuc);
    }

    /// <summary>
    /// Zaten IPv4'e bağlı, dışarıya açık kalması gereken bir dinleyici bozulmamalı
    /// (fazla kapatma, açık bırakmaktan daha görünür bir arızadır).
    /// </summary>
    [Fact]
    public void Zaten_dogru_bagli_posta_dinleyicisi_degistirilmez()
    {
        var mevcut = new[] { "0.0.0.0:25", "[::]:25" };

        var sonuc = ComponentBootstrapper.ResolveStalwartListenerBinds(mevcut);

        Assert.True(sonuc.SetEquals(mevcut));
    }

    /// <summary>
    /// Portu okunamayan (beklenmedik biçimli) bir dinleyiciye DOKUNULMAZ: tahminle bind yazmak,
    /// çalışan bir posta portunu sessizce kapatmaktan daha kötüdür.
    /// </summary>
    [Fact]
    public void Portu_okunamayan_dinleyiciye_dokunulmaz()
    {
        var mevcut = new[] { "unix:/tmp/soket" };

        var sonuc = ComponentBootstrapper.ResolveStalwartListenerBinds(mevcut);

        Assert.True(sonuc.SetEquals(mevcut));
    }

    /// <summary>
    /// Bir dinleyicinin portlarından biri bile kamuya açık listede değilse tamamı loopback'e iner:
    /// karışık kümede "en az ayrıcalık" tarafını seçeriz.
    /// </summary>
    [Fact]
    public void Karisik_port_kumesi_loopback_e_iner()
    {
        var sonuc = ComponentBootstrapper.ResolveStalwartListenerBinds(["[::]:143", "[::]:4190"]);

        Assert.DoesNotContain("0.0.0.0:143", sonuc);
        Assert.DoesNotContain("0.0.0.0:4190", sonuc);
        Assert.Contains("127.0.0.1:143", sonuc);
        Assert.Contains("127.0.0.1:4190", sonuc);
    }
}
