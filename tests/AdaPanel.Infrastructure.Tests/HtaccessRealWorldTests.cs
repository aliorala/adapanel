using AdaPanel.Core.Dtos.Htaccess;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Motor GERÇEK <c>.htaccess</c> dosyalarıyla sınanır — uydurulmuş örneklerle değil.
/// </summary>
/// <remarks>
/// Buradaki bloklar kaynağından BİREBİR alınmıştır:
/// <list type="bullet">
/// <item>WordPress: <c>wp-includes/class-wp-rewrite.php → mod_rewrite_rules()</c> çıktısı.</item>
/// <item>Laravel: <c>laravel/laravel</c> deposu, <c>public/.htaccess</c>.</item>
/// <item>Symfony: <c>symfony/recipes-contrib</c>, <c>symfony/apache-pack/1.0/public/.htaccess</c>.</item>
/// <item>HTTPS zorlama / kanonik host / hotlink: Apache 2.4 belgeleri
/// (<c>rewrite/remapping.html</c>, <c>rewrite/avoid.html</c>) — TIRNAKLI yazımıyla.</item>
/// </list>
/// Bu dosyaların tırnak kullanımı, satır devamı ve bayrak yazımı da böylece test edilmiş olur.
/// </remarks>
public sealed class HtaccessRealWorldTests
{
    // ------------------------------------------------------------------ WordPress

    private const string WordPressBlogu = """
# BEGIN WordPress
<IfModule mod_rewrite.c>
RewriteEngine On
RewriteRule .* - [E=HTTP_AUTHORIZATION:%{HTTP:Authorization}]
RewriteBase /
RewriteRule ^index\.php$ - [L]
RewriteCond %{REQUEST_FILENAME} !-f
RewriteCond %{REQUEST_FILENAME} !-d
RewriteRule . /index.php [L]
</IfModule>
# END WordPress
""";

    [Fact]
    public void WordPress_kalici_baglantisi_var_olmayan_yolu_index_php_ye_yazar()
    {
        using var site = new HtaccessTestSite();
        site.Htaccess(WordPressBlogu);
        site.Write("index.php", "<?php");

        var sonuc = site.Evaluate("/2026/08/merhaba-dunya/");

        Assert.Equal(HtaccessOutcome.Rewritten, sonuc.Outcome);
        Assert.Equal("/index.php", sonuc.RewrittenPath);
        Assert.False(sonuc.EngineFailed);
    }

    [Fact]
    public void WordPress_gercekten_var_olan_dosyaya_dokunmaz()
    {
        using var site = new HtaccessTestSite();
        site.Htaccess(WordPressBlogu);
        site.Write("index.php", "<?php");
        site.Write("wp-content/uploads/logo.png", "PNG");

        var sonuc = site.Evaluate("/wp-content/uploads/logo.png");

        // -f koşulu tuttuğu için hiçbir kural uygulanmamalı: statik dosya olduğu gibi sunulur.
        Assert.Equal(HtaccessOutcome.None, sonuc.Outcome);
    }

    [Fact]
    public void WordPress_var_olan_dizine_dokunmaz()
    {
        using var site = new HtaccessTestSite();
        site.Htaccess(WordPressBlogu);
        site.Write("index.php", "<?php");
        site.Mkdir("wp-admin");

        var sonuc = site.Evaluate("/wp-admin/");

        Assert.Equal(HtaccessOutcome.None, sonuc.Outcome);
    }

    [Fact]
    public void WordPress_index_php_istegi_L_bayragiyla_durur_sonsuz_donguye_girmez()
    {
        using var site = new HtaccessTestSite();
        site.Htaccess(WordPressBlogu);
        site.Write("index.php", "<?php");

        var sonuc = site.Evaluate("/index.php");

        Assert.Equal(HtaccessOutcome.None, sonuc.Outcome);
        Assert.False(sonuc.EngineFailed);
        Assert.False(sonuc.Budget.Exceeded);
    }

