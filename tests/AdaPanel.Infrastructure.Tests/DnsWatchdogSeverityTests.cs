using System.Text.RegularExpressions;
using AdaPanel.Core.Dtos.Sites;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// BAŞARISIZ OTOMATİK ONARIM, BAŞARILI GİBİ OKUNAMAZ.
/// <para>
/// ÖLÇÜLEN HATA: <c>DnsServerHealthDto.LastAutoRepairStatus</c> yalnız METİN taşıyordu ve
/// Sunucu Sağlığı ekranı o satırı NÖTR renkte yazıyordu. "DNS yeniden başlatıldı ama hâlâ
/// genel adreste dinlemiyor" ile "onarım tuttu" ekranda birebir aynı gri satırdı — yani
/// panelin ölçtüğü başarısızlık, kullanıcıya başarı gibi görünüyordu.
/// </para>
/// <para>
/// YANLIŞ ÇÖZÜM (bilerek REDDEDİLDİ): ön yüzün metnin başına bakıp ("failed ile mi başlıyor",
/// "anahtar şu mu") renk seçmesi. Metni de anahtarı da sunucu üretir; ikisi de değişebilir ve
/// değiştiği gün renk SESSİZCE yanlışa döner. Durumu ölçen taraf sunucudur, söyleyen de o
/// olmalı: ayrı bir alan.
/// </para>
/// <para>
/// Testler bir DEĞERİ değil, SINIFI kapatır: durum yazan her nokta, şiddetini de yazmak
/// zorunda. Yeni bir nöbetçi dalı eklenip şiddet unutulursa burada kızarır.
/// </para>
/// </summary>
public sealed class DnsWatchdogSeverityTests
{
    [Fact]
    public void Hic_onarim_yapilmadiysa_durum_ne_basarili_ne_basarisizdir()
    {
        // Uc degerli alan KASITLI: `false` varsayilani, nobetci hic calismamis bir sunucuda
        // "son onarim basarisiz" diye kirmizi yazardi -- olculmemis bir sey uydurmak olurdu.
        var dto = new DnsServerHealthDto();

        Assert.Null(dto.LastAutoRepairSucceeded);
    }

    [Fact]
    public void Durum_metnini_yazan_her_nobetci_dali_siddetini_de_yazar()
    {
        var kaynak = DnsServiceKaynagi();

        var metinYazan = Regex.Matches(kaynak, @"watchdog\.LastStatus\s*=").Count;
        var siddetYazan = Regex.Matches(kaynak, @"watchdog\.LastRepairOk\s*=").Count;

        Assert.True(metinYazan > 0, "Nobetci durum yazan hicbir nokta bulunamadi; tarama bozulmus olabilir.");
        Assert.True(
            siddetYazan >= metinYazan,
            $"Nobetci {metinYazan} yerde durum METNI yaziyor ama yalnizca {siddetYazan} yerde SIDDET yaziyor. " +
            "Siddeti yazilmayan dal ekranda notr renkte cikar: basarisiz onarim basarili gibi okunur.");
    }

    [Fact]
    public void Saglik_yaniti_durum_metnini_verdigi_her_yerde_siddeti_de_verir()
    {
        var kaynak = DnsServiceKaynagi();

        // DTO'nun kurulduğu HER yer sayılır: "kurulu değil" dalı da, ölçüm tutan dal da.
        // Birinde alan unutulursa arayüz o durumda yine renksiz kalırdı.
        var metinVeren = Regex.Matches(kaynak, @"LastAutoRepairStatus\s*=").Count;
        var siddetVeren = Regex.Matches(kaynak, @"LastAutoRepairSucceeded\s*=").Count;

        Assert.True(metinVeren > 0, "Saglik yaniti kurulan hicbir nokta bulunamadi; tarama bozulmus olabilir.");
        Assert.Equal(metinVeren, siddetVeren);
    }

    [Fact]
    public void Basarisiz_onarim_dali_siddeti_ACIKCA_false_yazar()
    {
        var kaynak = DnsServiceKaynagi();

        // `catch` dalı: onarım hiç çalıştırılamadı. Burada "belki" yoktur.
        var dal = kaynak.IndexOf("dns.watchdog.repair_failed", StringComparison.Ordinal);
        Assert.True(dal >= 0, "Onarim hatasi dali kaynakta bulunamadi.");

        var pencere = kaynak[dal..Math.Min(kaynak.Length, dal + 400)];
        Assert.Contains("watchdog.LastRepairOk = false", pencere);
    }

    private static string DnsServiceKaynagi() => File.ReadAllText(Path.Combine(
        BulKok(), "src", "AdaPanel.Infrastructure", "Dns", "DnsService.cs"));

    private static string BulKok()
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
}
