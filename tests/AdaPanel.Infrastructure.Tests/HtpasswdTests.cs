using System.Security.Cryptography;
using System.Text;
using AdaPanel.Core.Dtos.Htaccess;
using AdaPanel.Infrastructure.Htaccess.Htpasswd;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// <c>.htpasswd</c> parola biçimlerinin doğrulaması — YAYIMLANMIŞ test vektörleriyle.
/// </summary>
/// <remarks>
/// <para>
/// bcrypt ve <c>$apr1$</c> bu depoda SIFIRDAN yazıldı (yeni NuGet bağımlılığı eklenmedi). Kendi
/// uygulamanı kendi çıktınla doğrulamak DÖNGÜSELDİR ve hiçbir şey kanıtlamaz; bu yüzden bütün
/// vektörler dış kaynaklıdır:
/// </para>
/// <list type="bullet">
/// <item>bcrypt: OpenWall'ın <c>crypt_blowfish</c> test kümesi ve jBCrypt vektörleri
/// (passlib <c>test_handlers_bcrypt.py</c> üzerinden).</item>
/// <item><c>$apr1$</c>: Apache'nin KENDİ belgesindeki örnek
/// (<c>httpd.apache.org/docs/2.2/misc/password_encryptions.html</c>).</item>
/// <item>bcrypt <c>$2y$</c>/<c>$2b$</c>: <c>tg123/go-htpasswd</c> test dosyası.</item>
/// <item><c>{SHA}</c>: SHA-1 + Base64 tanımı gereği bağımsız olarak yeniden hesaplanabilir.</item>
/// </list>
/// </remarks>
public sealed class HtpasswdTests
{
    private static readonly HtaccessLimits Sinirlar = HtaccessLimits.Default;

    // ---------------------------------------------------------------- bcrypt

    [Theory]
    // OpenWall crypt_blowfish test kümesi
    [InlineData("U*U*U*U*", "$2a$05$c92SVSfjeiCD6F2nAD6y0uBpJDjdRkt0EgeC4/31Rf2LUZbDRDE.O")]
    [InlineData("U*U***U", "$2a$05$WY62Xk2TXZ7EvVDQ5fmjNu7b0GEzSzUXUh2cllxJwhtOeMtWV3Ujq")]
    [InlineData("U*U***U*", "$2a$05$Fa0iKV3E2SYVUlMknirWU.CFYGvJ67UwVKI1E2FP6XeLiZGcH3MJi")]
    [InlineData("*U*U*U*U", "$2a$05$.WRrXibc1zPgIdRXYfv.4uu6TD1KWf0VnHzq/0imhUhuxSxCyeBs2")]
    [InlineData("", "$2a$05$Otz9agnajgrAe0.kFVF9V.tzaStZ2s1s4ZWi/LY4sw2k/MTVFj/IO")]
    [InlineData("U*U", "$2a$05$CCCCCCCCCCCCCCCCCCCCC.E5YPO9kmyuRGyh0XouQYb4YMJKvyOeW")]
    [InlineData("U*U*", "$2a$05$CCCCCCCCCCCCCCCCCCCCC.VGOzA784oUp/Z0DY336zx7pLYAy0lwK")]
    [InlineData("U*U*U", "$2a$05$XXXXXXXXXXXXXXXXXXXXXOAcXxm9kjPGEMsLznoKqmqw7tc8WCx4a")]
    [InlineData("", "$2a$05$CCCCCCCCCCCCCCCCCCCCC.7uG0VCzI2bS7j6ymqJi9CdcdxiRTWNy")]
    // 98 karakter: bcrypt yalnızca İLK 72 BAYTI kullanır, gerisi yok sayılır.
    [InlineData("0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789chars after 72 are ignored",
        "$2a$05$abcdefghijklmnopqrstuu5s2v8.iXieOjg/.AySBTTZIIVFJeBui")]
    // jBCrypt kümesi (daha yüksek maliyet: tur döngüsü de sınanır)
    [InlineData("", "$2a$06$DCq7YPn5Rq63x1Lad4cll.TV4S6ytwfsfvkgY8jIucDrjc8deX1s.")]
    [InlineData("a", "$2a$10$k87L/MF28Q673VKh8/cPi.SUl7MU/rWuSiIDDFayrKk/1tBsSQu4u")]
    [InlineData("abc", "$2a$10$WvvTPHKwdBJ3uk0Z37EMR.hLA2W6N9AEBhEgrAOljy2Ae5MtaSIUi")]
    // $2y$ ve $2b$ sürümleri (go-htpasswd)
    [InlineData("bar", "$2y$05$bWBMg3oUStnhfy5rFvoyreviPySU6hvEmBub5wIlM/D.c5FeYJQ6O")]
    [InlineData("bar", "$2b$08$hQbZuw.cHsECArUAP9mOjehaJxTG9NMJfioQIHcbC0YyXpVybhoQa")]
    public void Bcrypt_yayimlanmis_vektorleri_dogrular(string parola, string ozet)
    {
        Assert.Equal(BCryptVerifier.Sonuc.Match, BCryptVerifier.Verify(ozet, parola, Sinirlar.MaxBcryptCost));
    }

