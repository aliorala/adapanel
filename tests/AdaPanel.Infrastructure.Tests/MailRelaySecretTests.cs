using System.Text.Json;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Mail;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Giden posta rölesi parolasının ASLA düz metin durmadığını ve hiçbir okuma yanıtında /
/// denetim kaydında görünmediğini sabitler.
/// </summary>
/// <remarks>
/// <para>
/// Röle parolası, sunucunun BÜTÜN giden postasını gönderebilen bir kimlik bilgisidir (SendGrid/
/// Mailgun API anahtarı gibi). Panel dosyaları yedeklere girer ve yedek panelden çok daha fazla
/// elden ele dolaşır; sırrın orada düz durması, tek bir yedek kopyasıyla bütün alan adlarından
/// posta gönderilebilmesi demektir.
/// </para>
/// <para>
/// Bu kural çalışma anında ölçülmesi zor ama saf fonksiyonlarda apaçık görünür bir kuraldır;
/// bu yüzden saklama ve DTO üretimi ayrı metotlara çıkarıldı.
/// </para>
/// </remarks>
public sealed class MailRelaySecretTests
{
    private const string Parola = "cok-gizli-role-parolasi-42";

    /// <summary>Testte DPAPI yerine geçen, tersine çevrilebilir ama DÜZ OLMAYAN koruyucu.</summary>
    private sealed class SahteKoruyucu : ISecretProtector
    {
        public string Protect(string plaintext) =>
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("korunmus:" + plaintext));

