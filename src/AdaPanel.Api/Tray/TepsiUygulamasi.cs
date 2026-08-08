using System.Diagnostics;

namespace AdaPanel.Api.Tray;

/// <summary>
/// AdaPanel tepsi programı: saatin yanındaki simgeden servis durumu ve yeniden başlatma.
/// </summary>
/// <summary>
/// Tepsi programı. AdaPanel.Api.exe içinde YAŞAR ve yalnız <c>--tray</c> / <c>--durum</c> /
/// <c>--restart</c> anahtarlarıyla çalışır.
/// </summary>
/// <remarks>
/// Ayrı bir exe olarak da yazılabilirdi ama kendi .NET çalışma zamanını taşıması gerekirdi ve
/// kurulum paketi ~70 MB büyürdü. Panel exe'si zaten net8.0-windows; aynı ikilinin içinde
/// durmak hiçbir şey eklemiyor. Kod yine de tamamen AYRI dosyalarda ve panelin hiçbir
/// servisine bağımlı değil: panel çökmüş olsa bile bu yol çalışmalı.
/// </remarks>
public static class TepsiUygulamasi
{
    /// <summary>Aynı anda tek örnek: her oturum açılışında yeni bir simge birikmesin.</summary>
    private static Mutex? tekOrnek;

    /// <summary>
    /// İkinci bir örnek başlatıldığında çalışan örneğe "pencereni aç" demek için kullanılan
    /// isimlendirilmiş sinyal. Kısayola basmak, program zaten açıkken de bir şey yapmalı.
    /// </summary>
    private const string GosterSinyali = "AdaPanel.Tray.PencereGoster";

    /// <summary>Anahtar bu programa mı ait? (Panel normal açılışında hiçbirine dokunulmaz.)</summary>
    public static bool BuBizim(string[] argumanlar)
        => argumanlar.Length > 0 && argumanlar[0] is "--tray" or "--tray-goster" or "--durum" or "--restart" or "--start" or "--stop";