    [Theory]
    [InlineData("yanlis", "$2a$06$DCq7YPn5Rq63x1Lad4cll.TV4S6ytwfsfvkgY8jIucDrjc8deX1s.")]
    [InlineData("ab", "$2a$10$k87L/MF28Q673VKh8/cPi.SUl7MU/rWuSiIDDFayrKk/1tBsSQu4u")]
    [InlineData("", "$2a$10$WvvTPHKwdBJ3uk0Z37EMR.hLA2W6N9AEBhEgrAOljy2Ae5MtaSIUi")]
    public void Bcrypt_yanlis_parolayi_REDDEDER(string parola, string ozet)
    {
        Assert.Equal(BCryptVerifier.Sonuc.NoMatch, BCryptVerifier.Verify(ozet, parola, Sinirlar.MaxBcryptCost));
    }

    [Theory]
    [InlineData("$2x$05$/OK.fbVrR/bpIqNJ5ianF.CE5elHaaO4EbggVDjb8P19RukzXSM3e")] // bilerek bozuk sürüm
    [InlineData("$2$05$......................")]
    public void Bcrypt_desteklenmeyen_surumler_reddedilir(string ozet)
    {
        Assert.Equal(BCryptVerifier.Sonuc.UnsupportedVariant, BCryptVerifier.Verify(ozet, "x", 31));
    }

    [Theory]
    [InlineData("")]
    [InlineData("$2y$")]
    [InlineData("$2y$0")]
    [InlineData("$2a$6$DCq7YPn5Rq63x1Lad4cll.TV4S6ytwfsfvkgY8jIucDrjc8deX1s.")] // tek basamaklı maliyet
    [InlineData("$2a$12$EXRkfkdmXn!gzds2SSitu.MW9.gAVqa9eLS1//RYtYCmB1eLHg.9q")] // alfabe dışı karakter
    [InlineData("duz-metin-degil-ozet")]
    public void Bcrypt_bozuk_ozetler_istisna_atmaz(string ozet)
    {
        var sonuc = BCryptVerifier.Verify(ozet, "x", 31);
        Assert.NotEqual(BCryptVerifier.Sonuc.Match, sonuc);
    }

    [Fact]
    public void Bcrypt_72_bayttan_sonrasi_yok_sayilir()
    {
        // OpenWall vektörü (yukarıdaki Theory'de de var) 98 karakterlik bir parolayı 72 bayta
        // kırpar. Aynı özetin, ilk 72 baytı AYNI olan BAŞKA bir parolayla da eşleşmesi gerekir —
        // "72 bayt sınırı" iddiasını doğrulayan asıl ölçüm budur.
        const string ozet = "$2a$05$abcdefghijklmnopqrstuu5s2v8.iXieOjg/.AySBTTZIIVFJeBui";
        const string ilk72 = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

        Assert.Equal(72, ilk72.Length);
        Assert.Equal(BCryptVerifier.Sonuc.Match, BCryptVerifier.Verify(ozet, ilk72, Sinirlar.MaxBcryptCost));
        Assert.Equal(BCryptVerifier.Sonuc.Match,
            BCryptVerifier.Verify(ozet, ilk72 + "BAMBASKA BIR SON", Sinirlar.MaxBcryptCost));

        // 72. bayttan ÖNCEKİ bir değişiklik ise eşleşmeyi bozmalı.
        Assert.Equal(BCryptVerifier.Sonuc.NoMatch,
            BCryptVerifier.Verify(ozet, "X" + ilk72[1..], Sinirlar.MaxBcryptCost));
    }

