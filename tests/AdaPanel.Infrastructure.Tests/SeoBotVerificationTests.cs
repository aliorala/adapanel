using AdaPanel.Infrastructure.Seo;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// SAHTE BOT DOĞRULAMASI — ve daha önemlisi, <b>ne zaman hüküm VERİLMEDİĞİ</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Korunan hata sınıfı:</b> ölçülmemiş bir şeyi iddia etmek. <c>User-Agent</c> istemcinin
/// yazdığı serbest metindir; "Googlebot" yazan her istemciyi Google saymak, kullanıcının gerçekte
/// bir kazıyıcıya ait trafiği SEO başarısı sanmasına yol açar. Tersi de aynı derecede zararlıdır:
/// DNS cevap vermediği için "sahte" demek, kullanıcıyı GERÇEK Googlebot'u engellemeye iter ve
/// sitesini arama sonuçlarından düşürür.
/// </para>
/// <para>
/// Bu yüzden testlerin YARISI "hüküm verilmediğini" ölçer.
/// </para>
/// </remarks>
public sealed class SeoBotVerificationTests
{
    private static readonly SeoBotEngine Google = SeoBotCatalog.ByKey("google")!;
    private static readonly SeoBotEngine DuckDuckGo = SeoBotCatalog.ByKey("duckduckgo")!;

    private static SeoBotClaim Iddia(string ip, SeoBotEngine? engine = null, long requests = 10) =>
        new(ip, engine ?? Google, requests);

    /// <summary>
    /// GERÇEK Googlebot: ters DNS <c>.googlebot.com</c> ile bitiyor VE ileri çözüm aynı IP'yi
    /// veriyor. Bu, Google'ın kendi tarif ettiği doğrulama zinciridir.
    /// </summary>
    [Fact]
    public async Task Ileri_teyitli_ters_DNS_gecerse_dogrulanir()
    {
        var dns = new SahteDns
        {
            Ters = { ["66.249.66.1"] = "crawl-66-249-66-1.googlebot.com" },
            Ileri = { ["crawl-66-249-66-1.googlebot.com"] = ["66.249.66.1"] }
        };

        var sonuc = await new SeoBotVerifier(dns).VerifyAsync([Iddia("66.249.66.1")], verify: true, CancellationToken.None);

        Assert.True(sonuc.Performed);
        Assert.Equal(1, sonuc.VerifiedIps);
        Assert.Equal(0, sonuc.SpoofedIps);
        Assert.Equal(SeoBotVerdict.Verified, sonuc.Identities.Single().Verdict);
        Assert.Equal("crawl-66-249-66-1.googlebot.com", sonuc.Identities.Single().ReverseDns);
    }

    /// <summary>
    /// SAHTE BOT: ters DNS çözüldü ama Google'a ait değil. Hüküm ancak KANIT varken verilir.
    /// </summary>
    [Fact]
    public async Task Ters_DNS_motora_ait_degilse_sahte_sayilir()
    {
        var dns = new SahteDns { Ters = { ["185.220.101.5"] = "tor-exit.example.net" } };

        var sonuc = await new SeoBotVerifier(dns).VerifyAsync([Iddia("185.220.101.5")], verify: true, CancellationToken.None);

        Assert.Equal(1, sonuc.SpoofedIps);
        Assert.Equal(SeoBotVerdict.Spoofed, sonuc.Identities.Single().Verdict);
    }

    /// <summary>
    /// <b>MUTASYON KAPISI — SON EK NOKTAYLA BAŞLAMALI.</b> <c>evilgooglebot.com</c> adresi
    /// "googlebot.com ile bitiyor" der ve nokta olmadan DOĞRULANMIŞ sayılırdı: saldırgan kendi
    /// alan adını böyle seçip panelin doğrulamasını tamamen geçersiz kılardı.
    /// </summary>
    [Fact]
    public async Task Motor_alan_adina_benzeyen_sahte_alan_adi_gecemez()
    {
        var dns = new SahteDns
        {
            Ters = { ["1.2.3.4"] = "crawler.evilgooglebot.com" },
            Ileri = { ["crawler.evilgooglebot.com"] = ["1.2.3.4"] }
        };

        var sonuc = await new SeoBotVerifier(dns).VerifyAsync([Iddia("1.2.3.4")], verify: true, CancellationToken.None);

        Assert.Equal(0, sonuc.VerifiedIps);
        Assert.Equal(1, sonuc.SpoofedIps);
        Assert.False(SeoBotCatalog.MatchesReverseDns(Google, "crawler.evilgooglebot.com"));
        Assert.True(SeoBotCatalog.MatchesReverseDns(Google, "crawl-1-2-3-4.googlebot.com"));
    }

