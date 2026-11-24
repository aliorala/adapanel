using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Infrastructure.Dns;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>BIND bölge dosyası dışa/içe aktarma: ayrıştırma, doğrulama, sınırlar, tur atma.</summary>
public sealed class DnsZoneFileTests
{
    private const string Zone = "ornek.com";

    internal static SiteDnsRecordDto Kayit(string tur, string konak, string deger, int? oncelik = null, int ttl = 3600, bool yonetilen = false, string? id = null) =>
        new() { Id = id ?? Guid.NewGuid().ToString("N"), Type = tur, Host = konak, Value = deger, Priority = oncelik, Ttl = ttl, IsManaged = yonetilen };

    internal static SiteDnsZoneDto Bolge(params SiteDnsRecordDto[] kayitlar) => new()
    {
        SiteName = "ornek", ZoneName = Zone, NameServers = ["ns1.ornek.com", "ns2.ornek.com"], Records = kayitlar
    };

    // --- Dışa aktarma ---

    [Fact]
    public void Disa_aktarim_ORIGIN_TTL_SOA_NS_ve_kayitlari_yazar()
    {
        var metin = DnsZoneFile.Export(Bolge(
            Kayit("A", "@", "203.0.113.5"),
            Kayit("MX", "@", "mail.ornek.com", 10),
            Kayit("CNAME", "www", "ornek.com"))).ZoneText;

        Assert.Contains("$ORIGIN ornek.com.", metin, StringComparison.Ordinal);
        Assert.Contains("$TTL 3600", metin, StringComparison.Ordinal);
        Assert.Contains("SOA\tns1.ornek.com. hostmaster.ornek.com.", metin, StringComparison.Ordinal);
        Assert.Contains("NS\tns2.ornek.com.", metin, StringComparison.Ordinal);
        Assert.Contains("MX\t10 mail.ornek.com.", metin, StringComparison.Ordinal);
        Assert.Contains("CNAME\tornek.com.", metin, StringComparison.Ordinal);
    }

    [Fact]
    public void Uzun_TXT_255_baytlik_parcalara_bolunur_ve_geri_okununca_AYNI_olur()
    {
        var uzun = "v=DKIM1; k=rsa; p=" + new string('A', 400);
        var disa = DnsZoneFile.Export(Bolge(Kayit("TXT", "s1._domainkey", uzun))).ZoneText;

        var satir = disa.Split('\n').Single(l => l.StartsWith("s1._domainkey", StringComparison.Ordinal));
        var parcalar = satir.Split('"').Where((_, i) => i % 2 == 1).ToArray();
        Assert.True(parcalar.Length >= 2, "TXT en az iki tirnakli parcaya bolunmeliydi.");
        Assert.All(parcalar, parca => Assert.True(parca.Length <= 255));

        var geri = DnsZoneFile.Parse(disa, Zone);
        Assert.Empty(geri.Errors);
        Assert.Equal(uzun, geri.Records.Single(r => r.Type == "TXT").Value);
    }

    [Fact]
    public void TXT_tirnak_ters_bolu_ve_ASCII_disi_karakterler_tur_atar()
    {
        var deger = "a \"q\" \\ b ş ü 😀 ;not-a-comment";
        var disa = DnsZoneFile.Export(Bolge(Kayit("TXT", "t", deger))).ZoneText;
        var geri = DnsZoneFile.Parse(disa, Zone);

        Assert.Empty(geri.Errors);
        Assert.Equal(deger, geri.Records.Single().Value);
    }

    [Fact]
    public void Tum_kayit_turleri_disa_ve_ice_aktarimda_tur_atar()
    {
        var bolge = Bolge(
            Kayit("A", "@", "203.0.113.5"),
            Kayit("AAAA", "@", "2001:db8::5"),
            Kayit("CNAME", "www", "ornek.com"),
            Kayit("MX", "@", "mail.ornek.com", 20),
            Kayit("SRV", "_sip._tcp", "5 5060 sip.ornek.com", 10),
            Kayit("CAA", "@", "0 issue letsencrypt.org"),
            Kayit("TLSA", "_25._tcp.mail", "3 1 1 " + new string('a', 64)),
            Kayit("NS", "alt", "ns.baska.net"),
            Kayit("TXT", "@", "v=spf1 a mx ~all"));

        var geri = DnsZoneFile.Parse(DnsZoneFile.Export(bolge).ZoneText, Zone);

        Assert.Empty(geri.Errors);
        Assert.Equal(9, geri.Records.Count);
        Assert.Contains(geri.Records, r => r.Type == "SRV" && r.Priority == 10 && r.Value == "5 5060 sip.ornek.com");
        Assert.Contains(geri.Records, r => r.Type == "CAA" && r.Value == "0 issue letsencrypt.org");
        Assert.Contains(geri.Records, r => r.Type == "MX" && r.Priority == 20 && r.Value == "mail.ornek.com");
        // Kök NS ve SOA kayıt olarak içeri alınmaz (bölge ayarlarının işi); sessizce değil, raporlanarak.
        Assert.Contains(geri.Ignored, i => i.Code == "dns.import_ignored_soa");
        Assert.Contains(geri.Ignored, i => i.Code == "dns.import_ignored_apex_ns");
    }

    // --- İçe aktarma: dosya biçimi ---