        public string Unprotect(string protectedValue) =>
            System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue))["korunmus:".Length..];
    }

    private static SetMailRelayRequest Istek() => new()
    {
        Enabled = true,
        Host = "smtp.saglayici.com",
        Port = 587,
        Username = "apikey",
        Password = Parola
    };

    [Fact]
    public void Role_parolasi_saklanirken_SIFRELENIR()
    {
        var kayit = MailService.MergeRelayStore(null, Istek(), new SahteKoruyucu());

        Assert.NotEqual(Parola, kayit.PasswordProtected);
        Assert.DoesNotContain(Parola, kayit.PasswordProtected, StringComparison.Ordinal);
        Assert.Equal(Parola, MailService.ResolveRelayPassword(kayit, new SahteKoruyucu()));
    }

    /// <summary>
    /// Kayıt dosyaya JSON olarak yazılır: SERİLEŞTİRİLMİŞ hâlinde de parola geçmemeli.
    /// (Alan adı değişse bile bu kontrol tutar; korunan şey biçim değil, sırrın kendisi.)
    /// </summary>
    [Fact]
    public void Role_kaydinin_diske_yazilan_hali_parolayi_TASIMAZ()
    {
        var kayit = MailService.MergeRelayStore(null, Istek(), new SahteKoruyucu());

        var json = JsonSerializer.Serialize(kayit);

        Assert.DoesNotContain(Parola, json, StringComparison.Ordinal);
    }

    /// <summary>
    /// Parola boş gönderilirse saklı parola KORUNUR: yönetici yalnız portu değiştirmek için
    /// parolayı yeniden yazmak zorunda kalmamalı (ve bir yere not etmek zorunda kalmamalı).
    /// </summary>
    [Fact]
    public void Parola_bos_gonderilirse_saklanan_parola_korunur()
    {
        var koruyucu = new SahteKoruyucu();
        var ilk = MailService.MergeRelayStore(null, Istek(), koruyucu);

        var guncel = MailService.MergeRelayStore(ilk, new SetMailRelayRequest
        {
            Enabled = true,
            Host = "smtp.saglayici.com",
            Port = 2525,
            Username = "apikey",
            Password = null
        }, koruyucu);

        Assert.Equal(2525, guncel.Port);
        Assert.Equal(Parola, MailService.ResolveRelayPassword(guncel, koruyucu));
    }

    /// <summary>Röle kapatılınca saklı parola SİLİNİR: kullanılmayan sır tutulmaz.</summary>
    [Fact]
    public void Role_kapatilinca_saklanan_parola_silinir()
    {
        var koruyucu = new SahteKoruyucu();
        var acik = MailService.MergeRelayStore(null, Istek(), koruyucu);

        var kapali = MailService.MergeRelayStore(acik, new SetMailRelayRequest { Enabled = false }, koruyucu);

        Assert.Equal(string.Empty, kapali.PasswordProtected);
        Assert.Null(MailService.ResolveRelayPassword(kapali, koruyucu));
    }

    /// <summary>
    /// Panelin döndüğü DTO'da parola alanı BULUNMAZ. "Maskeli" bir alan bile sırrın ikinci bir
    /// kopyasını (tarayıcı belleği, hata raporu, günlük) üretmeye açık kapı bırakırdı.
    /// </summary>
    [Fact]
    public void Role_yaniti_parola_alani_ICERMEZ()
    {
        var sirIceren = typeof(MailRelayDto)
            .GetProperties()
            .Where(item =>
                item.Name.Contains("Password", StringComparison.OrdinalIgnoreCase)
                || item.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Name)
            .Where(ad => ad != nameof(MailRelayDto.HasPassword))
            .ToArray();

        Assert.True(sirIceren.Length == 0, "MailRelayDto sir tasiyan alan(lar) iceriyor: " + string.Join(", ", sirIceren));
    }

    /// <summary>Canlı rota nesnesi çözümlenirken de yanıta yalnız kullanıcı adı taşınır.</summary>
    [Fact]
    public void Canli_rota_cozumlenirken_authSecret_yansitilmaz()
    {
        var rota = JsonDocument.Parse("""
            {"@type":"Relay","name":"adapanel-relay","address":"smtp.saglayici.com","port":587,
             "protocol":"smtp","implicitTls":false,"allowInvalidCerts":false,
             "authUsername":"apikey","authSecret":{"@type":"Value","secret":"cok-gizli-role-parolasi-42"}}
            """).RootElement.Clone();

        var dto = MailService.ParseRelayRoute(rota, etkin: true);

        Assert.Equal("smtp.saglayici.com", dto.Host);
        Assert.Equal(587, dto.Port);
        Assert.Equal("apikey", dto.Username);
        Assert.DoesNotContain(Parola, JsonSerializer.Serialize(dto), StringComparison.Ordinal);
    }

    /// <summary>
    /// Sunucuya giden gövde tam tersine parolayı TAŞIMALI (yoksa röle kimlik doğrulayamaz) ve
    /// şemanın istediği <c>authSecret</c> sarmalayıcısını kullanmalı.
    /// </summary>
    [Fact]
    public void Sunucuya_giden_govde_authSecret_sarmalayicisini_kullanir()
    {
        var kayit = MailService.MergeRelayStore(null, Istek(), new SahteKoruyucu());

        var govde = MailService.BuildRelayRouteJson(kayit, Parola, includeName: true);
        using var belge = JsonDocument.Parse(govde);
        var kok = belge.RootElement;

        Assert.Equal("Relay", kok.GetProperty("@type").GetString());
        Assert.Equal("smtp.saglayici.com", kok.GetProperty("address").GetString());
        Assert.Equal(587, kok.GetProperty("port").GetInt32());
        Assert.Equal("Value", kok.GetProperty("authSecret").GetProperty("@type").GetString());
        Assert.Equal(Parola, kok.GetProperty("authSecret").GetProperty("secret").GetString());
    }

    /// <summary>
    /// Kimlik doğrulama yoksa <c>authSecret</c> yine gönderilir (<c>None</c> varyantı):
    /// şemada ZORUNLU bir alandır, atlanırsa nesne geçersiz olur ve röle sessizce kurulmaz.
    /// </summary>
    [Fact]
    public void Kimlik_dogrulamasiz_role_None_varyantini_gonderir()
    {
        var kayit = MailService.MergeRelayStore(null, new SetMailRelayRequest
        {
            Enabled = true,
            Host = "ic-role.local",
            Port = 25
        }, new SahteKoruyucu());

        using var belge = JsonDocument.Parse(MailService.BuildRelayRouteJson(kayit, null, includeName: true));

        Assert.Equal("None", belge.RootElement.GetProperty("authSecret").GetProperty("@type").GetString());
        Assert.False(belge.RootElement.TryGetProperty("authUsername", out _));
    }

    /// <summary>
    /// Rota adı OLUŞTURULDUKTAN sonra değiştirilemez; güncelleme gövdesinde gönderilmemeli,
    /// yoksa sunucu salt-okunur alan yüzünden bütün güncellemeyi reddeder.
    /// </summary>
    [Fact]
    public void Guncelleme_govdesi_salt_okunur_ad_alanini_gondermez()
    {
        var kayit = MailService.MergeRelayStore(null, Istek(), new SahteKoruyucu());

        using var belge = JsonDocument.Parse(MailService.BuildRelayRouteJson(kayit, Parola, includeName: false));

        Assert.False(belge.RootElement.TryGetProperty("name", out _));
    }

    /// <summary>
    /// Yönlendirme ifadesi okunurken yöneticinin kendi <c>match</c> kuralları KORUNMALI.
    /// Ham metin taşınmazsa röle açıldığı anda yöneticinin özel yönlendirmesi sessizce silinir.
    /// </summary>
    [Fact]
    public void Yonlendirme_ifadesinin_match_dali_HAM_korunur()
    {
        var strateji = JsonDocument.Parse("""
            {"route":{"match":[{"if":"rcpt_domain = 'ic.local'","then":"'local'"}],"else":"'mx'"}}
            """).RootElement.Clone();

        Assert.Equal("'mx'", MailService.ReadRouteElse(strateji));
        Assert.Contains("ic.local", MailService.ReadRouteMatchRaw(strateji), StringComparison.Ordinal);
    }

    /// <summary>Yönlendirme hiç tanımlı değilse şemanın belgelenmiş varsayılanı (boş liste) kullanılır.</summary>
    [Fact]
    public void Yonlendirme_ifadesi_yoksa_bos_eslesme_listesi_kullanilir()
    {
        var strateji = JsonDocument.Parse("""{"connection":{"else":"'default'"}}""").RootElement.Clone();

        Assert.Equal(string.Empty, MailService.ReadRouteElse(strateji));
        Assert.Equal("[]", MailService.ReadRouteMatchRaw(strateji));
    }

    /// <summary>
    /// Denetim kaydına parola GEÇMEZ. <c>AuditCoverageTests</c> bunu bütün denetleyiciler için
    /// tarıyor; burada röle ucu ayrıca sabitleniyor çünkü kaydın "kime teslim ediliyor" bilgisini
    /// taşıması ŞART, yani boş bir kayıtla kural sağlanmış görünemez.
    /// </summary>
    [Fact]
    public void Role_ucu_denetim_kaydi_yazar_ama_parolayi_yazmaz()
    {
        var kaynak = File.ReadAllText(Path.Combine(KaynakKoku(), "src", "AdaPanel.Api", "Controllers", "MailController.cs"));
        var basi = kaynak.IndexOf("SetRelayAsync([FromBody]", StringComparison.Ordinal);
        Assert.True(basi > 0, "MailController'da role ucu bulunamadi.");

        var govde = kaynak[basi..];
        var sonu = govde.IndexOf("[Http", StringComparison.Ordinal);
        govde = sonu > 0 ? govde[..sonu] : govde;

        var cagri = govde.IndexOf("AuditAsync(", StringComparison.Ordinal);
        Assert.True(cagri > 0, "Role ucu denetim kaydi YAZMIYOR.");

        // Yalnız denetim çağrısının ARGÜMANLARI incelenir; yorum satırları değil. Aksi halde
        // "parolayi yazmiyoruz" diyen bir açıklama, testin kendisini kırardı.
        var argumanlar = ArgumanListesi(govde, govde.IndexOf('(', cagri));

        // Kayıt, giden postanın NEREYE teslim edildiğini içermeli; yoksa "kim ne yaptı" sorusu
        // cevapsız kalır ve boş bir kayıtla kural sağlanmış görünür.
        Assert.Contains("request.Host", argumanlar, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", argumanlar, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret", argumanlar, StringComparison.Ordinal);
    }

    /// <summary>Verilen açılış parantezinden dengeli kapanışa kadar olan argüman metni.</summary>
    private static string ArgumanListesi(string metin, int acilisIndeksi)
    {
        var derinlik = 0;
        for (var i = acilisIndeksi; i < metin.Length; i++)
        {
            if (metin[i] == '(')
            {
                derinlik++;
            }
            else if (metin[i] == ')')
            {
                derinlik--;
                if (derinlik == 0)
                {
                    return metin[(acilisIndeksi + 1)..i];
                }
            }
        }

        return metin[acilisIndeksi..];
    }

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
