using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Firewall;
using AdaPanel.Infrastructure.AppStore;
using AdaPanel.Infrastructure.Database;
using AdaPanel.Infrastructure.Dns;
using AdaPanel.Infrastructure.Docker;
using AdaPanel.Infrastructure.Firewall;
using AdaPanel.Infrastructure.Git;
using AdaPanel.Infrastructure.Ssl;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// ARGÜMAN / KOMUT ENJEKSİYONU DENETİMİ — panel LocalSystem olarak koşar, panel yöneticisi
/// Windows yöneticisi DEĞİLDİR. Bu dosyadaki her testin arkasında ÖLÇÜLMÜŞ bir yol vardır.
/// <para>
/// İki farklı sınıf karışmasın diye ayrımı burada bir kez yazıyorum:
/// </para>
/// <list type="number">
/// <item><b>PowerShell betiği içi:</b> <c>ProcessRunner.RunPowerShellAsync</c> bir BETİK
/// çalıştırır. Çift tırnaklı PowerShell dizesinde <c>$(...)</c> ÇALIŞIR; tek tırnaklıda
/// çalışmaz. Kaçış = <c>'</c> → <c>''</c>.</item>
/// <item><b>Komut satırı argümanı:</b> <c>ProcessRunner.RunAsync</c> KABUK KULLANMAZ
/// (<c>UseShellExecute = false</c>), yani <c>; | &amp;</c> anlamsızdır. Anlamlı olan BOŞLUK
/// (argüman böler), <c>"</c> (tırnak durumu) ve baştaki <c>-</c> (değer değil BAYRAK olur).
/// Tırnaklamak baştaki <c>-</c>'yi KAPATMAZ — tırnağı hedef süreç görmeden ayrıştırıcı soyar.</item>
/// </list>
/// </summary>
public sealed class ArgumentInjectionTests
{
    // --- win-acme (wacs.exe) argümanları: AcmeArgumentRules -------------------------------

    /// <summary>
    /// Denetimde yazılan gerçek yükler. Hepsinin ortak yanı BOŞLUK: wacs.exe'ye ikinci bir bayrak
    /// eklemenin tek yolu odur ve <c>--validation script --dnsscript &lt;yol&gt;</c> tanımı gereği
    /// harici bir program çalıştırır → SYSTEM.
    /// </summary>
    [Theory]
    [InlineData("ornek.com --validation script --dnsscript C:\\yuk.bat")]
    [InlineData("ornek.com --installation script --script C:\\Windows\\System32\\cmd.exe")]
    [InlineData("-installation")]           // değer BAYRAĞA benziyor
    [InlineData("ornek.com\tikinci")]       // sekme de boşluktur
    [InlineData("ornek com")]
    [InlineData("ornek.com\"")]
    [InlineData("ornek.com;calc")]
    [InlineData("ornek..com")]
    [InlineData("ornek.com.")]
    [InlineData("")]
    [InlineData("   ")]
    public void AcmeHost_komut_satirini_kiran_degerleri_reddeder(string host)
    {
        Assert.False(AcmeArgumentRules.IsCommandSafeHost(host));
    }

    /// <summary>
    /// Daraltma MEŞRU girdiyi kırmamalı. IDN (Türkçe harfli alan adı) panelde IIS bağlaması
    /// olarak meşrudur ve kendinden imzalı sertifika yoluna da giriyor; ASCII'ye kısmak o
    /// siteleri sertifikasız bırakırdı.
    /// </summary>
    [Theory]
    [InlineData("ornek.com")]
    [InlineData("www.ornek.com")]
    [InlineData("*.ornek.com")]
    [InlineData("panel-1.alt.ornek.com.tr")]
    [InlineData("örnek.com")]
    [InlineData("şirket.ünlü.com")]
    [InlineData("_acme-challenge.ornek.com")]
    [InlineData("10-1-2-3.nip.io")]
    public void AcmeHost_mesru_adlari_gecirir(string host)
    {
        Assert.True(AcmeArgumentRules.IsCommandSafeHost(host));
    }

    [Theory]
    [InlineData("admin@ornek.com --accepttos --script C:\\yuk.bat")]
    [InlineData("-emailaddress")]
    [InlineData("admin ornek.com")]
    [InlineData("admin@ornek.com\"")]
    [InlineData("adminornek.com")]          // @ yok
    [InlineData("admin@@ornek.com")]        // iki @
    [InlineData("admin@ornek")]             // alan adında nokta yok
    [InlineData("@ornek.com")]
    [InlineData("admin@")]
    [InlineData("")]
    public void AcmeEmail_komut_satirini_kiran_degerleri_reddeder(string email)
    {
        Assert.False(AcmeArgumentRules.IsCommandSafeEmail(email));
    }