    [Fact]
    public void Gorece_adlar_sahip_mirasi_parantezli_SOA_ve_TTL_birimleri_okunur()
    {
        var dosya = string.Join('\n',
            "$ORIGIN ornek.com.",
            "$TTL 1h",
            "@ IN SOA ns1.ornek.com. admin.ornek.com. (",
            "      2024010101 ; serial",
            "      3600 600 1209600 3600 )",
            "@        IN A     203.0.113.5",
            "         IN AAAA  2001:db8::5",
            "www 30m  IN CNAME @",
            "mail     IN MX    10 mx");

        var sonuc = DnsZoneFile.Parse(dosya, Zone);

        Assert.Empty(sonuc.Errors);
        Assert.Equal(4, sonuc.Records.Count);
        Assert.Contains(sonuc.Records, r => r.Type == "AAAA" && r.Host == "@" && r.Ttl == 3600);
        Assert.Contains(sonuc.Records, r => r.Type == "CNAME" && r.Host == "www" && r.Value == "ornek.com" && r.Ttl == 1800);
        Assert.Contains(sonuc.Records, r => r.Type == "MX" && r.Host == "mail" && r.Value == "mx.ornek.com" && r.Priority == 10);
    }

    [Theory]
    [InlineData("$INCLUDE /etc/passwd\n@ A 203.0.113.5", "dns.import_directive_forbidden")]
    [InlineData("$GENERATE 1-10 h$ A 203.0.113.$\n@ A 203.0.113.5", "dns.import_directive_forbidden")]
    [InlineData("evil.baska.com. 300 IN A 203.0.113.5", "dns.import_out_of_zone")]
    [InlineData("baska.com. 300 IN A 203.0.113.5", "dns.import_out_of_zone")]
    [InlineData("@ IN DNSKEY 257 3 13 AAAA", "dns.import_type_not_allowed")]
    [InlineData("@ IN HINFO a b", "dns.import_type_not_allowed")]
    [InlineData("@ CH A 203.0.113.5", "dns.import_class_not_allowed")]
    [InlineData("@ IN A 999.1.1.1", "dns.invalid_record_value")]
    [InlineData("@ IN MX 99999 mail", "dns.import_malformed")]
    [InlineData("@ IN TXT \"acik tirnak", "dns.import_unterminated_quote")]
    [InlineData("  IN A 203.0.113.5", "dns.import_no_owner")]
    public void Tehlikeli_ya_da_gecersiz_girdi_REDDEDILIR(string dosya, string beklenenKod)
    {
        var sonuc = DnsZoneFile.Parse(dosya, Zone);

        Assert.Contains(sonuc.Errors, e => e.Code == beklenenKod);
        // Hata veren satir kayit olarak da gecmez.
        Assert.DoesNotContain(sonuc.Records, r => r.Value.Contains("999", StringComparison.Ordinal));
    }

    [Fact]
    public void Bolge_disi_ORIGIN_reddedilir()
    {
        var sonuc = DnsZoneFile.Parse("$ORIGIN baska.org.\nwww IN A 203.0.113.5", Zone);
        Assert.Contains(sonuc.Errors, e => e.Code == "dns.import_out_of_zone");
    }

    [Fact]
    public void Kayit_sayisi_siniri_asilirsa_hata()
    {
        var satirlar = string.Join('\n', Enumerable.Range(1, DnsZoneFile.MaxRecords + 1).Select(i => $"h{i} IN A 203.0.113.5"));
        var sonuc = DnsZoneFile.Parse(satirlar, Zone);

        Assert.Contains(sonuc.Errors, e => e.Code == "dns.import_too_many");
        Assert.Equal(DnsZoneFile.MaxRecords, sonuc.Records.Count);
    }

    [Fact]
    public void Tam_sinirdaki_kayit_sayisi_kabul_edilir()
    {
        var satirlar = string.Join('\n', Enumerable.Range(1, DnsZoneFile.MaxRecords).Select(i => $"h{i} IN A 203.0.113.5"));
        Assert.Empty(DnsZoneFile.Parse(satirlar, Zone).Errors);
    }

    [Fact]
    public void Metin_boyut_siniri_asilirsa_hic_ayristirilmaz()
    {
        var dev = new string('x', DnsZoneFile.MaxTextChars + 1);
        var sonuc = DnsZoneFile.Parse(dev, Zone);

        Assert.Contains(sonuc.Errors, e => e.Code == "dns.import_too_large");
        Assert.Empty(sonuc.Records);
    }

    [Fact]
    public void Bos_metin_ve_yalniz_yorum_hata_verir()
    {
        Assert.Contains(DnsZoneFile.Parse("   \n", Zone).Errors, e => e.Code == "dns.import_empty");
        Assert.Contains(DnsZoneFile.Parse("; sadece yorum\n", Zone).Errors, e => e.Code == "dns.import_no_records");
    }

    [Fact]
    public void Denetim_karakteri_iceren_deger_reddedilir()
    {
        // Satır enjeksiyonu: PowerShell/cmd yoluna giden değerde denetim karakteri meşru değil.
        var sonuc = DnsZoneFile.Parse("@ IN TXT \"a\u0001b\"", Zone);
        Assert.NotEmpty(sonuc.Errors);
    }

    [Fact]
    public void Ayni_kayit_iki_kez_yazilirsa_tek_kayit_olur_ve_rapor_edilir()
    {
        var sonuc = DnsZoneFile.Parse("www IN A 203.0.113.5\nwww IN A 203.0.113.5", Zone);
        Assert.Single(sonuc.Records);
        Assert.Contains(sonuc.Ignored, i => i.Code == "dns.import_duplicate");
    }
}
