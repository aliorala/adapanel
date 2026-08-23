using AdaPanel.Core.Dtos.Htaccess;
using AdaPanel.Infrastructure.Htaccess;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Ayrıştırıcının söz dizimi sözleşmesi: tırnaklar, satır devamı, yorumlar, bloklar ve
/// <b>tanınmayan direktifin ATLANMAMASI</b>.
/// </summary>
public sealed class HtaccessParserTests
{
    private static HtaccessParseResult Ayristir(string icerik) =>
        HtaccessParser.Parse(icerik, "test/.htaccess", "/", HtaccessLimits.Default);

    [Fact]
    public void Tirnakli_argumanlar_tek_parca_olarak_okunur()
    {
        var sonuc = Ayristir("""
Header set Content-Security-Policy "default-src 'self'; script-src 'self'"
AuthName "Yonetim Alani"
""");

        var header = sonuc.Directives[0];
        Assert.Equal("Header", header.Name);
        Assert.Equal(["set", "Content-Security-Policy", "default-src 'self'; script-src 'self'"], header.Arguments);

        Assert.Equal("Yonetim Alani", sonuc.Directives[1].Argument(0));
    }

    [Fact]
    public void Tek_tirnak_da_kabul_edilir_ve_kacis_cozulur()
    {
        var sonuc = Ayristir("""Header set X-Test 'deger "icinde" tirnak'""");

        Assert.Equal("deger \"icinde\" tirnak", sonuc.Directives[0].Argument(2));
    }

    [Fact]
    public void Kapanmayan_tirnak_satiri_uygulamaz_ve_bulgu_uretir()
    {
        var sonuc = Ayristir("Header set X-Test \"kapanmadi");

        Assert.Empty(sonuc.Directives);
        Assert.Contains(sonuc.Diagnostics, t => t.Code == HtaccessDiagnosticCodes.UnterminatedQuote);
    }

    [Fact]
    public void Satir_devami_birlestirilir_ve_satir_numarasi_baslangici_gosterir()
    {
        var sonuc = Ayristir("""
# yorum
RewriteCond %{HTTP_HOST} \
    ^ornek\.com$ [NC]
RewriteRule ^$ /giris.php [L]
""");

        var kosul = sonuc.Directives[0];
        Assert.Equal("RewriteCond", kosul.Name);
        Assert.Equal(["%{HTTP_HOST}", "^ornek\\.com$", "[NC]"], kosul.Arguments);
        Assert.Equal(2, kosul.Line);
        Assert.Equal(4, sonuc.Directives[1].Line);
    }

    [Fact]
    public void Cift_ters_bolu_satir_devami_SAYILMAZ()
    {
        // "\\" kaçırılmış bir ters bölüdür; satır orada BİTER.
        var sonuc = Ayristir("RewriteRule ^a$ /b\\\\\nRewriteRule ^c$ /d");

        Assert.Equal(2, sonuc.Directives.Count);
    }

    [Fact]
    public void Yorum_yalnizca_satir_basinda_gecerlidir_Apache_de_boyle_yapar()
    {
        var sonuc = Ayristir("RewriteRule ^a$ /b [L] # bu yorum DEGIL");

        var kural = sonuc.Directives[0];
        // Apache satır sonu yorumu tanımaz: '#' ve sonrası ARGÜMANDIR.
        Assert.Contains("#", kural.Arguments);
        Assert.Contains("DEGIL", kural.Arguments);
    }

    [Fact]
    public void Bos_satirlar_ve_girintili_yorumlar_atlanir()
    {
        var sonuc = Ayristir("\n   \n\t# girintili yorum\n\nRewriteEngine On\n");

        Assert.Single(sonuc.Directives);
        Assert.Equal("RewriteEngine", sonuc.Directives[0].Name);
    }

    [Fact]
    public void Ic_ice_bloklar_direktifin_uzerinde_DISTAN_ICE_tasinir()
    {
        var sonuc = Ayristir("""
<IfModule mod_rewrite.c>
    <FilesMatch "\.php$">
        Require all denied
    </FilesMatch>
</IfModule>
""");

        var direktif = Assert.Single(sonuc.Directives);
        Assert.Equal(["IfModule", "FilesMatch"], direktif.Sections.Select(b => b.Name));
        Assert.Equal("mod_rewrite.c", direktif.Sections[0].Argument);
        Assert.Equal("\\.php$", direktif.Sections[1].Argument);
    }

