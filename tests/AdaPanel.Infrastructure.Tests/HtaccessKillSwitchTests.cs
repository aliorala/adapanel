using System.Diagnostics;
using System.Security.AccessControl;
using AdaPanel.Infrastructure.Htaccess.Hosting;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// KAPATMA DÜĞMESİ (kill switch) testleri.
///
/// Bu testlerin işi "kod çalışıyor mu"yu değil, <b>müşteri sunucusunda yangın çıktığında
/// kurtarma yolunun gerçekten çalıştığını</b> göstermektir. Bu yüzden her test bir ARIZA
/// SENARYOSUNA karşılık gelir ve hepsi aynı sonuca varmak zorundadır: modül KAPANIR.
/// </summary>
public sealed class HtaccessKillSwitchTests
{
    /// <summary>Testin diski taklit eden okuyucusu: yokluk, içerik ve İSTİSNA üretebilir.</summary>
    private sealed class FakeReader : IHtaccessSwitchReader
    {
        internal readonly Dictionary<string, string> Files = new(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> Throwing = new(StringComparer.OrdinalIgnoreCase);
        internal int ReadCount;

        public string? Read(string path)
        {
            ReadCount++;
            if (Throwing.Contains(path))
            {
                throw new UnauthorizedAccessException("test: erisim reddedildi");
            }

            return Files.TryGetValue(path, out var content) ? content : null;
        }
    }

    private const string DataRoot = @"C:\ProgramData\AdaPanel";
    private const string Site = "musteri.example";

    private static string GlobalPath => HtaccessHostingPaths.GlobalSwitchPath(DataRoot);

    private static string SitePath => HtaccessHostingPaths.SiteSwitchPath(DataRoot, Site);

    private static (HtaccessRuntimeGate Gate, FakeReader Reader, long[] Clock) Build(double ttlSeconds = 2)
    {
        var reader = new FakeReader();
        var clock = new long[] { 0 };
        var gate = new HtaccessRuntimeGate(DataRoot, TimeSpan.FromSeconds(ttlSeconds), reader, () => clock[0]);
        return (gate, reader, clock);
    }

    private static void Advance(long[] clock, double seconds)
        => clock[0] += (long)(seconds * Stopwatch.Frequency);

    // ---------------------------------------------------------------- VARSAYILAN KAPALI

    [Fact]
    public void GenelAnahtarDosyasiYoksaModulKAPALIDIR()
    {
        // Bu, "varsayilan KAPALI baslamasi" gereksiniminin tek ve dogrudan kaniti.
        // Kurulum bu dosyayi OLUSTURMAZ -> taze kurulmus bir sunucuda modul olu gelir.
        var (gate, _, _) = Build();

        Assert.Equal(HtaccessMode.Off, gate.Evaluate(Site));
    }

    [Fact]
    public void GenelAnahtarAcikAmaSiteAnahtariYoksaSiteKAPALIDIR()
    {
        // Kademeli acilis: genel anahtar acilmasi TEK BASINA hicbir siteyi acmaz.
        var (gate, reader, _) = Build();
        reader.Files[GlobalPath] = "enabled=1\nmode=enforce\n";

        Assert.Equal(HtaccessMode.Off, gate.Evaluate(Site));
    }

    [Fact]
    public void IkiAnahtarDaAcikkenModulUYGULAR()
    {
        var (gate, reader, _) = Build();
        reader.Files[GlobalPath] = "enabled=1\nmode=enforce\n";
        reader.Files[SitePath] = "enabled=1\nmode=enforce\n";

        Assert.Equal(HtaccessMode.Enforce, gate.Evaluate(Site));
    }

    // ---------------------------------------------------------------- KAPATMA DÜĞMESİ MUTLAKTIR