    // ---------------------------------------------------------------- $apr1$ ve $1$

    [Fact]
    public void Apr1_Apache_belgelerindeki_vektoru_dogrular()
    {
        // httpd.apache.org/docs/2.2/misc/password_encryptions.html
        Assert.True(ApacheMd5Crypt.Verify("$apr1$r31.....$HqJZimcKQFAMYayBlzkrA/", "myPassword"));
        Assert.False(ApacheMd5Crypt.Verify("$apr1$r31.....$HqJZimcKQFAMYayBlzkrA/", "myPasswore"));
        Assert.False(ApacheMd5Crypt.Verify("$apr1$r31.....$HqJZimcKQFAMYayBlzkrA/", ""));
    }

    [Fact]
    public void Apr1_uretimi_kendi_ureticisiyle_gidip_gelir()
    {
        // Çapa (yukarıdaki dış vektör) doğrulandığı için, üretim yolunun kendisi de güvenilir:
        // farklı tuz ve parola uzunluklarında (16 baytlık blok sınırı çevresinde) sınanır.
        foreach (var parola in new[] { "", "a", "abcdefghijklmno", "abcdefghijklmnop", "abcdefghijklmnopq", "çok uzun bir parola 1234567890" })
        {
            var ozet = ApacheMd5Crypt.Crypt(parola, "Xy8Zq2Lm", ApacheMd5Crypt.Apr1Magic);
            Assert.StartsWith("$apr1$Xy8Zq2Lm$", ozet);
            Assert.True(ApacheMd5Crypt.Verify(ozet, parola), parola);
            Assert.False(ApacheMd5Crypt.Verify(ozet, parola + "x"));
        }
    }

    [Fact]
    public void Md5crypt_1_biciminin_etiketi_apr1_den_FARKLI_sonuc_verir()
    {
        // İki biçim aynı algoritmayı kullanır ama etiket özete KARIŞTIRILIR: karıştırmasaydık
        // $1$ özetleri sessizce yanlış doğrulanırdı.
        var apr1 = ApacheMd5Crypt.Crypt("parola", "abcdefgh", ApacheMd5Crypt.Apr1Magic);
        var md5 = ApacheMd5Crypt.Crypt("parola", "abcdefgh", ApacheMd5Crypt.Md5Magic);

        Assert.NotEqual(apr1[apr1.LastIndexOf('$')..], md5[md5.LastIndexOf('$')..]);
        Assert.True(ApacheMd5Crypt.Verify(md5, "parola"));
    }

    // ---------------------------------------------------------------- {SHA} ve {SSHA}