    /// <summary>
    /// <b>MUTASYON KAPISI — İLERİ TEYİT ATLANAMAZ.</b> Ters DNS kaydını saldırgan kendi ağında
    /// istediği gibi yazabilir. İleri çözüm BAŞKA bir IP veriyorsa bu, zincirin kırıldığı yerdir.
    /// </summary>
    [Fact]
    public async Task Ileri_cozum_baska_IP_veriyorsa_dogrulanmaz()
    {
        var dns = new SahteDns
        {
            Ters = { ["203.0.113.9"] = "crawl-fake.googlebot.com" },
            Ileri = { ["crawl-fake.googlebot.com"] = ["66.249.66.1"] }
        };

        var sonuc = await new SeoBotVerifier(dns).VerifyAsync([Iddia("203.0.113.9")], verify: true, CancellationToken.None);

        Assert.Equal(0, sonuc.VerifiedIps);
        Assert.Equal(1, sonuc.SpoofedIps);
    }

    /// <summary>
    /// TERS DNS ÇÖZÜLEMEZSE HÜKÜM YOK. DNS'in susması sahtelik kanıtı DEĞİLDİR; "sahte" demek,
    /// kullanıcıyı gerçek Googlebot'u engellemeye iterdi.
    /// </summary>
    [Fact]
    public async Task Ters_DNS_cozulemezse_sahte_denmez()
    {
        var sonuc = await new SeoBotVerifier(new SahteDns())
            .VerifyAsync([Iddia("66.249.66.1")], verify: true, CancellationToken.None);

        Assert.Equal(0, sonuc.SpoofedIps);
        Assert.Equal(0, sonuc.VerifiedIps);
        Assert.Equal(1, sonuc.UndeterminedIps);
        Assert.Equal(SeoBotVerdict.Unresolved, sonuc.Identities.Single().Verdict);
    }

    /// <summary>
    /// İLERİ ÇÖZÜM CEVAP VERMEZSE de hüküm yok: ters kayıt doğru görünse bile teyit YOKTUR.
    /// "Doğrulandı" demek, zincirin yarısını atlayıp tam kanıt iddia etmek olurdu.
    /// </summary>
    [Fact]
    public async Task Ileri_cozum_cevapsizsa_dogrulandi_denmez()
    {
        var dns = new SahteDns { Ters = { ["66.249.66.1"] = "crawl-66-249-66-1.googlebot.com" } };

        var sonuc = await new SeoBotVerifier(dns).VerifyAsync([Iddia("66.249.66.1")], verify: true, CancellationToken.None);

        Assert.Equal(0, sonuc.VerifiedIps);
        Assert.Equal(0, sonuc.SpoofedIps);
        Assert.Equal(SeoBotVerdict.Unresolved, sonuc.Identities.Single().Verdict);
    }

    /// <summary>
    /// DOĞRULAMA YÖNTEMİ DESTEKLENMEYEN MOTOR: DuckDuckGo kimliğini yayımlanmış IP listesiyle
    /// doğrulatır, ters DNS ile değil. Panel o listeyi indirmediği için HÜKÜM VERMEZ — ve bunu
    /// "sahte" ile karıştırmaz.
    /// </summary>
    [Fact]
    public async Task Ters_DNS_desteklemeyen_motorda_hukum_verilmez()
    {
        var dns = new SahteDns { Ters = { ["20.1.1.1"] = "her-neyse.example.com" } };

        var sonuc = await new SeoBotVerifier(dns)
            .VerifyAsync([Iddia("20.1.1.1", DuckDuckGo)], verify: true, CancellationToken.None);

        Assert.Equal(0, sonuc.SpoofedIps);
        Assert.Equal(1, sonuc.UndeterminedIps);
        Assert.Equal(SeoBotVerdict.Unsupported, sonuc.Identities.Single().Verdict);

        // Ve DNS'e HİÇ sorulmamalı: desteklenmeyen bir yöntem için ağ isteği boşa gider.
        Assert.Empty(dns.SorulanIpler);
    }

