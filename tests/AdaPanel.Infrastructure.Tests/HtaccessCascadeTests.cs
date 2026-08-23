using AdaPanel.Core.Dtos.Htaccess;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Dizin BASAMAKLANDIRMASI: kökten istenen dizine kadar her seviye sırayla uygulanır.
/// </summary>
/// <remarks>
/// Buradaki en kritik davranış, sezgiye AYKIRI olanıdır: mod_rewrite kuralları BİRİKMEZ,
/// alt dizin üstünkileri tamamen DEĞİŞTİRİR (Apache <c>config_perdir_merge</c>, varsayılan
/// <c>RewriteOptions</c>). Diğer direktifler ise anahtar bazında birleşir. Bu ayrımı yanlış
/// yapmak, WordPress kökünün kurallarının alt dizindeki bir uygulamayla çakışması demektir.
/// </remarks>
public sealed class HtaccessCascadeTests
{
    [Fact]
    public void Alt_dizindeki_kurallar_ust_dizinin_kurallarini_TAMAMEN_ezer()
    {
        using var site = new HtaccessTestSite();
        site.Htaccess("""
RewriteEngine On
RewriteRule ^(.*)$ /kok.php [L]
""");
        site.Write("magaza/.htaccess", """
RewriteEngine On
RewriteRule ^(.*)$ /magaza/urun.php [L]
""");

        // Kök kuralı alt dizinde ÇALIŞMAMALI.
        Assert.Equal("/magaza/urun.php", site.Evaluate("/magaza/ayakkabi").RewrittenPath);
        // Kökte ise kök kuralı geçerli.
        Assert.Equal("/kok.php", site.Evaluate("/hakkimizda").RewrittenPath);
    }

    [Fact]
    public void Alt_dizinde_HIC_kural_yoksa_ust_dizinin_kurallari_gecerlidir()
    {
        using var site = new HtaccessTestSite();
        site.Htaccess("""
RewriteEngine On
RewriteCond %{REQUEST_FILENAME} !-f
RewriteRule . /index.php [L]
""");
        site.Write("index.php", "<?php");
        // Alt dizinde yalnızca yeniden yazmayla ilgisi olmayan bir direktif var.
        site.Write("blog/.htaccess", "Options -Indexes\n");

        var sonuc = site.Evaluate("/blog/yazi-1");

        Assert.Equal("/index.php", sonuc.RewrittenPath);
        Assert.False(sonuc.Options.Indexes);
    }

    [Fact]
    public void Alt_dizindeki_kural_dizin_on_eki_soyulmus_yolla_eslesir()
    {
        using var site = new HtaccessTestSite();
        site.Write("blog/.htaccess", """
RewriteEngine On
RewriteRule ^yazi-([0-9]+)$ /blog/goster.php?id=$1 [L]
""");

        var sonuc = site.Evaluate("/blog/yazi-42");

        Assert.Equal("/blog/goster.php", sonuc.RewrittenPath);
        Assert.Equal("id=42", sonuc.RewrittenQuery);
    }

    [Fact]
    public void RewriteBase_goreli_hedefin_onune_konur()
    {
        using var site = new HtaccessTestSite();
        site.Write("uygulama/.htaccess", """
RewriteEngine On
RewriteBase /uygulama/
RewriteCond %{REQUEST_FILENAME} !-f
RewriteRule ^ index.php [L]
""");
        site.Write("uygulama/index.php", "<?php");

        var sonuc = site.Evaluate("/uygulama/panel/ayarlar");

        Assert.Equal("/uygulama/index.php", sonuc.RewrittenPath);
    }

    [Fact]
    public void RewriteBase_yoksa_dizinin_kendi_on_eki_kullanilir()
    {
        using var site = new HtaccessTestSite();
        site.Write("uygulama/.htaccess", """
RewriteEngine On
RewriteCond %{REQUEST_FILENAME} !-f
RewriteRule ^ index.php [L]
""");
        site.Write("uygulama/index.php", "<?php");

        var sonuc = site.Evaluate("/uygulama/panel");

        Assert.Equal("/uygulama/index.php", sonuc.RewrittenPath);
    }

    [Fact]
    public void Ust_dizinin_kurali_alt_dizinde_calisirken_KENDI_on_ekini_kullanir()
    {
        using var site = new HtaccessTestSite();
        // Kural KÖKTE tanımlı; ön eki "/". Alt dizindeki istekte de aynı ön ek geçerli olmalı.
        site.Htaccess("""
RewriteEngine On
RewriteRule ^(.+)/(.+)$ /yakalandi.php?a=$1&b=$2 [L]
""");
        site.Mkdir("blog");

        var sonuc = site.Evaluate("/blog/yazi");

        Assert.Equal("/yakalandi.php", sonuc.RewrittenPath);
        Assert.Equal("a=blog&b=yazi", sonuc.RewrittenQuery);
    }

    [Fact]
    public void Hata_belgeleri_ve_MIME_eslemeleri_anahtar_bazinda_BIRLESIR()
    {
        using var site = new HtaccessTestSite();
        site.Htaccess("""
ErrorDocument 404 /kok-404.html
ErrorDocument 500 /kok-500.html
AddType text/plain .log
""");
        site.Write("alt/.htaccess", """
ErrorDocument 404 /alt-404.html
AddType application/json .jsonl
""");

        var sonuc = site.Evaluate("/alt/sayfa.html");

        Assert.Equal("/alt-404.html", sonuc.ErrorDocuments[404]); // alt ezer
        Assert.Equal("/kok-500.html", sonuc.ErrorDocuments[500]); // üstten miras
        Assert.Equal("text/plain", sonuc.MimeTypes[".log"]);
        Assert.Equal("application/json", sonuc.MimeTypes[".jsonl"]);
    }