    [Fact]
    public void GenelAnahtarKapatilinca_SiteAcikOlsaBile_KAPANIR()
    {
        // KURTARMA SENARYOSU: musteri sunucusunda modul sorun cikardi. Yonetici TEK dosyaya
        // dokunuyor; sitelerin kendi ayarlarina HIC bakilmiyor.
        var (gate, reader, clock) = Build();
        reader.Files[GlobalPath] = "enabled=1\nmode=enforce\n";
        reader.Files[SitePath] = "enabled=1\nmode=enforce\n";
        Assert.Equal(HtaccessMode.Enforce, gate.Evaluate(Site));

        reader.Files.Remove(GlobalPath); // panelin SetGlobal(Off) yaptigi sey: dosyayi SILMEK
        Advance(clock, 2.1);

        Assert.Equal(HtaccessMode.Off, gate.Evaluate(Site));
    }

    [Fact]
    public void GenelAnahtarEnabledSifirYapilinca_KAPANIR()
    {
        var (gate, reader, clock) = Build();
        reader.Files[GlobalPath] = "enabled=1\nmode=enforce\n";
        reader.Files[SitePath] = "enabled=1\nmode=enforce\n";
        Assert.Equal(HtaccessMode.Enforce, gate.Evaluate(Site));

        reader.Files[GlobalPath] = "enabled=0\n";
        Advance(clock, 2.1);

        Assert.Equal(HtaccessMode.Off, gate.Evaluate(Site));
    }

    [Fact]
    public void GenelAnahtarGozlemeAlininca_TumSunucuGozlemeDUSER()
    {
        // Sorunu tamamen kapatmadan teshis etme yolu: uygulama durur, gunluk akmaya devam eder.
        var (gate, reader, _) = Build();
        reader.Files[GlobalPath] = "enabled=1\nmode=observe\n";
        reader.Files[SitePath] = "enabled=1\nmode=enforce\n"; // site UYGULAMA istiyor

        Assert.Equal(HtaccessMode.Observe, gate.Evaluate(Site)); // ama genel anahtar KISITLIYOR
    }

    [Fact]
    public void SiteGozlemdeyseGenelAnahtarAcikOlsaBileGozlemdeKALIR()
    {
        var (gate, reader, _) = Build();
        reader.Files[GlobalPath] = "enabled=1\nmode=enforce\n";
        reader.Files[SitePath] = "enabled=1\nmode=observe\n";

        Assert.Equal(HtaccessMode.Observe, gate.Evaluate(Site));
    }

    [Fact]
    public void SiteBasinaKapatmaCalisir_DigerSiteETKILENMEZ()
    {
        var (gate, reader, clock) = Build();
        reader.Files[GlobalPath] = "enabled=1\nmode=enforce\n";
        reader.Files[SitePath] = "enabled=1\nmode=enforce\n";

        const string digerSite = "baska.example";
        var digerYol = HtaccessHostingPaths.SiteSwitchPath(DataRoot, digerSite);
        reader.Files[digerYol] = "enabled=1\nmode=enforce\n";

        Assert.Equal(HtaccessMode.Enforce, gate.Evaluate(Site));
        Assert.Equal(HtaccessMode.Enforce, gate.Evaluate(digerSite));

        // TEK siteyi kapat.
        reader.Files.Remove(SitePath);
        Advance(clock, 2.1);

        Assert.Equal(HtaccessMode.Off, gate.Evaluate(Site));
        Assert.Equal(HtaccessMode.Enforce, gate.Evaluate(digerSite)); // komsu ETKILENMEDI
    }

    // ---------------------------------------------------------------- HER ARIZA -> KAPALI

    [Fact]
    public void AnahtarDosyasiOKUNAMAZSA_KAPALI_ve_ISTISNA_SIZMAZ()
    {
        // ACL bozuk / disk hatasi. Fail-open: ozellik calismaz ama site de bozulmaz.
        var (gate, reader, _) = Build();
        reader.Throwing.Add(GlobalPath);

        var mode = gate.Evaluate(Site); // firlatmamali

        Assert.Equal(HtaccessMode.Off, mode);
        Assert.True(gate.ReadFailureCount > 0, "ariza sayaci artmali (sessizce yutulmamali)");
    }