    public static int Calistir(string[] argumanlar)
    {
        // YETKİLİ ALT ÇALIŞTIRMA: arayüz açmadan tek bir servisi yeniden başlatıp çıkar.
        // Tepsi programı oturum açılışında yönetici İSTEMEDEN başlar; yetki yalnız gerçekten
        // gerektiği anda, bu yolla bir kez istenir.
        if (argumanlar.Length == 2 && argumanlar[0] is "--restart" or "--start" or "--stop")
        {
            return YetkiliServisIslemi(argumanlar[0], argumanlar[1]);
        }

        // Pencere açmadan durumu yazar. İki işe yarıyor: (1) sunucuya uzaktan bağlanıldığında
        // ya da masaüstü hiç açılmadığında durum yine de görülebilsin, (2) arayüz olmadan
        // sınanabilsin — çalıştığı görülmeden "yapıldı" denemez.
        if (argumanlar.Length == 1 && argumanlar[0] == "--durum")
        {
            return DurumuYaz();
        }

        // "--tray-goster": kullanıcı pencereyi görmek istiyor (Başlat menüsü kısayolu).
        var pencereIstendi = argumanlar.Length > 0 && argumanlar[0] == "--tray-goster";

        tekOrnek = new Mutex(initiallyOwned: true, "AdaPanel.Tray.TekOrnek", out var ilkMiyim);
        if (!ilkMiyim)
        {
            // ZATEN ÇALIŞIYOR.
            //
            // İki farklı niyet var ve ayrılmaları şart:
            //   --tray        → "çalıştığından emin ol". Zaten çalışıyorsa SESSİZCE çıkar.
            //                   Aksi halde her hatırlatma/başlatma denemesi pencereyi açardı.
            //   --tray-goster → "pencereyi aç". Başlat menüsü kısayolu bunu kullanır; eskiden
            //                   kısayol hiçbir şey yapmıyordu çünkü ikinci süreç sessizce ölüyordu.
            if (pencereIstendi)
            {
                try
                {
                    using var mevcutOrnek = EventWaitHandle.OpenExisting(GosterSinyali);
                    mevcutOrnek.Set();
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    // Sinyal yoksa (eski sürüm çalışıyor olabilir) yapacak bir şey yok.
                }
            }

            return 0;
        }

        // KONSOL PENCERESİNİ GİZLE.
        //
        // AdaPanel.Api bir konsol uygulaması (servis olarak da çalışması gerekiyor). "--tray" ile
        // açıldığında Windows ona bir konsol penceresi veriyordu: görev çubuğunda siyah bir pencere
        // duruyor ve kullanıcı onu kapatınca SÜREÇ ÖLÜYOR, tepsi simgesi de kayboluyordu.
        // Tepsi programının görev çubuğunda hiçbir penceresi olmamalı.
        //
        // "--durum" bunu YAPMAZ: orası çıktısını konsola yazıyor.
        var konsol = KonsolPenceresi();
        if (konsol != IntPtr.Zero)
        {
            PencereGoster(konsol, SW_HIDE);
        }

        ApplicationConfiguration.Initialize();

        using var pencere = new DurumPenceresi();

        // PENCERE TANITICISI ŞİMDİ OLUŞTURULUR.
        //
        // Pencere henüz gösterilmiyor ama `BeginInvoke` için tanıtıcı ŞART. Olmadan sinyal
        // geldiğinde "handle oluşturulmadı" istisnası ARKA PLAN iş parçacığında fırlıyor,
        // yakalayan olmadığı için süreç ÇÖKÜYORDU — yani kısayola ikinci kez basmak tepsi
        // programını tamamen kapatıyordu (ölçüldü: süreç sayısı 1 → 0).
        _ = pencere.Handle;

        // ÇALIŞAN örneğe "pencereni göster" demenin yolu. Ayrı bir iş parçacığında beklenir;
        // sinyal gelince pencere ARAYÜZ iş parçacığında açılır (WinForms başka türlü kabul etmez).
        using var gosterSinyali = new EventWaitHandle(false, EventResetMode.AutoReset, GosterSinyali);
        var dinleyici = new Thread(() =>
        {
            while (true)
            {
                gosterSinyali.WaitOne();
                if (pencere.IsDisposed)
                {
                    return;
                }

                try
                {
                    pencere.BeginInvoke(() => pencere.SagAltaGoster());
                }
                catch (Exception)
                {
                    // Arka plan iş parçacığında YAKALANMAYAN hiçbir istisna kalmamalı: burada
                    // fırlayan bir istisna bütün tepsi programını düşürür. Pencere kapanıyorsa
                    // ya da tanıtıcı yoksa yapılacak bir şey yok — sessizce çekilinir.
                    return;
                }
            }
        })
        {
            IsBackground = true
        };
        dinleyici.Start();
        using var simge = new NotifyIcon
        {
            Icon = SimgeYukle(),
            Visible = true,
            Text = "AdaPanel"
        };

        var menu = new ContextMenuStrip();
        // EN ÜSTTE "AdaPanel'i aç": tepsi simgesine gidilmesinin en sık sebebi bu.
        var panelAc = new ToolStripMenuItem("AdaPanel'i aç", null, (_, _) => TarayicidaAc(PanelAdresi.Bul()))
        {
            Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold)
        };
        menu.Items.Add(panelAc);
        menu.Items.Add("Servis durumu penceresi", null, (_, _) => pencere.SagAltaGoster());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("AdaPanel servisini yeniden başlat", null, (_, _) =>
        {
            // Menüden yapılan işin sonucu da GÖRÜNMELİ; pencere açılıp sonucu yazar.
            pencere.SagAltaGoster();
            YenidenBaslatVeGoster(pencere, "adapanel");
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Çıkış", null, (_, _) =>
        {
            simge.Visible = false;
            Application.Exit();
        });
        simge.ContextMenuStrip = menu;

        // Kabuk (explorer.exe) yeniden basladiginda simge silinir; geri koyan koruma.
        using var koruma = new SimgeKorumasi(simge);

        // Sol tık: pencere açık/kapa. Windows'ta tepsi simgesinin beklenen davranışı budur.
        simge.MouseClick += (_, olay) =>
        {
            if (olay.Button == MouseButtons.Left)
            {
                if (pencere.Visible)
                {
                    pencere.Hide();
                }
                else
                {
                    pencere.SagAltaGoster();
                }
            }
        };

        // İlk örnek doğrudan "pencereyi göster" niyetiyle açıldıysa pencere hemen açılır.
        if (pencereIstendi)
        {
            pencere.SagAltaGoster();
        }

        Application.Run();
        return 0;
    }