    /// <summary>
    /// DOĞRULAMA İSTENMEDİYSE HİÇ AĞ İSTEĞİ YAPILMAZ ve rapor bunu açıkça söyler.
    /// </summary>
    [Fact]
    public async Task Dogrulama_istenmediyse_aga_cikilmaz_ve_rapor_bunu_soyler()
    {
        var dns = new SahteDns { Ters = { ["66.249.66.1"] = "crawl-66-249-66-1.googlebot.com" } };

        var sonuc = await new SeoBotVerifier(dns).VerifyAsync([Iddia("66.249.66.1")], verify: false, CancellationToken.None);

        Assert.False(sonuc.Performed);
        Assert.Empty(dns.SorulanIpler);
        Assert.Equal(1, sonuc.CandidateIps);
        Assert.Equal(1, sonuc.UndeterminedIps);
        Assert.Equal(0, sonuc.SpoofedIps);
    }

    /// <summary>
    /// IP BÜTÇESİ: sınırsız ters DNS, analizi DNS sunucusunun hızına teslim etmek olurdu. Sıra
    /// gelmeyenler "hüküm yok" sayılır, sessizce yok sayılmaz.
    /// </summary>
    [Fact]
    public async Task IP_butcesi_asilinca_kalanlar_hukumsuz_sayilir()
    {
        var dns = new SahteDns();
        var iddialar = Enumerable
            .Range(1, SeoLogLimits.MaxVerifiedIps + 5)
            .Select(index => Iddia($"10.0.0.{index}"))
            .ToArray();

        var sonuc = await new SeoBotVerifier(dns).VerifyAsync(iddialar, verify: true, CancellationToken.None);

        Assert.Equal(SeoLogLimits.MaxVerifiedIps, sonuc.CheckedIps);
        Assert.Equal(SeoLogLimits.MaxVerifiedIps, dns.SorulanIpler.Count);
        Assert.Equal(iddialar.Length, sonuc.CandidateIps);
        Assert.Equal(iddialar.Length, sonuc.UndeterminedIps);
        Assert.Equal(0, sonuc.SpoofedIps);

        // SORULMAYANLAR DA LİSTEDE: bütçenin dolduğu gizlenmez.
        Assert.Contains(sonuc.Identities, item => item.Verdict == SeoBotVerdict.NotChecked);
    }

    /// <summary>
    /// EKRANDA ÖNCE SAHTELER: kullanıcının ilk görmesi gereken kalem odur.
    /// </summary>
    [Fact]
    public async Task Sahte_hukumler_listenin_basinda_gelir()
    {
        var dns = new SahteDns
        {
            Ters =
            {
                ["66.249.66.1"] = "crawl-66-249-66-1.googlebot.com",
                ["185.220.101.5"] = "tor-exit.example.net"
            },
            Ileri = { ["crawl-66-249-66-1.googlebot.com"] = ["66.249.66.1"] }
        };

        var sonuc = await new SeoBotVerifier(dns).VerifyAsync(
            [Iddia("66.249.66.1", requests: 900), Iddia("185.220.101.5", requests: 3)],
            verify: true,
            CancellationToken.None);

        Assert.Equal(SeoBotVerdict.Spoofed, sonuc.Identities.First().Verdict);
    }

    /// <summary>Ağa çıkmayan, ne sorulduğunu SAYAN çözücü.</summary>
    private sealed class SahteDns : ISeoReverseDnsResolver
    {
        public Dictionary<string, string> Ters { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string[]> Ileri { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> SorulanIpler { get; } = [];

        public Task<string> ReverseAsync(string ip, CancellationToken cancellationToken)
        {
            SorulanIpler.Add(ip);
            return Task.FromResult(Ters.TryGetValue(ip, out var ad) ? ad : string.Empty);
        }

        public Task<IReadOnlyList<string>> ForwardAsync(string hostName, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(Ileri.TryGetValue(hostName, out var ipler) ? ipler : []);
    }
}