    [Fact]
    public void Header_satirlari_BIRIKIR()
    {
        using var site = new HtaccessTestSite();
        site.Htaccess("Header set X-Kok 1\n");
        site.Write("alt/.htaccess", "Header set X-Alt 2\n");

        var sonuc = site.Evaluate("/alt/sayfa.html");

        Assert.Contains(sonuc.Headers, h => h.Name == "X-Kok");
        Assert.Contains(sonuc.Headers, h => h.Name == "X-Alt");
    }

    [Fact]
    public void DirectoryIndex_alt_dizinde_tamamen_degistirilir()
    {
        using var site = new HtaccessTestSite();
        site.Htaccess("DirectoryIndex index.php index.html\n");
        site.Write("belgeler/.htaccess", "DirectoryIndex baslangic.html\n");

        Assert.Equal(["baslangic.html"], site.Evaluate("/belgeler/").DirectoryIndex);
        Assert.Equal(["index.php", "index.html"], site.Evaluate("/").DirectoryIndex);
    }

    [Fact]
    public void Options_isaretli_yazildiginda_mirasi_DEGISTIRIR_isaretsizde_SIFIRLAR()
    {
        using var site = new HtaccessTestSite();
        site.Htaccess("Options -Indexes +FollowSymLinks\n");

        site.Write("a/.htaccess", "Options +Indexes\n");
        var isaretli = site.Evaluate("/a/x.html");
        Assert.True(isaretli.Options.Indexes);
        Assert.True(isaretli.Options.FollowSymLinks); // miras korunur

        site.Write("b/.htaccess", "Options Indexes\n");
        var isaretsiz = site.Evaluate("/b/x.html");
        Assert.True(isaretsiz.Options.Indexes);
        Assert.False(isaretsiz.Options.FollowSymLinks); // miras SIFIRLANIR
    }

    [Fact]
    public void Erisim_kurallari_alt_dizinde_tamamen_degistirilir()
    {
        using var site = new HtaccessTestSite();
        site.Htaccess("Require all denied\n");
        site.Write("acik/.htaccess", "Require all granted\n");

        Assert.Equal(HtaccessOutcome.AccessDenied, site.Evaluate("/kapali.html").Outcome);
        Assert.Equal(HtaccessOutcome.None, site.Evaluate("/acik/serbest.html").Outcome);
    }

    [Fact]
    public void Uygulanan_dosyalar_KOKTEN_ICERIYE_dogru_raporlanir()
    {
        using var site = new HtaccessTestSite();
        site.Htaccess("Options -Indexes\n");
        site.Write("a/.htaccess", "Options -Indexes\n");
        site.Write("a/b/.htaccess", "Options -Indexes\n");

        var sonuc = site.Evaluate("/a/b/sayfa.html");

        Assert.Equal(3, sonuc.AppliedFiles.Count);
        Assert.EndsWith(Path.Combine(site.Root, ".htaccess"), sonuc.AppliedFiles[0]);
        Assert.EndsWith(Path.Combine("a", ".htaccess"), sonuc.AppliedFiles[1]);
        Assert.EndsWith(Path.Combine("a", "b", ".htaccess"), sonuc.AppliedFiles[2]);
    }

    [Fact]
    public void Inspect_bulunan_butun_dosyalari_ve_desteklenmeyenleri_raporlar()
    {
        using var site = new HtaccessTestSite();
        site.Htaccess("RewriteEngine On\nRewriteRule ^a$ /b [L]\nphp_value memory_limit 256M\n");
        site.Write("alt/.htaccess", "HicBoyleBirSeyYok x\n");

        var rapor = site.Engine.Inspect(site.Root, "alt");

        Assert.Equal(2, rapor.Files.Count);
        Assert.True(rapor.RewriteEngineEnabled);
        Assert.Equal(1, rapor.RewriteRuleCount);
        Assert.Contains(rapor.Unsupported, t => t.Directive == "php_value");
        Assert.Contains(rapor.Unsupported, t => t.Directive == "HicBoyleBirSeyYok");
        Assert.False(rapor.HasFatal);
    }

    [Fact]
    public void Olumcul_hatali_dosya_UYGULANMAZ_ama_digerleri_uygulanir()
    {
        using var site = new HtaccessTestSite();
        site.Htaccess("Options -Indexes\nErrorDocument 404 /kok.html\n");
        site.Write("bozuk/.htaccess", "<IfModule mod_rewrite.c>\nErrorDocument 404 /bozuk.html\n");

        var sonuc = site.Evaluate("/bozuk/sayfa.html");

        Assert.Equal("/kok.html", sonuc.ErrorDocuments[404]);
        Assert.Contains(sonuc.Diagnostics,
            t => t is { Level: HtaccessDiagnosticLevel.Fatal, Code: HtaccessDiagnosticCodes.UnclosedSection });
        Assert.DoesNotContain(sonuc.AppliedFiles, y => y.Contains("bozuk"));
    }

    [Fact]
    public void Dosya_degisince_onbellek_kendiliginden_duser()
    {
        using var site = new HtaccessTestSite();
        var yol = site.Htaccess("ErrorDocument 404 /once.html");
        Assert.Equal("/once.html", site.Evaluate("/x.html").ErrorDocuments[404]);

        // Damga (son yazma zamanı + boyut) değiştiği için InvalidateCache olmadan da düşmeli.
        File.WriteAllText(yol, "ErrorDocument 404 /sonra-degisti.html");
        File.SetLastWriteTimeUtc(yol, DateTime.UtcNow.AddSeconds(5));

        Assert.Equal("/sonra-degisti.html", site.Evaluate("/x.html").ErrorDocuments[404]);
    }
}