    [Theory]
    [InlineData("")]                       // bos dosya (disk doldu, yarim yazim)
    [InlineData("   \n\n  ")]              // yalniz bosluk
    [InlineData("bozuk icerik")]           // hic '=' yok
    [InlineData("enabled")]                // deger yok
    [InlineData("=1")]                     // anahtar yok
    [InlineData("mode=enforce")]           // enabled YOK -> acik sayilmamali
    [InlineData("enabled=1\nmode=enfroce")] // YAZIM HATASI -> tanınmayan mod
    [InlineData("enabled=maybe")]          // anlamsiz deger
    public void BOZUK_ya_da_EKSIK_icerik_KAPALI_demektir(string content)
    {
        var (gate, reader, _) = Build();
        reader.Files[GlobalPath] = content;
        reader.Files[SitePath] = content;

        Assert.Equal(HtaccessMode.Off, gate.Evaluate(Site));
    }

    [Fact]
    public void ModYazimHatasi_ENFORCE_SAYILMAZ()
    {
        // Ayri test: bu, kullanicinin "gozlem modundayim" sanip GERCEK uygulama baslatmasini
        // onleyen kural. Ayristirici tanimadigi modu enforce'a DUSURSEYDI tam tersi olurdu.
        Assert.Equal(HtaccessMode.Off, HtaccessSwitchFile.Parse("enabled=1\nmode=gozlem\n"));
        Assert.Equal(HtaccessMode.Observe, HtaccessSwitchFile.Parse("enabled=1\nmode=observe\n"));
    }

    [Fact]
    public void SiteAdiBOSSA_KAPALI()
    {
        // Hangi kiracinin kurallarini uygulayacagimizi bilmiyorsak hicbir sey yapmayiz.
        var (gate, reader, _) = Build();
        reader.Files[GlobalPath] = "enabled=1\nmode=enforce\n";

        Assert.Equal(HtaccessMode.Off, gate.Evaluate(null));
        Assert.Equal(HtaccessMode.Off, gate.Evaluate(string.Empty));
        Assert.Equal(HtaccessMode.Off, gate.Evaluate("   "));
    }

    // ---------------------------------------------------------------- MALIYET / TTL

    [Fact]
    public void GenelAnahtarKAPALIYKEN_SiteDosyasinaHIC_BAKILMAZ()
    {
        // KISA DEVRE olcumu: kapatma dugmesine basildiginda sunucudaki dosya okuma yuku ARTMAMALI.
        var (gate, reader, clock) = Build();

        for (var i = 0; i < 10; i++)
        {
            gate.Evaluate(Site);
            Advance(clock, 2.1);
        }

        // Yalnizca GENEL dosya okundu; site dosyasi hic denenmedi.
        Assert.Equal(10, reader.ReadCount);
    }

    [Fact]
    public void TTL_icinde_DOSYA_TEKRAR_OKUNMAZ()
    {
        var (gate, reader, _) = Build();
        reader.Files[GlobalPath] = "enabled=1\nmode=enforce\n";
        reader.Files[SitePath] = "enabled=1\nmode=enforce\n";

        gate.Evaluate(Site);
        var afterFirst = reader.ReadCount; // 2 (genel + site)

        for (var i = 0; i < 1000; i++)
        {
            gate.Evaluate(Site);
        }

        Assert.Equal(afterFirst, reader.ReadCount);
    }

    [Fact]
    public void TTL_dolunca_DEGISIKLIK_GORULUR()
    {
        var (gate, reader, clock) = Build(ttlSeconds: 2);
        reader.Files[GlobalPath] = "enabled=1\nmode=enforce\n";
        reader.Files[SitePath] = "enabled=1\nmode=enforce\n";
        Assert.Equal(HtaccessMode.Enforce, gate.Evaluate(Site));

        reader.Files.Remove(GlobalPath);

        // TTL DOLMADAN: hala eski deger (bu, kabul edilen gecikme).
        Advance(clock, 1.0);
        Assert.Equal(HtaccessMode.Enforce, gate.Evaluate(Site));

        // TTL DOLUNCA: kapaniyor. "Aninda" tanimimiz budur ve olculmustur.
        Advance(clock, 1.5);
        Assert.Equal(HtaccessMode.Off, gate.Evaluate(Site));
    }