    [Theory]
    [InlineData("admin@ornek.com")]
    [InlineData("ada.panel+ssl@ornek.com.tr")]
    [InlineData("ali_veli-1@alt.ornek.com")]
    public void AcmeEmail_mesru_adresleri_gecirir(string email)
    {
        Assert.True(AcmeArgumentRules.IsCommandSafeEmail(email));
    }

    /// <summary>
    /// SINIF KAPATMA: iki win-acme çağrı yerinde de değerler TIRNAKLI olmalı. Doğrulamanın yanında
    /// ikinci ve bağımsız katman; doğrulama bir gün gevşerse çıplak gömme geri gelmesin.
    /// </summary>
    [Fact]
    public void Winacme_cagrilarinda_host_ve_eposta_tirnakli()
    {
        foreach (var kaynak in new[]
                 {
                     OkuKaynak("src", "AdaPanel.Infrastructure", "Ssl", "WinAcmeSslService.cs"),
                     OkuKaynak("src", "AdaPanel.Infrastructure", "Mail", "MailService.cs")
                 })
        {
            Assert.DoesNotContain("--host {", kaynak, StringComparison.Ordinal);
            Assert.DoesNotContain("--emailaddress {", kaynak, StringComparison.Ordinal);
        }
    }

    // --- Windows Firewall kural adı ------------------------------------------------------

    private const string FirewallYuk = "AdaPanel $(net localgroup administrators hax /add)";