    [Fact]
    public void Olumsuz_IfModule_isaretlenir()
    {
        var sonuc = Ayristir("<IfModule !mod_rewrite.c>\nRedirect 307 /a /b\n</IfModule>");

        Assert.True(sonuc.Directives[0].Sections[0].Negated);
        Assert.Equal("mod_rewrite.c", sonuc.Directives[0].Sections[0].Argument);
    }

    [Fact]
    public void Kapanmayan_blok_dosyayi_KULLANILAMAZ_yapar()
    {
        var sonuc = Ayristir("<IfModule mod_rewrite.c>\nRewriteEngine On\n");

        Assert.False(sonuc.Usable);
        Assert.Contains(sonuc.Diagnostics,
            t => t is { Level: HtaccessDiagnosticLevel.Fatal, Code: HtaccessDiagnosticCodes.UnclosedSection });
    }

    [Fact]
    public void Eslesmeyen_blok_sonu_bulgu_uretir()
    {
        var sonuc = Ayristir("<IfModule mod_rewrite.c>\nRewriteEngine On\n</Files>");

        Assert.Contains(sonuc.Diagnostics, t => t.Code == HtaccessDiagnosticCodes.MismatchedSectionEnd);
    }

    [Fact]
    public void Acilmamis_blok_sonu_bulgu_uretir()
    {
        var sonuc = Ayristir("</IfModule>");

        Assert.Contains(sonuc.Diagnostics, t => t.Code == HtaccessDiagnosticCodes.UnexpectedSectionEnd);
    }

    // ---------------------------------------------------------------- dürüstlük

    [Fact]
    public void Taninmayan_direktif_ATLANMAZ_bulgu_uretir()
    {
        var sonuc = Ayristir("RewriteEngine On\nHicBoyleBirSeyYok filan\n");

        var bulgu = Assert.Single(sonuc.Diagnostics, t => t.Code == HtaccessDiagnosticCodes.UnknownDirective);
        Assert.Equal(HtaccessDiagnosticLevel.Unsupported, bulgu.Level);
        Assert.Equal("HicBoyleBirSeyYok", bulgu.Directive);
        Assert.Equal(2, bulgu.Line);
        Assert.Contains("yazım hatası", bulgu.Message);
    }

    [Theory]
    [InlineData("php_value memory_limit 256M", "php_value")]
    [InlineData("AddOutputFilterByType DEFLATE text/html", "AddOutputFilterByType")]
    [InlineData("SetEnvIf Referer ornek\\.com localreferer", "SetEnvIf")]
    [InlineData("RewriteMap alt txt:/tmp/x.txt", "RewriteMap")]
    [InlineData("Satisfy any", "Satisfy")]
    [InlineData("FallbackResource /index.php", "FallbackResource")]
    [InlineData("AuthGroupFile /var/www/.htgroup", "AuthGroupFile")]
    public void Taninan_ama_uygulanmayan_direktifler_SEBEBIYLE_bildirilir(string satir, string ad)
    {
        var sonuc = Ayristir(satir);

        var bulgu = Assert.Single(sonuc.Diagnostics, t => t.Code == HtaccessDiagnosticCodes.UnsupportedDirective);
        Assert.Equal(HtaccessDiagnosticLevel.Unsupported, bulgu.Level);
        Assert.Equal(ad, bulgu.Directive);
        // Sebep boş olamaz: kullanıcıya NE yapması gerektiği söylenmeli.
        Assert.True(bulgu.Message.Length > ad.Length + 20, bulgu.Message);
    }

    [Fact]
    public void Uygulanan_direktifler_bulgu_URETMEZ()
    {
        var sonuc = Ayristir("""
RewriteEngine On
RewriteBase /
RewriteRule ^a$ /b [L]
DirectoryIndex index.php
ErrorDocument 404 /404.html
Options -Indexes
Order deny,allow
Deny from all
Require all granted
Header set X-A B
ExpiresActive On
ExpiresByType text/css "access plus 1 year"
AuthType Basic
AuthName Alan
AuthUserFile .htpasswd
AddType text/plain .log
AddDefaultCharset UTF-8
SetEnv X 1
Redirect 301 /eski /yeni
RedirectMatch 302 ^/a(.*)$ /b$1
""");

        Assert.DoesNotContain(sonuc.Diagnostics, t => t.Level == HtaccessDiagnosticLevel.Unsupported);
        Assert.DoesNotContain(sonuc.Diagnostics, t => t.Level == HtaccessDiagnosticLevel.Error);
    }