    [Fact]
    public void Sha1_ozeti_dogrulanir()
    {
        // {SHA} = base64(sha1(parola)); bağımsız olarak yeniden hesaplanabilir.
        var ozet = "{SHA}" + Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes("secret")));
        Assert.Equal("{SHA}5en6G6MezRroT3XKqkdPOmY/BfQ=", ozet);

        var (gecti, _, _) = HtpasswdFile.OzetiDogrula(ozet, "secret", Sinirlar);
        Assert.True(gecti);

        var (yanlis, _, _) = HtpasswdFile.OzetiDogrula(ozet, "Secret", Sinirlar);
        Assert.False(yanlis);
    }

    [Fact]
    public void Ssha_tuzlu_ozeti_dogrulanir()
    {
        var tuz = "TUZ12345"u8.ToArray();
        var girdi = Encoding.UTF8.GetBytes("parola").Concat(tuz).ToArray();
        var ozet = "{SSHA}" + Convert.ToBase64String(SHA1.HashData(girdi).Concat(tuz).ToArray());

        Assert.True(HtpasswdFile.OzetiDogrula(ozet, "parola", Sinirlar).Gecti);
        Assert.False(HtpasswdFile.OzetiDogrula(ozet, "parolb", Sinirlar).Gecti);
    }

    // ---------------------------------------------------------------- dürüstlük

    [Theory]
    [InlineData("$5$rounds=5000$abc$def", "SHA-256")]
    [InlineData("$6$abc$def", "SHA-256")]
    public void Desteklenmeyen_bicimler_SEBEBIYLE_reddedilir(string ozet, string beklenenIpucu)
    {
        var (gecti, kod, sebep) = HtpasswdFile.OzetiDogrula(ozet, "x", Sinirlar);

        Assert.False(gecti);
        Assert.Equal(HtaccessDiagnosticCodes.UnsupportedHashScheme, kod);
        Assert.Contains(beklenenIpucu, sebep);
        Assert.Contains("htpasswd -B", sebep);
    }

    [Fact]
    public void Klasik_DES_crypt_sebebiyle_reddedilir()
    {
        var (gecti, kod, sebep) = HtpasswdFile.OzetiDogrula("abJnggxhB/yWI", "myPassword", Sinirlar);

        Assert.False(gecti);
        Assert.Equal(HtaccessDiagnosticCodes.UnsupportedHashScheme, kod);
        Assert.Contains("DES", sebep);
    }

    [Fact]
    public void Duz_metin_parola_Windows_ta_kabul_edilir()
    {
        // Apache düz metni yalnızca Windows/NetWare/BeOS'ta kabul eder; hedef platform Windows.
        Assert.True(HtpasswdFile.OzetiDogrula("acikparola", "acikparola", Sinirlar).Gecti);
        Assert.False(HtpasswdFile.OzetiDogrula("acikparola", "baskasi", Sinirlar).Gecti);
    }

    // ---------------------------------------------------------------- dosya okuma

    [Fact]
    public void Parola_dosyasindan_dogru_kullanici_secilir_ve_yorumlar_atlanir()
    {
        using var site = new HtaccessTestSite();
        site.Write(".htpasswd", """
# yorum satiri

ali:$apr1$r31.....$HqJZimcKQFAMYayBlzkrA/
veli:$2a$06$DCq7YPn5Rq63x1Lad4cll.TV4S6ytwfsfvkgY8jIucDrjc8deX1s.
bozuk-satir-iki-nokta-yok
""");
        site.Htaccess("""
AuthType Basic
AuthName "Alan"
AuthUserFile .htpasswd
Require valid-user
""");

        var sorgu = site.Evaluate("/gizli.html").Auth!;

        Assert.True(site.Engine.Authenticate(sorgu, "ali", "myPassword").Succeeded);
        Assert.True(site.Engine.Authenticate(sorgu, "veli", "").Succeeded);
        Assert.False(site.Engine.Authenticate(sorgu, "ali", "").Succeeded);
        Assert.False(site.Engine.Authenticate(sorgu, "yok", "myPassword").Succeeded);
    }

    [Fact]
    public void Var_olmayan_kullanici_ile_yanlis_parola_AYNI_cevabi_verir()
    {
        // Kullanıcı adı sızdırılmamalı: iki durumda da aynı kod ve aynı mesaj dönmeli.
        using var site = new HtaccessTestSite();
        site.Write(".htpasswd", "ali:$apr1$r31.....$HqJZimcKQFAMYayBlzkrA/\n");
        site.Htaccess("AuthType Basic\nAuthName Alan\nAuthUserFile .htpasswd\nRequire valid-user\n");

        var sorgu = site.Evaluate("/gizli.html").Auth!;
        var yokKullanici = site.Engine.Authenticate(sorgu, "hicyok", "birsey");
        var yanlisParola = site.Engine.Authenticate(sorgu, "ali", "birsey");

        Assert.Equal(yokKullanici.FailureCode, yanlisParola.FailureCode);
        Assert.Equal(yokKullanici.FailureReason, yanlisParola.FailureReason);
    }
}
