using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Dış araç çıktısının BELLEĞE ALINMASI sınırlı olmalı.
/// </summary>
/// <remarks>
/// <para>
/// <b>KAPATILAN AÇIK (ölçüldü):</b> <c>ProcessRunner</c> her iki akışı da <c>ReadToEndAsync</c> ile
/// SINIRSIZ okuyordu; tek sınır zaman aşımıydı (10 dk). Bu, deponun en çok kullanılan ilkelidir ve
/// KİRACIYA ULAŞILABİLİR: <c>FileService.SetPermissionsAsync</c> <c>icacls … /T /C</c>'yi kiracının
/// KENDİ ağacında koşturur ve <c>/C</c> her dosya için bir satır yazar — yani çıktının boyutunu
/// kiracının dosya sayısı belirliyordu. Panel LocalSystem koştuğu için bu belleği hiçbir kota
/// sınırlamıyordu.
/// </para>
/// <para>
/// <b>NE ÖLÇÜLÜYOR:</b> tavanın gerçekten uygulandığı, kesilmenin SÖYLENDİĞİ ve tavanın aracı
/// ASMADIĞI. Sonuncusu kritik: okumayı kesmek işletim sistemi borusunu doldurup dış aracı kendi
/// yazma çağrısında bloke ederdi — "belleği koru" düzeltmesi, her büyük çıktıda 10 dakikalık bir
/// asılmaya dönüşürdü.
/// </para>
/// </remarks>
public sealed class ProcessRunnerOutputLimitTests
{
    /// <summary>Tavanı AŞAN çıktı kesilir ve kesildiği YAZILIR.</summary>
    /// <remarks>
    /// MUTASYON (ölçüldü): tavanlı okuyucu <c>ReadToEndAsync</c>'e geri döndürüldüğünde bu test
    /// KIRMIZI yandı — çıktı tavanın onlarca katına çıktı ve kesilme notu hiç yazılmadı.
    /// </remarks>
    [Fact]
    public async Task Tavani_asan_cikti_KESILIR_ve_kesildigi_YAZILIR()
    {
        // 2.000 satır × ~60 karakter ≈ 120 KB; tavan 4 KB.
        var sonuc = await ProcessRunner.RunAsync(
            "cmd.exe",
            "/c for /L %i in (1,1,2000) do @echo ADAPANEL-SATIR-DOLGU-DOLGU-DOLGU-DOLGU-DOLGU-%i",
            timeout: TimeSpan.FromMinutes(2),
            outputCharLimit: 4096);

        Assert.Contains(ProcessRunner.TruncationNotice, sonuc.StandardOutput, StringComparison.Ordinal);

        // Tavan + kesilme notu kadar; ham çıktının tamamı değil.
        Assert.True(
            sonuc.StandardOutput.Length <= 4096 + ProcessRunner.TruncationNotice.Length + Environment.NewLine.Length,
            $"Cikti tavani asti: {sonuc.StandardOutput.Length} karakter.");

        // Baştaki gerçek çıktı KAYBOLMAZ: teşhis için ilk satırlar en değerli olanlardır.
        Assert.Contains("ADAPANEL-SATIR-DOLGU", sonuc.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>Tavana SIĞAN çıktıya dokunulmaz — ve kesilme notu ASILSIZ yazılmaz.</summary>
    /// <remarks>
    /// Bu, tavanın kendi başına bir yalan üretmediğinin nöbetçisidir: her çıktıya "kesildi" yazan
    /// bir düzeltme, panelin bütün teşhis çıktılarını şüpheli hâle getirirdi.
    /// </remarks>
    [Fact]
    public async Task Tavana_sigan_cikti_AYNEN_doner()
    {
        var sonuc = await ProcessRunner.RunAsync(
            "cmd.exe",
            "/c echo ADAPANEL-KISA-CIKTI",
            timeout: TimeSpan.FromMinutes(2),
            outputCharLimit: 4096);

        Assert.Equal(0, sonuc.ExitCode);
        Assert.Contains("ADAPANEL-KISA-CIKTI", sonuc.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(ProcessRunner.TruncationNotice, sonuc.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// Tavan aşıldıktan sonra da BORU TÜKETİLİR: araç normal biter, zaman aşımına düşmez.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Okumayı tavanda bırakan bir uygulama, işletim sistemi borusunu doldurur ve aracı kendi
    /// <c>write</c> çağrısında SONSUZA KADAR bloke eder; çıkış kodu 0 yerine -1 olur ve stderr'e
    /// "timed out" yazılır. Ölçülen ayrım tam olarak budur.
    /// </para>
    /// <para>
    /// Çıktı BİLEREK boru tamponundan (Windows'ta ~64 KB) büyük seçildi: daha küçük bir çıktı
    /// tampona sığar, okunmasa bile araç biter ve test asılmayı hiç ölçemezdi.
    /// </para>
    /// <para>
    /// <b>DUVAR SAATİ ÖLÇÜLMÜYOR — ve bu, ölçülmüş bir düzeltmedir.</b> Testin ilk hâli "55
    /// saniyeden kısa sürdü" diye bakıyordu ve tam takım altında BİR KEZ kırmızı yandı: makine
    /// doygunken <c>cmd.exe</c>'nin döngüsü yavaşlar, yani test korumayı değil MAKİNENİN YÜKÜNÜ
    /// ölçüyordu. Aralıklı kırmızı yanan bir nöbetçi, gerçek bir arızadan ayırt edilemediği için
    /// hiçbir şey ölçmeyen bir testten daha zararlıdır. Asılma sınırsızdır: hangi zaman aşımı
    /// verilirse verilsin ona takılır, dolayısıyla ayrımı yapmak için saat tutmaya gerek yoktur.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Tavan_asilsa_bile_arac_ASILMAZ()
    {
        var sonuc = await ProcessRunner.RunAsync(
            "cmd.exe",
            "/c for /L %i in (1,1,4000) do @echo ADAPANEL-DOLGU-DOLGU-DOLGU-DOLGU-DOLGU-DOLGU-%i",
            timeout: TimeSpan.FromMinutes(2),
            outputCharLimit: 1024);

        Assert.Equal(0, sonuc.ExitCode);
        Assert.DoesNotContain("timed out", sonuc.StandardError, StringComparison.OrdinalIgnoreCase);

        // Tavan gerçekten uygulanmış olmalı; aksi hâlde bu test "asılmadı" derken korumanın
        // kalktığını fark etmezdi.
        Assert.Contains(ProcessRunner.TruncationNotice, sonuc.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// VARSAYILAN çağrılarda da tavan var: sınır yalnız açıkça isteyen çağıranlara verilseydi
    /// depodaki yüzlerce çağrı korumasız kalırdı.
    /// </summary>
    [Fact]
    public void Varsayilan_tavan_sinirsiz_degildir()
    {
        Assert.True(ProcessRunner.DefaultOutputCharLimit > 0);

        // Değer meşru makine çıktılarını kırmayacak kadar cömert olmalı (bkz. gerekçe).
        Assert.True(ProcessRunner.DefaultOutputCharLimit >= 1024 * 1024);
    }
}