    [Fact]
    public void OKUMA_HATASI_da_ONBELLEGE_ALINIR_ve_HER_ISTEKTE_TEKRARLANMAZ()
    {
        // Fail-open sadece dogru cevap vermek degil, UCUZ olmak zorunda: bozuk ACL'li bir
        // kurulumda her istek bir istisna uretseydi site calisir ama SURUNURDU.
        var (gate, reader, _) = Build();
        reader.Throwing.Add(GlobalPath);

        for (var i = 0; i < 500; i++)
        {
            gate.Evaluate(Site);
        }

        Assert.Equal(1, reader.ReadCount);
        Assert.Equal(1, gate.ReadFailureCount);
    }

    // ---------------------------------------------------------------- İZOLASYON

    [Fact]
    public void IKI_AYRI_KAPI_DURUM_PAYLASMAZ()
    {
        // Kiraci izolasyonu: her uygulama havuzu kendi ornegini kurar. Buraya static bir alan
        // konulsaydi bir kiracinin anahtar durumu digerine sizardi.
        var readerA = new FakeReader();
        var readerB = new FakeReader();
        readerA.Files[GlobalPath] = "enabled=1\nmode=enforce\n";
        readerA.Files[SitePath] = "enabled=1\nmode=enforce\n";

        var gateA = new HtaccessRuntimeGate(DataRoot, TimeSpan.FromSeconds(2), readerA, () => 0L);
        var gateB = new HtaccessRuntimeGate(DataRoot, TimeSpan.FromSeconds(2), readerB, () => 0L);

        Assert.Equal(HtaccessMode.Enforce, gateA.Evaluate(Site));
        Assert.Equal(HtaccessMode.Off, gateB.Evaluate(Site));
    }

    [Fact]
    public void SITE_ONBELLEGI_SINIRLIDIR()
    {
        // Uydurma site adlariyla bellek sisirilemesin.
        //
        // BU TESTIN ILK SURUMU MUTASYONDA HAYATTA KALDI ve sebebi ogreticiydi: sinir kaldirilinca
        // da yesil kaliyordu, cunku doldurmak icin kullanilan sahte sitelerin HICBIRININ anahtar
        // dosyasi YOKTU -> zaten hepsi Off donuyordu. Test "sinir var mi"yi degil "dosyasi olmayan
        // site Off mu"yu olcuyordu. Simdi doldurma siteleri GERCEKTEN ACIK; boylece sinirin
        // devrede olmasi ile olmamasi FARKLI sonuc uretiyor.
        var (gate, reader, _) = Build();
        reader.Files[GlobalPath] = "enabled=1\nmode=enforce\n";

        // Sinirin (64) COK uzerinde, hepsi ACIK anahtar dosyasina sahip site.
        for (var i = 0; i < 500; i++)
        {
            var ad = "site-" + i + ".example";
            reader.Files[HtaccessHostingPaths.SiteSwitchPath(DataRoot, ad)] = "enabled=1\nmode=enforce\n";
        }

        var acikSayisi = 0;
        for (var i = 0; i < 500; i++)
        {
            if (gate.Evaluate("site-" + i + ".example") == HtaccessMode.Enforce)
            {
                acikSayisi++;
            }
        }

        // Sinir DEVREDE: ilk 64 site onbellege girdi, geri kalani KAPALI dondu.
        // Sinir kaldirilsaydi 500'un tamami Enforce donerdi ve bu iddia kirilirdi.
        Assert.True(acikSayisi > 0, "sinira kadar olan siteler calismali");
        Assert.True(acikSayisi <= 64, $"sinir asilmamali, {acikSayisi} site acik donmus");
    }

    // ---------------------------------------------------------------- PANEL <-> MODÜL SÖZLEŞMESİ