    private static void YenidenBaslatVeGoster(DurumPenceresi pencere, string anahtar)
    {
        var servisAdi = ServisYoneticisi.ServisAdiniBul(anahtar);
        if (servisAdi is null)
        {
            return;
        }

        if (ServisYoneticisi.YoneticiMi())
        {
            ServisYoneticisi.YenidenBaslat(servisAdi, TimeSpan.FromSeconds(60));
        }
        else
        {
            ServisYoneticisi.YetkiliCalistir(anahtar);
        }

        pencere.Yenile();
    }

    /// <summary>
    /// Servis durumunu konsola yazar. WinExe olduğu için konsol yoktur; üst süreç varsa ona
    /// bağlanılır (çağıran kabuk çıktıyı böyle görür).
    /// </summary>
    private static int DurumuYaz()
    {
        _ = KonsolaBaglan(-1);

        // ÇIKTI KODLAMASI açıkça UTF-8: varsayılan konsol kod sayfasında Türkçe harfler
        // bozuluyordu ("çalışıyor" → "Ã§alÄ±ÅŸÄ±yor"). Durumu okumak için çalıştırılan bir
        // tanı komutunun okunamaz çıktı vermesi, aracı işe yaramaz hâle getirir.
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch
        {
            // Konsol yoksa/yönlendirilmişse ayarlanamayabilir; çıktı yine yazılır.
        }

        Console.WriteLine($"Panel adresi   : {PanelAdresi.Bul()}");
        Console.WriteLine($"Kurulum klasörü: {PanelAdresi.KurulumKlasoru()}");
        Console.WriteLine($"Yönetici mi    : {(ServisYoneticisi.YoneticiMi() ? "evet" : "hayır")}");
        Console.WriteLine();

        var hepsi = ServisYoneticisi.Oku();

        // Pencereyle AYNI gruplama: düz liste 27 satır oluyor ve göz tutunacak yer bulamıyor.
        void Bolum(string baslik, IEnumerable<ServisDurumu> ogeler)
        {
            var dizi = ogeler.ToList();
            if (dizi.Count == 0)
            {
                return;
            }

            Console.WriteLine($"  [{baslik}]");
            foreach (var oge in dizi)
            {
                Console.WriteLine($"    {oge.Baslik,-34} {oge.DurumMetni}");
            }

            Console.WriteLine();
        }

        Bolum("SERVİSLER", hepsi.Where(x => x.ServisiVar));
        Bolum("KURULU ARAÇLAR", hepsi.Where(x => !x.ServisiVar && x.DosyalarKurulu));

        var kurulmayan = hepsi.Where(x => !x.ServisiVar && !x.DosyalarKurulu).Select(x => x.Baslik).ToList();
        if (kurulmayan.Count > 0)
        {
            Console.WriteLine($"  [KURULU DEĞİL] {string.Join(" · ", kurulmayan)}");
            Console.WriteLine();
        }

        // SORUN SAYILANLAR: kayıtlı ama çalışmayan servis VE dosyaları kurulu olduğu hâlde
        // servisi hiç kayıtlı olmayan bileşen. İkincisi de düzeltilmesi gereken bir arızadır;
        // "kurulu değil" ile aynı kefeye konursa izleme betiği sessiz kalırdı.
        var hataliVar = hepsi.Any(x => x.ServisiVar
            && ((x.ServisKayitli && !x.Calisiyor) || (!x.ServisKayitli && x.DosyalarKurulu)));

        // Çıkış kodu bilgi taşır: kurulu ama çalışmayan servis varsa 1. Bir izleme betiği
        // çıktıyı ayrıştırmak zorunda kalmasın.
        return hataliVar ? 1 : 0;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "AttachConsole", SetLastError = true)]
    private static extern bool KonsolaBaglan(int surecId);

    private const int SW_HIDE = 0;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetConsoleWindow")]
    private static extern IntPtr KonsolPenceresi();

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "ShowWindow")]
    private static extern bool PencereGoster(IntPtr pencere, int komut);

    /// <summary>
    /// Yetkili alt süreçte tek bir servis işlemi. Çıkış kodu ÇAĞIRANIN tek bilgi kaynağıdır:
    /// beklenen duruma ulaşılmadıysa başarı sayılmaz.
    /// </summary>
    private static int YetkiliServisIslemi(string anahtarBayragi, string anahtar)
    {
        var servisAdi = ServisYoneticisi.ServisAdiniBul(anahtar);
        if (servisAdi is null)
        {
            return 2;
        }

        try
        {
            var sure = TimeSpan.FromSeconds(90);
            var sonuc = anahtarBayragi switch
            {
                "--start" => ServisYoneticisi.Baslat(servisAdi, sure),
                "--stop" => ServisYoneticisi.Durdur(servisAdi, sure),
                _ => ServisYoneticisi.YenidenBaslat(servisAdi, sure)
            };

            var basarili = sonuc.Contains("başlatıldı", StringComparison.Ordinal)
                || sonuc.Contains("durduruldu", StringComparison.Ordinal)
                || sonuc.Contains("zaten", StringComparison.Ordinal);
            return basarili ? 0 : 3;
        }
        catch
        {
            return 1;
        }
    }

    /// <summary>
    /// Panelin favicon'u SVG; tepsi simgesi ICO ister. Kurulum klasöründe bir .ico varsa kullanılır,
    /// yoksa uygulamanın kendi simgesine düşülür — simge bulunamadı diye program açılmamazlık etmez.
    /// </summary>
    private static Icon SimgeYukle()
    {
        try
        {
            // ONCE KENDI YANINDA aranir, sonra servisin kurulum klasorunde.
            //
            // Ters sirada yazilmisti ve yanlisti: tepsi programi hangi klasorden calisiyorsa
            // ikonu da orada; servisin klasorune bakmak, tasinabilir bir kopyadan ya da
            // gelistirme ciktisindan calistirildiginda yanlis (ya da hic) ikon demekti.
            var adaylar = new List<string>();
            var kendiKlasoru = Path.GetDirectoryName(Environment.ProcessPath);
            if (!string.IsNullOrWhiteSpace(kendiKlasoru))
            {
                adaylar.Add(Path.Combine(kendiKlasoru, "adapanel.ico"));
            }

            var kurulum = PanelAdresi.KurulumKlasoru();
            if (!string.IsNullOrWhiteSpace(kurulum))
            {
                adaylar.Add(Path.Combine(kurulum, "adapanel.ico"));
            }

            foreach (var ico in adaylar)
            {
                if (File.Exists(ico))
                {
                    return new Icon(ico);
                }
            }

            var kendi = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(kendi))
            {
                var cikarilan = Icon.ExtractAssociatedIcon(kendi);
                if (cikarilan is not null)
                {
                    return cikarilan;
                }
            }
        }
        catch
        {
            // aşağıdaki varsayılana düşülür
        }

        return SystemIcons.Application;
    }

    private static void TarayicidaAc(string hedef)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = hedef, UseShellExecute = true });
        }
        catch
        {
            // Tarayıcı açılamadıysa yapabileceğimiz bir şey yok; program çökmemeli.
        }
    }
}