    [Fact]
    public void E_bayragi_Authorization_basligini_ortam_degiskenine_tasir()
    {
        // WordPress ve Laravel'in ilk kuralı budur: Apache CGI'de Authorization başlığını
        // düşürdüğü için mod_rewrite onu ortam değişkenine kopyalar. Değişken sonraki
        // koşullardan %{ENV:...} ile okunabilmeli.
        using var site = new HtaccessTestSite();
        site.Htaccess("""
RewriteEngine On
RewriteRule .* - [E=HTTP_AUTHORIZATION:%{HTTP:Authorization}]
RewriteCond %{ENV:HTTP_AUTHORIZATION} ^Bearer
RewriteRule ^gizli$ /yetkili.php [L]
""");
        site.Write("yetkili.php", "<?php");

        var basliklar = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = "Bearer abc" };
        var jetonlu = site.Engine.Evaluate(site.Request("/gizli", basliklar: basliklar));
        Assert.Equal("/yetkili.php", jetonlu.RewrittenPath);

        // Başlık yoksa değişken de boş kalmalı; kural tutmamalı.
        var jetonsuz = site.Evaluate("/gizli");
        Assert.Equal(HtaccessOutcome.None, jetonsuz.Outcome);
    }

    [Fact]
    public void WordPress_sorgu_dizesi_yeniden_yazmada_korunur()
    {
        using var site = new HtaccessTestSite();
        site.Htaccess(WordPressBlogu);
        site.Write("index.php", "<?php");

        var sonuc = site.Evaluate("/arama", "s=merhaba&sayfa=2");

        Assert.Equal("/index.php", sonuc.RewrittenPath);
        Assert.Equal("s=merhaba&sayfa=2", sonuc.RewrittenQuery);
    }

    // ------------------------------------------------------------------ Laravel

    private const string LaravelBlogu = """
<IfModule mod_rewrite.c>
    <IfModule mod_negotiation.c>
        Options -MultiViews -Indexes
    </IfModule>

    RewriteEngine On

    # Handle Authorization Header
    RewriteCond %{HTTP:Authorization} .
    RewriteRule .* - [E=HTTP_AUTHORIZATION:%{HTTP:Authorization}]

    # Redirect Trailing Slashes If Not A Folder...
    RewriteCond %{REQUEST_FILENAME} !-d
    RewriteCond %{REQUEST_URI} (.+)/$
    RewriteRule ^ %1 [L,R=301]

    # Send Requests To Front Controller...
    RewriteCond %{REQUEST_FILENAME} !-d
    RewriteCond %{REQUEST_FILENAME} !-f
    RewriteRule ^ index.php [L]
</IfModule>
""";

    [Fact]
    public void Laravel_on_denetleyiciye_yonlendirir()
    {
        using var site = new HtaccessTestSite();
        site.Htaccess(LaravelBlogu);
        site.Write("index.php", "<?php");

        var sonuc = site.Evaluate("/api/kullanicilar");

        Assert.Equal(HtaccessOutcome.Rewritten, sonuc.Outcome);
        Assert.Equal("/index.php", sonuc.RewrittenPath);
    }

    [Fact]
    public void Laravel_sondaki_egik_cizgiyi_301_ile_atar()
    {
        using var site = new HtaccessTestSite();
        site.Htaccess(LaravelBlogu);
        site.Write("index.php", "<?php");

        var sonuc = site.Evaluate("/hakkimizda/");

        Assert.Equal(HtaccessOutcome.Redirect, sonuc.Outcome);
        Assert.Equal(301, sonuc.StatusCode);
        // %1 geri referansı RewriteCond'un yakalamasıdır: "/hakkimizda"
        Assert.Equal("http://ornek.com/hakkimizda", sonuc.RedirectLocation);
    }

    [Fact]
    public void Laravel_gercek_bir_dizin_icin_egik_cizgiyi_ATMAZ()
    {
        using var site = new HtaccessTestSite();
        site.Htaccess(LaravelBlogu);
        site.Write("index.php", "<?php");
        site.Mkdir("storage");

        var sonuc = site.Evaluate("/storage/");

        // !-d koşulu tuttuğu için yönlendirme kuralı atlanmalı.
        Assert.NotEqual(HtaccessOutcome.Redirect, sonuc.Outcome);
    }

    [Fact]
    public void Laravel_mod_negotiation_blogundaki_Indexes_kapatmasi_UYGULANIR()
    {
        using var site = new HtaccessTestSite();
        site.Htaccess(LaravelBlogu);
        site.Write("index.php", "<?php");

        var sonuc = site.Evaluate("/api/kullanicilar");

        // <IfModule mod_negotiation.c> bloğu atlanırsa dizin listeleme AÇIK kalırdı.
        Assert.False(sonuc.Options.Indexes);
        Assert.False(sonuc.Options.MultiViews);
    }

    // ------------------------------------------------------------------ Symfony

    private const string SymfonyBlogu = """
DirectoryIndex index.php

<IfModule mod_negotiation.c>
    Options -MultiViews
</IfModule>

<IfModule mod_rewrite.c>
    Options +SymLinksIfOwnerMatch

    RewriteEngine On

    RewriteCond %{REQUEST_URI}::$0 ^(/.+)/(.*)::\2$
    RewriteRule .* - [E=BASE:%1]

    RewriteCond %{HTTP:Authorization} .+
    RewriteRule ^ - [E=HTTP_AUTHORIZATION:%0]

    RewriteCond %{ENV:REDIRECT_STATUS} =""
    RewriteRule ^index\.php(?:/(.*)|$) %{ENV:BASE}/$1 [R=308,L]

    RewriteCond %{REQUEST_FILENAME} !-f
    RewriteRule ^ %{ENV:BASE}/index.php [L]
</IfModule>

<IfModule !mod_rewrite.c>
    <IfModule mod_alias.c>
        RedirectMatch 307 ^/$ /index.php/
    </IfModule>
</IfModule>
""";

    [Fact]
    public void Symfony_on_denetleyiciye_yonlendirir()
    {
        using var site = new HtaccessTestSite();
        site.Htaccess(SymfonyBlogu);
        site.Write("index.php", "<?php");

        var sonuc = site.Evaluate("/blog/yazi-1");

        Assert.Equal(HtaccessOutcome.Rewritten, sonuc.Outcome);
        Assert.Equal("/index.php", sonuc.RewrittenPath);
        Assert.Equal(["index.php"], sonuc.DirectoryIndex);
    }

    [Fact]
    public void Symfony_olumsuz_IfModule_blogu_ATLANIR()
    {
        using var site = new HtaccessTestSite();
        site.Htaccess(SymfonyBlogu);
        site.Write("index.php", "<?php");

        // <IfModule !mod_rewrite.c> içindeki RedirectMatch 307 çalışsaydı kök isteği
        // /index.php/ adresine yönlenirdi.
        var sonuc = site.Evaluate("/");

        Assert.NotEqual(307, sonuc.StatusCode);
    }

    [Fact]
    public void Symfony_index_php_istegi_308_ile_koke_yonlenir()
    {
        using var site = new HtaccessTestSite();
        site.Htaccess(SymfonyBlogu);
        site.Write("index.php", "<?php");

        var sonuc = site.Evaluate("/index.php/blog");

        Assert.Equal(HtaccessOutcome.Redirect, sonuc.Outcome);
        Assert.Equal(308, sonuc.StatusCode);
    }

    // ------------------------------------------------------------------ Apache belgelerindeki kalıplar

    [Fact]
    public void HTTPS_zorlama_calisir()
    {
        // Apache belgelerindeki `!=on` sözcüksel karşılaştırması + gerçek dünyada en yaygın
        // .htaccess yazımı (%{REQUEST_URI} kullanır, çünkü dizin-içi bağlamda $1'de baştaki
        // eğik çizgi YOKTUR — bkz. Dizin_ici_baglamda_desen_bastaki_egik_cizgi_olmadan_eslesir).
        using var site = new HtaccessTestSite();
        site.Htaccess("""
RewriteEngine On
RewriteCond "%{HTTPS}" !=on
RewriteRule ^(.*)$ https://%{HTTP_HOST}%{REQUEST_URI} [R=301,L]
""");

        var duz = site.Engine.Evaluate(site.Request("/urunler", https: false));
        Assert.Equal(HtaccessOutcome.Redirect, duz.Outcome);
        Assert.Equal(301, duz.StatusCode);
        Assert.Equal("https://ornek.com/urunler", duz.RedirectLocation);

        var guvenli = site.Engine.Evaluate(site.Request("/urunler", https: true));
        Assert.Equal(HtaccessOutcome.None, guvenli.Outcome);
    }

    [Fact]
    public void Dizin_ici_baglamda_desen_bastaki_egik_cizgi_olmadan_eslesir()
    {
        // Apache mod_rewrite belgeleri, "Per-directory Rewrites":
        //   "The removed prefix always ends with a slash, meaning the matching occurs against
        //    a string which never has a leading slash. Therefore, a Pattern with ^/ never
        //    matches in per-directory context."
        // Bu kural sık yanlış bilinir; motorun Apache ile aynı davrandığı burada SABİTLENİR.
        using var site = new HtaccessTestSite();
        site.Htaccess("""
RewriteEngine On
RewriteRule ^/urunler$ /asla-olmaz.php [L]
RewriteRule ^urunler$ /katalog.php [L]
""");

        var sonuc = site.Evaluate("/urunler");

        Assert.Equal("/katalog.php", sonuc.RewrittenPath);
    }

    [Fact]
    public void Kanonik_host_www_yonlendirmesi_Apache_belgelerindeki_yazimla_calisir()
    {
        // httpd.apache.org/docs/2.4/rewrite/remapping.html — "Canonical Hostnames"
        using var site = new HtaccessTestSite();
        site.Htaccess("""
RewriteEngine On
RewriteCond "%{HTTP_HOST}"   "!^www\.ornek\.com"       [NC]
RewriteCond "%{HTTP_HOST}"   "!^$"
RewriteRule "^/?(.*)"        "http://www.ornek.com/$1" [L,R,NE]
""");

        var wwwsuz = site.Engine.Evaluate(site.Request("/iletisim", host: "ornek.com"));
        Assert.Equal(HtaccessOutcome.Redirect, wwwsuz.Outcome);
        Assert.Equal(302, wwwsuz.StatusCode); // [R] varsayılanı 302'dir
        Assert.Equal("http://www.ornek.com/iletisim", wwwsuz.RedirectLocation);

        var wwwlu = site.Engine.Evaluate(site.Request("/iletisim", host: "www.ornek.com"));
        Assert.Equal(HtaccessOutcome.None, wwwlu.Outcome);
    }

    [Fact]
    public void Hotlink_korumasi_yabanci_referer_icin_403_verir()
    {
        // httpd.apache.org/docs/2.4/rewrite/avoid.html — "Forbidding Image Hotlinking"
        using var site = new HtaccessTestSite();
        site.Htaccess("""
RewriteEngine On
RewriteCond "%{HTTP_REFERER}"  "!^$"
RewriteCond "%{HTTP_REFERER}"  "!www.ornek.com" [NC]
RewriteRule "\.(gif|jpg|png)$" "-"                [F,NC]
""");
        site.Write("resim.jpg", "JPEG");

        var yabanci = site.Engine.Evaluate(site.Request("/resim.jpg",
            basliklar: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Referer"] = "http://hirsiz.example/sayfa" }));
        Assert.Equal(HtaccessOutcome.Forbidden, yabanci.Outcome);
        Assert.Equal(403, yabanci.StatusCode);

        var kendi = site.Engine.Evaluate(site.Request("/resim.jpg",
            basliklar: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Referer"] = "http://www.ornek.com/galeri" }));
        Assert.Equal(HtaccessOutcome.None, kendi.Outcome);

        // Referer HİÇ yoksa (doğrudan adres çubuğuna yazma) engellenmez.
        var referersiz = site.Engine.Evaluate(site.Request("/resim.jpg"));
        Assert.Equal(HtaccessOutcome.None, referersiz.Outcome);
    }

    [Fact]
    public void Dizin_listeleme_kapatma_ve_hata_belgesi_okunur()
    {
        using var site = new HtaccessTestSite();
        site.Htaccess("""
Options -Indexes
ErrorDocument 403 /hata/403.html
ErrorDocument 404 /hata/404.html
DirectoryIndex index.html index.php
AddType application/x-httpd-php .php
AddDefaultCharset UTF-8
""");

        var sonuc = site.Evaluate("/");

        Assert.False(sonuc.Options.Indexes);
        Assert.Equal("/hata/404.html", sonuc.ErrorDocuments[404]);
        Assert.Equal(["index.html", "index.php"], sonuc.DirectoryIndex);
        Assert.Equal("application/x-httpd-php", sonuc.MimeTypes[".php"]);
        Assert.Equal("UTF-8", sonuc.DefaultCharset);
    }

    [Fact]
    public void Guvenlik_basliklari_ve_onbellek_omru_okunur()
    {
        using var site = new HtaccessTestSite();
        site.Htaccess("""
<IfModule mod_headers.c>
    Header set X-Frame-Options "SAMEORIGIN"
    Header always set X-Content-Type-Options "nosniff"
    Header unset X-Powered-By
</IfModule>

<IfModule mod_expires.c>
    ExpiresActive On
    ExpiresDefault "access plus 1 month"
    ExpiresByType text/css "access plus 1 year"
    ExpiresByType image/png "access plus 2 weeks"
</IfModule>
""");

        var sonuc = site.Evaluate("/stil.css");

        Assert.Contains(sonuc.Headers, h => h is { Name: "X-Frame-Options", Value: "SAMEORIGIN", Operation: HtaccessHeaderOperation.Set });
        Assert.Contains(sonuc.Headers, h => h is { Name: "X-Content-Type-Options", Always: true });
        Assert.Contains(sonuc.Headers, h => h is { Name: "X-Powered-By", Operation: HtaccessHeaderOperation.Unset });

        Assert.True(sonuc.Expires.Active);
        Assert.Equal(31536000, sonuc.Expires.Resolve("text/css")!.Seconds);
        Assert.Equal(1209600, sonuc.Expires.Resolve("image/png")!.Seconds);
        Assert.Equal(2592000, sonuc.Expires.Resolve("application/pdf")!.Seconds);
        // MIME tipinde parametre olması eşleşmeyi bozmamalı.
        Assert.Equal(31536000, sonuc.Expires.Resolve("text/css; charset=utf-8")!.Seconds);
    }

    [Fact]
    public void FilesMatch_blogu_yalnizca_eslesen_dosyada_calisir()
    {
        using var site = new HtaccessTestSite();
        site.Htaccess("""
<FilesMatch "^(wp-config\.php|\.htaccess|\.env)$">
    Require all denied
</FilesMatch>
""");
        site.Write("wp-config.php", "<?php");
        site.Write("index.php", "<?php");

        Assert.Equal(HtaccessOutcome.AccessDenied, site.Evaluate("/wp-config.php").Outcome);
        Assert.Equal(HtaccessOutcome.None, site.Evaluate("/index.php").Outcome);
    }

    [Fact]
    public void Files_blogu_joker_karakterle_eslesir()
    {
        using var site = new HtaccessTestSite();
        site.Htaccess("""
<Files "*.log">
    Order allow,deny
    Deny from all
</Files>
""");

        Assert.Equal(HtaccessOutcome.AccessDenied, site.Evaluate("/hata.log").Outcome);
        Assert.Equal(HtaccessOutcome.None, site.Evaluate("/hata.txt").Outcome);
    }

    [Fact]
    public void Apache_22_Order_deny_allow_beyaz_liste_gibi_calisir()
    {
        using var site = new HtaccessTestSite();
        site.Htaccess("""
Order deny,allow
Deny from all
Allow from 203.0.113.0/24
""");

        Assert.Equal(HtaccessOutcome.None, site.Engine.Evaluate(site.Request("/panel", uzakAdres: "203.0.113.10")).Outcome);
        Assert.Equal(HtaccessOutcome.AccessDenied, site.Engine.Evaluate(site.Request("/panel", uzakAdres: "198.51.100.4")).Outcome);
    }

    [Fact]
    public void Apache_24_Require_ip_kismi_on_ekle_calisir()
    {
        using var site = new HtaccessTestSite();
        site.Htaccess("""
Require ip 192.168.1
""");

        Assert.Equal(HtaccessOutcome.None, site.Engine.Evaluate(site.Request("/panel", uzakAdres: "192.168.1.77")).Outcome);
        Assert.Equal(HtaccessOutcome.AccessDenied, site.Engine.Evaluate(site.Request("/panel", uzakAdres: "192.168.2.77")).Outcome);
    }

    [Fact]
    public void Limit_blogu_yalnizca_belirtilen_yontemi_kisitlar()
    {
        using var site = new HtaccessTestSite();
        site.Htaccess("""
<Limit POST>
    Require all denied
</Limit>
""");

        Assert.Equal(HtaccessOutcome.AccessDenied, site.Engine.Evaluate(site.Request("/form", yontem: "POST")).Outcome);
        Assert.Equal(HtaccessOutcome.None, site.Engine.Evaluate(site.Request("/form")).Outcome);
    }

    // ------------------------------------------------------------------ temel kimlik doğrulama

    [Fact]
    public void Temel_kimlik_dogrulama_401_uretir_ve_dogru_parolayi_kabul_eder()
    {
        using var site = new HtaccessTestSite();
        // "$apr1$r31.....$HqJZimcKQFAMYayBlzkrA/" -> "myPassword"
        // (Apache'nin kendi belgelerindeki test vektörü.)
        site.Write(".htpasswd", "ali:$apr1$r31.....$HqJZimcKQFAMYayBlzkrA/\n");
        site.Htaccess("""
AuthType Basic
AuthName "Yonetim Alani"
AuthUserFile .htpasswd
Require valid-user
""");

        var sonuc = site.Evaluate("/gizli.html");

        Assert.Equal(HtaccessOutcome.AuthenticationRequired, sonuc.Outcome);
        Assert.Equal(401, sonuc.StatusCode);
        Assert.NotNull(sonuc.Auth);
        Assert.Equal("Yonetim Alani", sonuc.Auth!.Realm);
        Assert.True(sonuc.Auth.AnyValidUser);

        Assert.True(site.Engine.Authenticate(sonuc.Auth, "ali", "myPassword").Succeeded);
        Assert.False(site.Engine.Authenticate(sonuc.Auth, "ali", "yanlis").Succeeded);
        Assert.False(site.Engine.Authenticate(sonuc.Auth, "veli", "myPassword").Succeeded);
    }

    [Fact]
    public void Kimligi_dogrulanmis_kullanici_icin_erisim_acilir()
    {
        using var site = new HtaccessTestSite();
        site.Write(".htpasswd", "ali:$apr1$r31.....$HqJZimcKQFAMYayBlzkrA/\n");
        site.Htaccess("""
AuthType Basic
AuthName "Yonetim"
AuthUserFile .htpasswd
Require valid-user
""");

        var sonuc = site.Engine.Evaluate(site.Request("/gizli.html", kullanici: "ali"));

        Assert.Equal(HtaccessOutcome.None, sonuc.Outcome);
    }

    [Fact]
    public void Require_user_listesindeki_kullanici_disindakiler_reddedilir()
    {
        using var site = new HtaccessTestSite();
        site.Write(".htpasswd", "ali:$apr1$r31.....$HqJZimcKQFAMYayBlzkrA/\nveli:$apr1$r31.....$HqJZimcKQFAMYayBlzkrA/\n");
        site.Htaccess("""
AuthType Basic
AuthName "Yonetim"
AuthUserFile .htpasswd
Require user ali
""");

        var sorgu = site.Evaluate("/gizli.html").Auth!;
        Assert.True(site.Engine.Authenticate(sorgu, "ali", "myPassword").Succeeded);
        Assert.False(site.Engine.Authenticate(sorgu, "veli", "myPassword").Succeeded);
    }
}