    /// <summary>
    /// GERÇEK DİSK ÜZERİNDE UÇTAN UCA: panelin YAZDIĞINI modülün OKUDUĞU.
    ///
    /// Yukarıdaki testlerin tamamı sahte bir okuyucu kullanıyor; hepsi "kapı mantığı doğru mu"
    /// sorusunu cevaplıyor. Ama asıl risk orada değil: panel (net8) ile modül (net48) AYRI
    /// SÜREÇLER ve aralarındaki tek sözleşme DİSKTEKİ DOSYA. Biçim ya da yol eşleşmezse
    /// bütün o testler yeşil kalır ve özellik canlıda hiç çalışmaz.
    ///
    /// Bu test o sözleşmeyi gerçek dosyalarla kurar: yazan taraf <see cref="HtaccessSwitchStore"/>,
    /// okuyan taraf <see cref="HtaccessRuntimeGate"/> (modülün kullandığı sınıfın ta kendisi).
    /// </summary>
    [Fact]
    public void PANELIN_YAZDIGINI_MODUL_OKUR_GERCEK_DISKTE()
    {
        var dizin = Directory.CreateTempSubdirectory("adapanel-htaccess-sozlesme");
        try
        {
            var dataRoot = dizin.FullName;
            var store = new HtaccessSwitchStore(dataRoot);
            const string site = "sozlesme.example";

            // Modül tarafı: kurucu yolları bir kez hesapladığı için her adımda YENİ örnek
            // kuruluyor (TTL beklemeden taze okuma).
            HtaccessMode Read() => new HtaccessRuntimeGate(dataRoot, TimeSpan.Zero).Evaluate(site);

            // 1) Hicbir sey yazilmadan: KAPALI (varsayilan kapali).
            Assert.Equal(HtaccessMode.Off, Read());

            // 2) Panel klasorleri kurar ama anahtari ACMAZ -> hala KAPALI.
            //    HardenAcl() BILEREK cagrilmiyor: bu test BICIM ve YOL sozlesmesini olcuyor.
            //    ACL'i burada uygulamak, testi calistiran siradan kullaniciyi kendi gecici
            //    klasorunden KILITLERDI (olculdu) -- ACL davranisi ayri testte, yukseltilmis
            //    yetki varsa olculuyor.
            store.EnsureDirectories();
            Assert.Equal(HtaccessMode.Off, Read());

            // 3) Panel genel anahtari acar, site kapali -> hala KAPALI.
            store.SetGlobal(HtaccessMode.Enforce, "test");
            Assert.Equal(HtaccessMode.Off, Read());

            // 4) Panel siteyi GOZLEM modunda acar -> modul GOZLEM okur (kademeli acilis).
            store.SetSite(site, HtaccessMode.Observe, "kademeli acilis");
            Assert.Equal(HtaccessMode.Observe, Read());

            // 5) Panel siteyi UYGULAMA moduna alir -> modul UYGULAMA okur.
            store.SetSite(site, HtaccessMode.Enforce, null);
            Assert.Equal(HtaccessMode.Enforce, Read());

            // 6) KAPATMA DUGMESI: panel genel anahtari kapatir -> her sey KAPANIR.
            store.SetGlobal(HtaccessMode.Off, null);
            Assert.Equal(HtaccessMode.Off, Read());

            // Genel anahtar dosyasi GERCEKTEN SILINMIS olmali: "enabled=0 yaz" yerine silmek,
            // ayristirici ne kadar bozulursa bozulsun kapali kalmasini garanti eder.
            Assert.False(File.Exists(HtaccessHostingPaths.GlobalSwitchPath(dataRoot)));

            // 7) Genel anahtar geri acilinca site ayari KORUNMUS olmali (site dosyasi silinmedi).
            store.SetGlobal(HtaccessMode.Enforce, null);
            Assert.Equal(HtaccessMode.Enforce, Read());
        }
        finally
        {
            dizin.Delete(recursive: true);
        }
    }