    /// <summary>
    /// Kural adı ÇİFT TIRNAKLI PowerShell dizesine giriyordu ve tek kaçış <c>"</c> → <c>`"</c>
    /// idi. Çift tırnaklı dizede <c>$(...)</c> ÇALIŞIR. Ad artık tek tırnaklı dizede.
    /// </summary>
    [Fact]
    public void Firewall_kural_adi_cift_tirnakli_baglamda_degil()
    {
        var upsert = FirewallService.BuildUpsertScript(FirewallYuk, "Allow", "TCP", 8890);
        var remove = FirewallService.BuildRemoveScript(FirewallYuk);

        // Ad upsert betiğinde İKİ yerde geçiyor (önce kaldır, sonra kur); biri unutulursa açık durur.
        Assert.Equal(2, SayHepsi(upsert, $"-DisplayName '{FirewallYuk}'"));
        Assert.Equal(1, SayHepsi(remove, $"-DisplayName '{FirewallYuk}'"));

        foreach (var betik in new[] { upsert, remove })
        {
            Assert.DoesNotContain($"-DisplayName \"{FirewallYuk}\"", betik, StringComparison.Ordinal);
            Assert.DoesNotContain("`\"", betik, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("AdaPanel'; calc.exe; '")]
    [InlineData("a'b")]
    public void Firewall_tek_tirnak_ikilenerek_kacilir(string ad)
    {
        Assert.Contains($"-DisplayName '{ad.Replace("'", "''")}'", FirewallService.BuildRemoveScript(ad), StringComparison.Ordinal);
        Assert.Contains($"-DisplayName '{ad.Replace("'", "''")}'", FirewallService.BuildUpsertScript(ad, "Block", "UDP", 53), StringComparison.Ordinal);
    }

    /// <summary>
    /// Kaçışın YANINDA beyaz liste de var (iki bağımsız katman). Doğrulama PowerShell'e HİÇ
    /// gitmeden çalışmalı: buraya düşen bir süreç çağrısı testi asmadan hata verir.
    /// </summary>
    [Theory]
    [InlineData("AdaPanel $(whoami)")]
    [InlineData("AdaPanel `whoami`")]
    [InlineData("AdaPanel'; calc; '")]
    [InlineData("AdaPanel \"x")]
    [InlineData("AdaPanel;calc")]
    [InlineData("AdaPanel|calc")]
    [InlineData("AdaPanel&calc")]
    [InlineData("AdaPanel\ncalc")]
    public async Task Firewall_beyaz_liste_kabuk_karakterlerini_reddeder(string ad)
    {
        var servis = new FirewallService(NullLogger<FirewallService>.Instance);
        var istek = new UpdateFirewallRuleRequest { Port = 8899, Protocol = "TCP", RuleName = ad };

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.AllowPortAsync(istek));
        Assert.Equal("firewall.invalid_rule_name", hata.MessageKey);
        Assert.Equal(400, hata.HttpStatusCode);

        var hata2 = await Assert.ThrowsAsync<AppException>(() => servis.BlockPortAsync(istek, 8890));
        Assert.Equal("firewall.invalid_rule_name", hata2.MessageKey);
    }

    /// <summary>
    /// Beyaz liste MEŞRU adı kırmamalı: panelin kendi ürettiği ad ve Türkçe harfli yönetici adları.
    /// Boş ad da serbest kalmalı — o durumda adı panel kendisi üretir.
    /// </summary>
    /// <summary>
    /// Beyaz liste MEŞRU adı kırmamalı: panelin kendi ürettiği ad ve Türkçe harfli yönetici adları.
    /// Boş ad da serbest kalmalı — o durumda adı panel kendisi üretir.
    /// <para>
    /// ÖLÇÜM YÖNTEMİ: burada servis ÇAĞRILMIYOR. Kabul yolunu servis üzerinden ölçmek, bu makinede
    /// GERÇEK bir güvenlik duvarı kuralı yazmak demek olurdu — testin yan etkisi olmamalı.
    /// Reddetme yolu servis üzerinden ölçülüyor (aşağıdaki test), çünkü orada doğrulama
    /// PowerShell'e ULAŞMADAN fırlatır; zaten ölçtüğümüz şey de tam olarak budur.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("AdaPanel Allow TCP 8890")]
    [InlineData("Şirket İç Ağ 3389")]
    [InlineData("ada_panel-web.1")]
    public void Firewall_mesru_adlar_serbest_kalir(string ad)
    {
        Assert.True(FirewallService.KuralAdiGuvenli(ad));
    }

    /// <summary>
    /// SINIF KAPATMA: tehlikeli kaçış fonksiyonu kaynaktan TAMAMEN kalkmalı. Geri gelirse yeni bir
    /// çağrı yerinde açık sessizce yeniden doğar.
    /// </summary>
    [Fact]
    public void Tehlikeli_kacis_fonksiyonu_kaynakta_kalmadi()
    {
        foreach (var yol in new[]
                 {
                     new[] { "src", "AdaPanel.Infrastructure", "Firewall", "FirewallService.cs" },
                     new[] { "src", "AdaPanel.Infrastructure", "Security", "BruteForceProtectionService.cs" }
                 })
        {
            var kaynak = OkuKaynak(yol);
            // "(" ile arıyoruz: tanım ve çağrı kalmamalı. Ad, neden kaldırıldığını anlatan
            // yorumlarda geçmeye devam ediyor — o kasıtlı.
            Assert.DoesNotContain("EscapePowerShellString(", kaynak, StringComparison.Ordinal);
            Assert.DoesNotContain("-DisplayName \"{", kaynak, StringComparison.Ordinal);
        }
    }

    // --- git depo adresi -----------------------------------------------------------------

    /// <summary>
    /// <c>ext::</c> git'in UZANTI TAŞIYICISIDIR ve tanımı gereği "bu komutu çalıştır" demektir.
    /// Eski doğrulama "içinde @ ve : var" diyordu; bu yükler o kuralı sağlıyordu.
    /// </summary>
    [Theory]
    [InlineData("ext::sh -c \"whoami>C:/x\" @ :")]
    [InlineData("ext::sh@host:x")]
    [InlineData("transport::git@host:x/y")]
    [InlineData("--upload-pack=C:\\yuk.bat")]
    [InlineData("-u git@host:x/y.git")]
    // BU SATIR BAŞTAKİ TİRE KONTROLÜNÜ TEK BAŞINA ÖLÇER: scp kalıbındaki ilk karakter sınıfı
    // (`[A-Za-z0-9._-]`) tireyi İÇERİR, yani biçim beyaz listesi bunu KABUL EDER. Mutasyonla
    // doğrulandı: tire kontrolü kapatılınca yalnız bu satır kırmızıya döner.
    [InlineData("-u@host:yuk")]
    [InlineData("https://ornek.com/x.git --config=core.fsmonitor=calc")]
    [InlineData("file:///C:/gizli")]
    [InlineData("git@host:x/y.git\ncalc")]
    [InlineData("")]
    public void Git_depo_adresi_tehlikeli_bicimleri_reddeder(string url)
    {
        var hata = Assert.Throws<AppException>(() => GitDeploymentService.ValidateRepositoryUrl(url));
        Assert.Equal("git.invalid_repository", hata.MessageKey);
    }

    [Theory]
    [InlineData("https://github.com/ada/panel.git")]
    [InlineData("http://gitlab.ornek.com/ada/panel.git")]
    [InlineData("https://kullanici:token@github.com/ada/panel.git")]
    [InlineData("git@github.com:ada/panel.git")]
    [InlineData("git@github.com:22:ada/panel.git")]
    [InlineData("ssh://git@github.com:22/ada/panel.git")]
    public void Git_mesru_depo_adresleri_gecer(string url)
    {
        GitDeploymentService.ValidateRepositoryUrl(url);
    }

    /// <summary>
    /// İKİNCİ KATMAN: doğrulama yalnız ConfigureAsync'te koşuyor, DeployAsync diskteki kaydı
    /// yeniden ölçmüyor. git'in kendi taşıyıcı beyaz listesi kararı transport katmanına taşır.
    /// </summary>
    [Fact]
    public void Git_calistirmasi_tasiyici_beyaz_listesi_veriyor()
    {
        var kaynak = OkuKaynak("src", "AdaPanel.Infrastructure", "Git", "GitDeploymentService.cs");
        Assert.Contains("[\"GIT_ALLOW_PROTOCOL\"] = \"http:https:ssh\"", kaynak, StringComparison.Ordinal);
    }

    // --- DNS kayıt değeri ----------------------------------------------------------------

    /// <summary>
    /// 252'den uzun bir TXT değeri, sonunda bir <c>.cmd</c> DOSYASINA yazılıp çalıştırılıyor.
    /// Değerdeki satır sonu orada İKİNCİ BİR KOMUT SATIRI olur. Uç Reseller'a açık.
    /// </summary>
    [Theory]
    [InlineData("TXT")]
    [InlineData("A")]
    [InlineData("CNAME")]
    public void Dns_kayit_degerinde_denetim_karakteri_reddedilir(string tip)
    {
        var yuk = new string('a', 300) + "\r\nnet localgroup administrators hax /add";
        var hata = Assert.Throws<AppException>(() => DnsService.ValidateRecordValue(tip, yuk, null));
        Assert.Equal("dns.invalid_record_value", hata.MessageKey);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\t")]
    [InlineData("\u0000")]
    public void Dns_txt_degerinde_her_denetim_karakteri_reddedilir(string karakter)
    {
        Assert.Throws<AppException>(() => DnsService.ValidateRecordValue("TXT", "v=spf1" + karakter + "-all", null));
    }

    /// <summary>Meşru TXT değerleri (SPF, DKIM, uzun anahtarlar) geçmeye devam etmeli.</summary>
    [Theory]
    [InlineData("v=spf1 include:_spf.ornek.com ~all")]
    [InlineData("v=DMARC1; p=quarantine; rua=mailto:dmarc@ornek.com")]
    public void Dns_mesru_txt_degerleri_gecer(string deger)
    {
        DnsService.ValidateRecordValue("TXT", deger, null);
    }

    [Fact]
    public void Dns_uzun_dkim_degeri_gecer()
    {
        // 300 karakterlik bir DKIM p= değeri: uzun TXT yolunu tetikler ve MEŞRUDUR.
        DnsService.ValidateRecordValue("TXT", "v=DKIM1; k=rsa; p=" + new string('A', 300), null);
    }

    /// <summary>
    /// Senkron betiğindeki hata etiketi ÇİFT TIRNAKLI dizedeydi ve <c>EscapePowerShellLiteral</c>
    /// (yalnız <c>'</c> ikiler) ile kaçırılıyordu — yani <c>$(...)</c> hiç kapatılmıyordu. Şablon
    /// yolu Host'u NormalizeRecord'a hiç uğratmadığı için ulaşılabilirdi.
    /// </summary>
    [Fact]
    public void Dns_senkron_hata_etiketi_cift_tirnakli_degil()
    {
        var kaynak = OkuKaynak("src", "AdaPanel.Infrastructure", "Dns", "DnsService.cs");
        Assert.DoesNotContain("$basarisiz += \\\"{etiket}", kaynak, StringComparison.Ordinal);
        Assert.Contains("$basarisiz += '{etiket}'", kaynak, StringComparison.Ordinal);
    }

    // --- mysqldump kaynak host / kullanıcı ------------------------------------------------

    /// <summary>
    /// <c>SourceHost</c> ve <c>SourceUserName</c> mysqldump.exe'nin komut satırına giriyor ve
    /// yalnız <c>Trim</c> görüyordu (aynı metotta <c>SourceDatabase</c> normalize ediliyordu).
    /// </summary>
    [Theory]
    [InlineData("1.2.3.4 --defaults-extra-file=C:\\Users\\Public\\kotu.cnf")]
    [InlineData("-h1.2.3.4")]           // BAYRAĞA benziyor; tire karakter sınıfının İÇİNDE olduğu
                                        // için bu satırı yalnız ayrı tire kontrolü yakalar
    [InlineData("host\"x")]
    [InlineData("host;calc")]
    [InlineData("")]
    public void Db_kaynak_host_komut_satirini_kiran_degerleri_reddeder(string host)
    {
        Assert.False(DatabaseService.IsCommandSafeTransferHost(host));
    }

    [Theory]
    [InlineData("db.ornek.com")]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    [InlineData("mysql-1_yedek.ic")]
    public void Db_mesru_kaynak_hostlari_gecer(string host)
    {
        Assert.True(DatabaseService.IsCommandSafeTransferHost(host));
    }

    [Theory]
    [InlineData("root\" --defaults-extra-file=\"C:\\kotu.cnf")]
    [InlineData("root x")]
    [InlineData("-uroot")]
    [InlineData("")]
    public void Db_kaynak_kullanicisi_komut_satirini_kiran_degerleri_reddeder(string kullanici)
    {
        Assert.False(DatabaseService.IsCommandSafeTransferUser(kullanici));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("ada_panel-1.yedek")]
    public void Db_mesru_kaynak_kullanicilari_gecer(string kullanici)
    {
        Assert.True(DatabaseService.IsCommandSafeTransferUser(kullanici));
    }

    // --- Windows argüman tırnaklaması (docker -e) ----------------------------------------

    /// <summary>
    /// <c>Replace("\"", "\\\"")</c> Windows'ta YANLIŞTIR: girdideki <c>\"</c> dizisi <c>\\"</c>
    /// olur ve ayrıştırıcı tırnağı KAPATIR — sonrası yeni argüman. Doğru kural: tırnaktan (ve
    /// kapanış tırnağından) önceki ters bölüler İKİLENİR.
    /// </summary>
    [Theory]
    [InlineData("A=x", "\"A=x\"")]
    [InlineData("A=bosluk var", "\"A=bosluk var\"")]
    [InlineData("A=C:\\yol\\", "\"A=C:\\yol\\\\\"")]            // sondaki \ ikilenir
    [InlineData("A=x\"y", "\"A=x\\\"y\"")]
    [InlineData("A=x\\\"y", "\"A=x\\\\\\\"y\"")]                // \" → \\\" (ESKİ HÂLİ BURADA KIRILIYORDU)
    [InlineData("A=C:\\a\\b", "\"A=C:\\a\\b\"")]                // tırnaksız ters bölü ikilenmez
    public void WindowsArgument_tirnaklamasi_dogru(string girdi, string beklenen)
    {
        Assert.Equal(beklenen, WindowsArgument.Quote(girdi));
    }

    /// <summary>
    /// TERSİNE ÖLÇÜM: tırnaklanan değer, Windows'un KENDİ ayrıştırıcısından geçince TEK argüman
    /// olarak ve BOZULMADAN geri gelmeli. Elle yazılmış bir beklenen dizeyi doğrulamak yetmez —
    /// beklenen dizenin kendisi yanlış olabilir.
    /// </summary>
    [Theory]
    [InlineData("A=x")]
    [InlineData("A=bosluk var")]
    [InlineData("A=C:\\yol\\")]
    [InlineData("A=x\"y")]
    [InlineData("A=x\\\"y --privileged -v C:\\:/host")]
    [InlineData("A=\"")]
    [InlineData("A=\\\\sunucu\\pay\\")]
    public void WindowsArgument_ayristirici_tek_argumana_geri_cozer(string girdi)
    {
        var parcalar = CommandLineToArgv(WindowsArgument.Quote(girdi));

        Assert.Single(parcalar);
        Assert.Equal(girdi, parcalar[0]);
    }

    [Fact]
    public void Docker_ortam_degiskeni_dogru_tirnaklaniyor()
    {
        var kaynak = OkuKaynak("src", "AdaPanel.Infrastructure", "Docker", "DockerService.cs");
        Assert.DoesNotContain("variable.Replace(\"\\\"\", \"\\\\\\\"\")", kaynak, StringComparison.Ordinal);
        Assert.Contains("WindowsArgument.Quote(variable)", kaynak, StringComparison.Ordinal);
    }

    // --- PHP tek tırnak kaçışı (Roundcube sqlite init) -----------------------------------

    /// <summary>
    /// Kiracının site fiziksel yolu <c>php.exe -r "..."</c> içine PHP KAYNAĞI olarak giriyordu.
    /// NTFS'te <c>'</c> meşru bir yol karakteridir; PHP dizesi kapanıp yeni ifade eklenebiliyordu
    /// ve o php.exe LocalSystem olarak koşuyor.
    /// </summary>
    [Theory]
    [InlineData("C:/site/temp/roundcube.db", "'C:/site/temp/roundcube.db'")]
    [InlineData("C:/si'te/x.db", "'C:/si\\'te/x.db'")]
    [InlineData("C:\\site\\x.db", "'C:\\\\site\\\\x.db'")]
    [InlineData("'); system('calc'); ('", "'\\'); system(\\'calc\\'); (\\''")]
    public void Php_tek_tirnak_kacisi_dogru(string girdi, string beklenen)
    {
        Assert.Equal(beklenen, AppStoreService.PhpTekTirnak(girdi));
    }

    [Fact]
    public void Roundcube_sqlite_baslatmasi_yolu_php_kaynagina_gommuyor()
    {
        var kaynak = OkuKaynak("src", "AdaPanel.Infrastructure", "AppStore", "AppStoreService.cs");
        Assert.DoesNotContain("new PDO('sqlite:{", kaynak, StringComparison.Ordinal);
        Assert.DoesNotContain("file_get_contents('{", kaynak, StringComparison.Ordinal);
    }

    // --- webmail bağlaması ----------------------------------------------------------------

    /// <summary>
    /// <c>-HostHeader '{webmailHost}'</c> kaçışsızdı; aynı dosyada doğrusu (KeepAppPoolWarmAsync)
    /// zaten yazılıydı. Tek tırnaklı dizede genişleme yok ama tek tırnağın KENDİSİ dizeyi kapatır.
    /// </summary>
    [Fact]
    public void Webmail_host_basligi_kacirilmis()
    {
        var kaynak = OkuKaynak("src", "AdaPanel.Infrastructure", "Mail", "WebmailService.cs");
        Assert.DoesNotContain("-HostHeader '{{webmailHost}}'", kaynak, StringComparison.Ordinal);
        Assert.Contains("webmailHost.Replace(\"'\", \"''\")", kaynak, StringComparison.Ordinal);
    }

    // --- düzenek ---------------------------------------------------------------------------

    /// <summary>
    /// Windows'un GERÇEK argüman ayrıştırıcısı. Beklenen dizeyi elle yazmak yerine tersine
    /// çözüyoruz — bu depoda "testin var olması bir şey ölçtüğü anlamına gelmez" dersi var.
    /// </summary>
    private static string[] CommandLineToArgv(string arguments)
    {
        // İlk belirteç program adı sayılır; ayrıştırıcı onu farklı kurallarla okur. Başa sabit bir
        // ad koyup sonucu atıyoruz ki ölçtüğümüz şey gerçekten BİZİM argümanımız olsun.
        var satir = "ada.exe " + arguments;
        var isaretci = NativeMethods.CommandLineToArgvW(satir, out var adet);
        Assert.NotEqual(IntPtr.Zero, isaretci);
        try
        {
            var hepsi = new string[adet];
            for (var i = 0; i < adet; i++)
            {
                var eleman = System.Runtime.InteropServices.Marshal.ReadIntPtr(isaretci, i * IntPtr.Size);
                hepsi[i] = System.Runtime.InteropServices.Marshal.PtrToStringUni(eleman) ?? string.Empty;
            }

            return hepsi[1..];
        }
        finally
        {
            NativeMethods.LocalFree(isaretci);
        }
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr LocalFree(IntPtr hMem);
    }

    private static int SayHepsi(string metin, string parca)
    {
        var adet = 0;
        var index = 0;
        while ((index = metin.IndexOf(parca, index, StringComparison.Ordinal)) >= 0)
        {
            adet++;
            index += parca.Length;
        }

        return adet;
    }

    private static string OkuKaynak(params string[] parcalar)
    {
        var kok = AppContext.BaseDirectory;
        while (kok is not null && !File.Exists(Path.Combine(kok, "AdaPanel.sln")))
        {
            kok = Path.GetDirectoryName(kok);
        }

        Assert.NotNull(kok);
        return File.ReadAllText(Path.Combine([kok!, .. parcalar]));
    }
}