    [Fact]
    public void Uygulanmayan_blogun_ICINDEKI_direktifler_tek_tek_bildirilmez()
    {
        var sonuc = Ayristir("""
<IfDefine PRODUCTION>
    HicBoyleBirSeyYok a
    BaskaBirSacmalik b
</IfDefine>
""");

        // Blok için TEK bulgu; içeriden gürültü gelmemeli.
        var bulgu = Assert.Single(sonuc.Diagnostics);
        Assert.Equal(HtaccessDiagnosticCodes.UnsupportedSection, bulgu.Code);
        Assert.Equal("IfDefine", bulgu.Directive);
    }

    [Fact]
    public void Directory_blogu_Apache_nin_reddedecegini_soyleyerek_uygulanir()
    {
        var sonuc = Ayristir("<Directory /var/www>\nRequire all denied\n</Directory>");

        var bulgu = Assert.Single(sonuc.Diagnostics, t => t.Code == HtaccessDiagnosticCodes.ApacheWouldReject);
        Assert.Equal(HtaccessDiagnosticLevel.Warning, bulgu.Level);
        Assert.Contains("KABUL ETMEZ", bulgu.Message);
    }

    [Fact]
    public void Desteklenmeyen_bayrak_KURALI_DEVRE_DISI_birakir()
    {
        using var site = new HtaccessTestSite();
        site.Htaccess("""
RewriteEngine On
RewriteRule ^api/(.*)$ http://baska-sunucu/$1 [P]
RewriteRule ^api/(.*)$ /yerel.php?y=$1 [L]
""");

        var sonuc = site.Evaluate("/api/x");

        // [P] uygulanamıyor -> o kural düşer; SONRAKİ kural çalışır.
        Assert.Equal("/yerel.php", sonuc.RewrittenPath);
        Assert.Contains(sonuc.Diagnostics,
            t => t.Code == HtaccessDiagnosticCodes.UnsupportedFlag && t.Message.Contains("DEVRE DIŞI"));
    }

    [Fact]
    public void Bilinmeyen_sunucu_degiskeni_bos_doner_ve_bildirilir()
    {
        using var site = new HtaccessTestSite();
        site.Htaccess("""
RewriteEngine On
RewriteCond %{BOYLE_BIR_DEGISKEN_YOK} ^bir-sey$
RewriteRule ^a$ /b [L]
""");

        var sonuc = site.Evaluate("/a");

        Assert.Equal(HtaccessOutcome.None, sonuc.Outcome);
        Assert.Contains(sonuc.Diagnostics, t => t.Code == HtaccessDiagnosticCodes.UnsupportedVariable);
    }

    [Fact]
    public void RewriteEngine_yazilmamissa_kurallar_calismaz_ve_UYARILIR()
    {
        using var site = new HtaccessTestSite();
        site.Htaccess("RewriteRule ^a$ /b [L]");

        var sonuc = site.Evaluate("/a");

        Assert.Equal(HtaccessOutcome.None, sonuc.Outcome);
        Assert.Contains(sonuc.Diagnostics, t => t.Code == HtaccessDiagnosticCodes.RewriteEngineOff);
    }

    [Fact]
    public void Kuralsiz_kalan_RewriteCond_bildirilir()
    {
        using var site = new HtaccessTestSite();
        site.Htaccess("RewriteEngine On\nRewriteRule ^a$ /b [L]\nRewriteCond %{HTTP_HOST} ^x$\n");

        var sonuc = site.Evaluate("/a");

        Assert.Contains(sonuc.Diagnostics, t => t.Code == HtaccessDiagnosticCodes.CondWithoutRule);
    }

    [Fact]
    public void Eksik_argumanli_direktif_hata_uretir_ama_dosyayi_dusurmez()
    {
        var sonuc = Ayristir("RewriteEngine On\nRewriteRule ^tek-arguman\nRewriteRule ^a$ /b [L]");

        Assert.True(sonuc.Usable);
        Assert.Equal(3, sonuc.Directives.Count);
    }

    [Fact]
    public void UTF8_BOM_ilk_direktifi_bozmaz()
    {
        var sonuc = Ayristir("﻿RewriteEngine On");

        Assert.Equal("RewriteEngine", sonuc.Directives[0].Name);
        Assert.DoesNotContain(sonuc.Diagnostics, t => t.Code == HtaccessDiagnosticCodes.UnknownDirective);
    }
}