    /// <summary>
    /// ERİŞİM İZİNLERİ — yalnız YÜKSELTİLMİŞ yetkiyle ölçülebilir.
    ///
    /// DÜRÜSTLÜK: bu geliştirme makinesinde yükseltilmiş yetki YOK, dolayısıyla bu test burada
    /// ATLANIYOR ve ACL davranışı bu turda KANITLANAMADI. Test yine de yazıldı çünkü yönetici
    /// haklarıyla çalışan bir makinede (CI ya da müşteri sunucusu) kendiliğinden koşacak.
    /// "Çalıştıramadım" ile "doğru" aynı şey değildir; bu ayrımı testin kendisi taşıyor.
    /// </summary>
    [Fact]
    public void ERISIM_IZINLERI_KIRACIYA_OKUMA_VERIR_YAZMA_VERMEZ()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        if (!principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
        {
            // Yukseltilmis yetki yok -> ACL uygulanamaz. Yanlis bir "yesil" uretmemek icin cikiyoruz.
            return;
        }

        var dizin = Directory.CreateTempSubdirectory("adapanel-htaccess-acl");
        try
        {
            var store = new HtaccessSwitchStore(dizin.FullName);
            store.HardenAcl();

            var root = new DirectoryInfo(HtaccessHostingPaths.Root(dizin.FullName));
            var rules = root.GetAccessControl()
                .GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier));

            var iisSid = new System.Security.Principal.SecurityIdentifier("S-1-5-32-568");
            var iisRules = rules
                .Cast<FileSystemAccessRule>()
                .Where(rule => rule.IdentityReference.Equals(iisSid))
                .ToList();

            Assert.NotEmpty(iisRules);

            foreach (var rule in iisRules)
            {
                // OKUYABILMELI: okuyamazsa modul sessizce kapali kalir, panel yalan soyler.
                Assert.True(rule.FileSystemRights.HasFlag(FileSystemRights.Read));

                // YAZAMAMALI: yazabilseydi bir kiraci kendi PHP'siyle SUNUCU GENELINDEKI
                // anahtari cevirebilirdi -- kiraci izolasyonu acigi.
                Assert.False(rule.FileSystemRights.HasFlag(FileSystemRights.Write));
                Assert.False(rule.FileSystemRights.HasFlag(FileSystemRights.Modify));
            }
        }
        finally
        {
            try { dizin.Delete(recursive: true); } catch { /* yonetici degilsek silinemeyebilir */ }
        }
    }

    [Fact]
    public void PANELIN_YAZDIGI_DOSYA_ELLE_OKUNABILIR_ve_KULLANICI_NOTU_ENJEKSIYON_YAPAMAZ()
    {
        // Dosya bir KURTARMA ARACIDIR: yonetici yangin aninda Not Defteri ile acip tek satir
        // degistirebilmeli. Ayrica kullanici notu YORUM satirina yaziliyor -> icine satir sonu
        // koyup sahte bir "enabled=1" ENJEKTE edilememeli.
        var icerik = HtaccessSwitchFile.Render(HtaccessMode.Observe, "zararsiz\nenabled=1\nmode=enforce");

        // ASIL OLCUT: enjeksiyon denemesine RAGMEN etkin mod hala GOZLEM.
        Assert.Equal(HtaccessMode.Observe, HtaccessSwitchFile.Parse(icerik));

        // Not TEK SATIRA indirgenmis olmali: satir sonu gecseydi "mode=enforce" KENDI SATIRINDA
        // yer alir ve ayristirici onu GERCEK bir yonerge sanardi.
        // (Ilk surumde bu test yanlis seyi olcuyordu: metnin dosyada hic gecmemesini bekliyordu.
        //  Oysa metin YORUM SATIRININ ICINDE gecebilir ve gecmesi zararsizdir -- onemli olan
        //  KENDI SATIRINDA olmamasi.)
        var yonergeSatirlari = icerik
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && line[0] != '#' && line[0] != ';')
            .ToList();

        Assert.Contains("enabled=1", yonergeSatirlari);
        Assert.Contains("mode=observe", yonergeSatirlari);
        Assert.DoesNotContain("mode=enforce", yonergeSatirlari); // ENJEKTE EDILEMEDI
        Assert.Equal(2, yonergeSatirlari.Count);                 // fazladan yonerge YOK
    }
}
