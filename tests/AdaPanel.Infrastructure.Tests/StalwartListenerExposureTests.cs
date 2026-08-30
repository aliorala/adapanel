using AdaPanel.Infrastructure.Bootstrap;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// AĞ YÜZEYİ — Stalwart dinleyicilerinin hangisi dışarıya açılır?
/// </summary>
/// <remarks>
/// <para>
/// Denetim bulgusu: kurulum sonrası yapılandırma, 8080 DIŞINDAKİ HER dinleyiciye
/// <c>0.0.0.0:port</c> bind'i ekliyordu. Panel bu portların hiçbirini dışarıdan kullanmaz
/// (Roundcube ve panel aynı makinededir).
/// </para>
/// <para>
/// <b>ÖLÇÜLDÜ</b> (Stalwart 0.16.8, panelin yazdığı minimal <c>config.json</c> ile yalıtılmış bir
/// kopya ayağa kaldırılıp <c>query NetworkListener --json</c> ile okundu): varsayılan dinleyici
/// kümesi <c>http 8080</c>, <c>https 443</c>, <c>sieve 4190</c>, <c>pop3s 995</c>,
/// <c>imaps 993</c>, <c>submissions 465</c>, <c>smtp 25</c>'tir. Yani ManageSieve (4190)
/// GERÇEKTEN varsayılandadır ve düzeltme olmadan dünyaya açılırdı; <b>düz metin POP3 (110)
/// varsayılanda YOKTUR</b> — aşağıdaki 110 satırı ölçülmüş bir varsayılan değil, ileride elle
/// eklenirse yakalansın diye konmuş savunma amaçlı bir vakadır.
/// </para>
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
