using System.Diagnostics;
using System.ServiceProcess;

namespace AdaPanel.Api.Tray;

/// <summary>
/// Tepsi simgesine tıklanınca açılan servis paneli.
/// </summary>
/// <remarks>
/// <para>
/// Sağ tık menüsü yerine ayrı bir pencere: menüde her satır tek satır metindir, oysa asıl ihtiyaç
/// "hangisi ayakta, hangisi değil"i BİR BAKIŞTA görmek ve yanındaki düğmeye basmak. Panelin
/// açılmadığı bir anda menü gezmek istenmez.
/// </para>
/// <para>
/// Her satır ÜÇ ayrı gerçeği gösterir: dosyalar kurulu mu, servis kayıtlı mı, çalışıyor mu — ve
/// çalışıyorsa hangi portu tutuyor. Port, "iki motor kuruluysa hangisi kullanılıyor" sorusunun
/// ölçülebilir cevabıdır.
/// </para>
/// </remarks>
public sealed class DurumPenceresi : Form
{
    // Panelin kendi paleti: tepsi penceresi web arayüzüyle aynı dili konuşsun.
    private static readonly Color Zemin = Color.FromArgb(12, 23, 20);
    private static readonly Color Yuzey = Color.FromArgb(23, 48, 42);
    private static readonly Color Cizgi = Color.FromArgb(35, 66, 58);
    private static readonly Color Yazi = Color.FromArgb(230, 238, 234);
    private static readonly Color Soluk = Color.FromArgb(150, 175, 165);
    private static readonly Color Yesil = Color.FromArgb(72, 213, 151);
    private static readonly Color Kirmizi = Color.FromArgb(240, 130, 130);
    private static readonly Color Amber = Color.FromArgb(240, 180, 77);
    private static readonly Color Gri = Color.FromArgb(110, 130, 124);

    private readonly FlowLayoutPanel liste;
    private readonly Label ozet;
    private readonly Label durumSatiri;
    private readonly Label adresSatiri;
    private readonly System.Windows.Forms.Timer tazeleyici;
    private bool mesgul;
    private DateTime gosterilmeAni = DateTime.MinValue;

    /// <summary>Ekranda hâlihazırda duran bileşen listesi; değişmedikçe satırlar yeniden kurulmaz.</summary>
    private string sonAnahtarlar = string.Empty;

    /// <summary>Bileşen anahtarı → o satırın denetimleri (yerinde güncelleme için).</summary>
    private readonly Dictionary<string, Satir> satirlar = [];

    /// <summary>
    /// Bir satırın güncellenebilir parçaları.
    /// </summary>
    /// <remarks>
    /// Durum metni ve rengi her tazelemede değişebilir; düğmeler ise yalnız YAPILABİLİR İŞLEM
    /// değiştiğinde (çalışıyor ↔ durdu) değişir. İkisi ayrı tutuluyor ki her saniyede bir düğme
    /// yeniden yaratılmasın — asıl titreme oradan geliyordu.
    /// </remarks>
    private sealed class Satir(Panel kart, Label durumEtiketi)
    {
        private string sonDurum = string.Empty;
        private bool? sonCalisiyor;

        public void Guncelle(ServisDurumu servis, DurumPenceresi pencere)
        {
            if (sonDurum != servis.DurumMetni)
            {
                sonDurum = servis.DurumMetni;
                durumEtiketi.Text = servis.DurumMetni;
                durumEtiketi.ForeColor = RenkSec(servis);
            }

            // Düğmeler YALNIZCA kayıtlı bir servisin çalışma durumu değiştiğinde yenilenir.
            //
            // Koşulsuz çağrılıyordu ve iki hata birden üretiyordu: (1) servisi olmayan araç
            // kartlarına (PHP, Composer…) "Başlat" düğmesi ekleniyordu — olmayan bir yeteneği
            // varmış gibi göstermek; (2) her satırın düğmeleri ilk tazelemede yeniden yaratılıyor,
            // pencere titriyordu. Ölçüldü: denetim sayısı aynı kalıyor ama pencere tanıtıcıları
            // değişiyordu, yani denetimler yok edilip yeniden kuruluyordu.
            if (servis.ServisiVar
                && (servis.ServisKayitli || servis.Bicim == CalismaBicimi.ZamanlanmisGorev)
                && sonCalisiyor != servis.Calisiyor)
            {
                sonCalisiyor = servis.Calisiyor;
                pencere.DugmeleriYenile(kart, servis);
            }
        }
    }

    public DurumPenceresi()
    {
        Text = "AdaPanel — servisler";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;
        BackColor = Zemin;
        ForeColor = Yazi;
        Font = new Font("Segoe UI", 9F);
        ClientSize = new Size(560, 470);

        // --- Başlık: marka + tek cümlelik genel durum ---
        var baslikAlani = new Panel { Dock = DockStyle.Top, Height = 62, BackColor = Yuzey };
        var marka = new Label
        {
            Text = "AdaPanel",
            AutoSize = true,
            Location = new Point(16, 12),
            Font = new Font("Segoe UI Semibold", 12F),
            ForeColor = Yazi
        };
        ozet = new Label
        {
            AutoSize = false,
            Location = new Point(16, 34),
            Size = new Size(528, 18),
            ForeColor = Soluk,
            Text = string.Empty
        };
        baslikAlani.Controls.Add(marka);
        baslikAlani.Controls.Add(ozet);

        // --- Servis satırları ---
        liste = new CiftTamponluPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(12, 10, 12, 10),
            BackColor = Zemin
        };

        // --- Alt bölüm: sonuç satırı, adres, düğmeler ---
        durumSatiri = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 34,
            Padding = new Padding(16, 8, 16, 0),
            ForeColor = Soluk,
            Text = string.Empty
        };

        adresSatiri = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 26,
            Padding = new Padding(16, 4, 16, 0),
            ForeColor = Gri,
            Cursor = Cursors.Hand,
            Text = string.Empty
        };
        adresSatiri.Click += (_, _) => Ac(PanelAdresi.Bul());

        var serit = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 52,
            Padding = new Padding(14, 8, 14, 8),
            BackColor = Yuzey
        };

        var panelAc = Dugme("AdaPanel'i aç", 132, birincil: true);
        panelAc.Click += (_, _) => Ac(PanelAdresi.Bul());

        var klasor = Dugme("Kurulum klasörü", 132);
        klasor.Click += (_, _) =>
        {
            var yol = PanelAdresi.KurulumKlasoru();
            if (string.IsNullOrWhiteSpace(yol) || !Directory.Exists(yol))
            {
                Bildir("Kurulum klasörü bulunamadı.");
                return;
            }

            Ac(yol);
        };

        var yenile = Dugme("Yenile", 88);
        yenile.Click += (_, _) => Yenile();

        serit.Controls.AddRange([panelAc, klasor, yenile]);

        Controls.Add(liste);
        Controls.Add(durumSatiri);
        Controls.Add(adresSatiri);
        Controls.Add(serit);
        Controls.Add(baslikAlani);

        // CANLI KALSIN: pencere açıkken durum kendiliğinden tazelenir. Kullanıcı bir servisi
        // başka bir yerden (hizmetler ekranı, güncelleme) durdurduğunda burada bayat bilgi
        // görünmemeli — bayat bilgi, yanlış servisi durdurmaya yol açar.
        tazeleyici = new System.Windows.Forms.Timer { Interval = 4000 };
        tazeleyici.Tick += (_, _) =>
        {
            if (Visible && !mesgul)
            {
                Yenile();
            }
        };
        tazeleyici.Start();

        // Pencere odaktan çıkınca kapanır — ama GÖSTERİLDİKTEN HEMEN SONRA GELEN Deactivate sayılmaz.
        // Tepsi simgesine tıklandığında odak KABUKTA kalır ve pencere açılır açılmaz bir odak
        // kaybı olayı gelir. Bunu "başka yere tıklandı" saymak, pencerenin fare üzerine gelir
        // gelmez kapanmasına yol açıyordu; yani pencereye ulaşmak imkânsızdı.
        Deactivate += (_, _) =>
        {
            if (mesgul || DateTime.UtcNow - gosterilmeAni < TimeSpan.FromMilliseconds(600))
            {
                return;
            }

            Hide();
        };
    }

    /// <summary>
    /// Durumu yeniden okur ve satırları YERİNDE günceller.
    /// </summary>
    /// <remarks>
    /// Pencere dört saniyede bir tazeleniyor. Önce her tazelemede bütün satırlar silinip yeniden
    /// oluşturuluyordu: hiçbir şey değişmese bile ekran komple yeniden çiziliyor ve kullanıcı
    /// sürekli F5 basılıyormuş gibi bir titreme görüyordu. Canlı kalmak için denetimleri yeniden
    /// yaratmak gerekmiyor — var olanların metnini yazmak yetiyor. Denetimler yalnızca bileşen
    /// LİSTESİ değiştiğinde (yeni bir şey kuruldu/kaldırıldı) yeniden kurulur.
    /// </remarks>
    public void Yenile()
    {
        var servisler = ServisYoneticisi.Oku();

        var anahtarlar = string.Join("|", servisler.Select(x => x.Anahtar))
            + "#" + BilesenListesi.BildirimOkundu;

        if (anahtarlar != sonAnahtarlar || satirlar.Count == 0)
        {
            sonAnahtarlar = anahtarlar;
            SatirlariKur(servisler);
        }
        else
        {
            foreach (var servis in servisler)
            {
                if (satirlar.TryGetValue(servis.Anahtar, out var satir))
                {
                    satir.Guncelle(servis, this);
                }
            }
        }

        OzetiYaz(servisler);
        adresSatiri.Text = "Panel adresi: " + PanelAdresi.Bul();
    }

    /// <summary>Bileşen listesi değiştiğinde satırları baştan kurar.</summary>
    private void SatirlariKur(IReadOnlyList<ServisDurumu> servisler)
    {
        liste.SuspendLayout();
        liste.Controls.Clear();
        satirlar.Clear();

        // GRUPLAMA: bütün bileşenleri düz bir liste hâlinde dökmek okunmaz bir duvar olurdu.
        // Kullanıcının aradığı sırayla: önce servisler (asıl müdahale edilecek yer), sonra kurulu
        // araçlar, en sonda kurulu olmayanlar — tek satırda, çünkü onlar için yapılacak bir şey yok
        // ama "neden listede yok" sorusunu da doğurmamalılar.
        var servisliler = servisler.Where(x => x.ServisiVar).ToList();
        var kuruluAraclar = servisler.Where(x => !x.ServisiVar && x.DosyalarKurulu).ToList();
        var kurulmayanlar = servisler.Where(x => !x.ServisiVar && !x.DosyalarKurulu).ToList();

        if (servisliler.Count > 0)
        {
            liste.Controls.Add(BolumBasligi("SERVİSLER VE GÖREVLER"));
            foreach (var servis in servisliler)
            {
                liste.Controls.Add(SatirYap(servis));
            }
        }

        if (kuruluAraclar.Count > 0)
        {
            liste.Controls.Add(BolumBasligi("KURULU ARAÇLAR"));
            foreach (var arac in kuruluAraclar)
            {
                liste.Controls.Add(SatirYap(arac));
            }
        }

        if (kurulmayanlar.Count > 0)
        {
            liste.Controls.Add(BolumBasligi("KURULU DEĞİL"));
            liste.Controls.Add(new Label
            {
                Text = string.Join(" · ", kurulmayanlar.Select(x => x.Baslik)),
                AutoSize = false,
                Width = liste.ClientSize.Width - 30,
                Height = 46,
                ForeColor = Gri,
                Font = new Font("Segoe UI", 8.5F),
                Margin = new Padding(2, 0, 0, 8)
            });
        }

        liste.ResumeLayout();
    }

    /// <summary>
    /// Dosyaları kurulu olduğu hâlde arka plan birimi SİSTEMDE BULUNAMAYAN bileşen.
    /// </summary>
    /// <remarks>
    /// Zamanlanmış görevlerde "bulunamadı" YALNIZ <see cref="GorevDurumu.Kayitsiz"/> demektir.
    /// Okunamayan durum (yetkisiz oturum) buraya girmez; girseydi Stalwart her turda "eksik"
    /// sayılırdı — düzeltilen hatanın ta kendisi.
    /// </remarks>
    private static bool EksikBirim(ServisDurumu s)
        => s.ServisiVar && s.DosyalarKurulu
            && (s.Bicim == CalismaBicimi.ZamanlanmisGorev ? s.Gorev == GorevDurumu.Kayitsiz : !s.ServisKayitli);

    /// <summary>Başlıktaki tek cümlelik özet: kullanıcı tek bakışta "sorun var mı" görsün.</summary>
    private void OzetiYaz(IReadOnlyList<ServisDurumu> servisler)
    {
        // Özet, çıkış kodunun kullandığı ARIZA tanımıyla aynı kaynaktan beslenir; iki ayrı kural
        // olsaydı ekran "sorun yok" derken izleme betiği 1 döndürebilirdi.
        var eksik = servisler.Count(EksikBirim);
        var sorunlu = servisler.Count(s => ServisYoneticisi.ArizaVar([s]) && !EksikBirim(s));

        var metin = !BilesenListesi.BildirimOkundu
            // Bileşen listesi okunamadıysa SESSİZ GEÇİLMEZ: ekranda yalnız temel servisler görünür
            // ve kullanıcı veritabanının neden listede olmadığını anlayamazdı.
            ? $"Bileşen listesi okunamadı ({BilesenListesi.BildirimHatasi}) — yalnız temel servisler"
            : sorunlu == 0 && eksik == 0
                ? "Tüm servisler çalışıyor"
                : string.Join(" · ",
                    new[]
                    {
                        sorunlu > 0 ? $"{sorunlu} servis çalışmıyor" : null,
                        eksik > 0 ? $"{eksik} bileşenin servisi kayıtlı değil" : null
                    }.Where(x => x is not null));

        var renk = sorunlu > 0 || eksik > 0 ? Amber : Yesil;

        // Aynı metni yeniden atamak bile boyama tetikler; yalnız DEĞİŞTİYSE yazılır.
        if (ozet.Text != metin)
        {
            ozet.Text = metin;
        }

        if (ozet.ForeColor != renk)
        {
            ozet.ForeColor = renk;
        }
    }

    /// <summary>Bölüm başlığı: liste uzun olduğu için gözün tutunacağı yer.</summary>
    private Label BolumBasligi(string metin) => new()
    {
        Text = metin,
        AutoSize = false,
        Width = liste.ClientSize.Width - 30,
        Height = 22,
        ForeColor = Gri,
        Font = new Font("Segoe UI Semibold", 7.8F),
        TextAlign = ContentAlignment.BottomLeft,
        Margin = new Padding(2, 4, 0, 4)
    };

    /// <summary>Tek bir servis satırı: ad + durum + işlem düğmeleri.</summary>
    private Control SatirYap(ServisDurumu servis)
    {
        var kart = new Panel
        {
            Width = liste.ClientSize.Width - 30,
            Height = 52,
            BackColor = Yuzey,
            Margin = new Padding(0, 0, 0, 8)
        };

        var ad = new Label
        {
            Text = servis.Baslik,
            AutoSize = false,
            Location = new Point(12, 8),
            Size = new Size(260, 18),
            Font = new Font("Segoe UI Semibold", 9.5F),
            ForeColor = Yazi
        };

        var durum = new Label
        {
            Text = servis.DurumMetni,
            AutoSize = false,
            Location = new Point(12, 27),
            Size = new Size(300, 17),
            ForeColor = RenkSec(servis),
            Font = new Font("Segoe UI", 8.5F)
        };

        kart.Controls.Add(ad);
        kart.Controls.Add(durum);
        satirlar[servis.Anahtar] = new Satir(kart, durum);

        // İŞLEM DÜĞMELERİ duruma göre: durmuş bir servise "durdur" göstermek anlamsız, çalışan
        // bir servise "başlat" göstermek de öyle. Yalnız yapılabilecek olan görünür.
        var sag = kart.Width - 12;
        if (!servis.ServisiVar)
        {
            // Servisi olmayan araç: başlatılıp durdurulacak bir şey yok. "Yeniden başlat" koymak,
            // olmayan bir yeteneği varmış gibi göstermek olurdu.
            //
            // AMA BOŞ BIRAKILMAZ. Düğmesiz satırı gören kullanıcı "bunun yeniden başlatması niye
            // yok" diye soruyor — sebebi SÖYLENMELİ. Ayrıca yapılabilecek gerçek bir iş var:
            // bileşenin klasörünü açmak.
            if (servis.DosyalarKurulu)
            {
                var klasorYolu = BilesenListesi.Oku()
                    .FirstOrDefault(x => x.Anahtar == servis.Anahtar)?.KurulumDizini ?? string.Empty;
                if (klasorYolu.Length > 0)
                {
                    sag = DugmeKoy(kart, "Klasör", sag, 78, () => Ac(klasorYolu));
                }
            }

            var aciklama = new Label
            {
                Text = "servis değil",
                AutoSize = false,
                Location = new Point(sag - 96, 17),
                Size = new Size(92, 16),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Gri,
                Font = new Font("Segoe UI", 8F)
            };
            kart.Controls.Add(aciklama);
            return kart;
        }

        // ZAMANLANMIŞ GÖREV: "kayıtlı mı" sorusu yetkisiz oturumda cevaplanamıyor (ölçüldü:
        // E_ACCESSDENIED). Durumu okunamadı diye düğmeleri gizlemek, kullanıcıyı elle
        // schtasks'a mahkûm ederdi; işlem zaten yetkili alt süreçte çalışıp sonucunu ölçüyor.
        if (servis.ServisKayitli || (servis.Bicim == CalismaBicimi.ZamanlanmisGorev && servis.DosyalarKurulu))
        {
            DugmeleriYenile(kart, servis);
        }
        else if (servis.DosyalarKurulu)
        {
            // Dosyalar var ama servis yok: burada yapılabilecek bir şey yok, panelden yeniden
            // kurulması gerekiyor. Düğme koymak yerine bunu SÖYLÜYORUZ.
            //
            // Bu tavsiye YALNIZ gerçekten servis olan bileşenler içindir. Eskiden zamanlanmış
            // görevle çalışan Stalwart da buraya düşüyordu: kurulumu sağlam bir posta sunucusu
            // için "panelden yeniden kurun" yazıyordu — yani çalışan kurulumu bozmaya davet.
            var not = new Label
            {
                Text = "panelden yeniden kurun",
                AutoSize = false,
                Location = new Point(kart.Width - 172, 18),
                Size = new Size(160, 16),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Gri,
                Font = new Font("Segoe UI", 8F)
            };
            kart.Controls.Add(not);
        }

        return kart;
    }

    /// <summary>
    /// Bir satırın işlem düğmelerini duruma göre yeniden kurar.
    /// </summary>
    /// <remarks>
    /// YALNIZCA yapılabilir işlem değiştiğinde (çalışıyor ↔ durdu) çağrılır. Her tazelemede
    /// çağrılsaydı düğmeler saniyede bir yeniden yaratılır ve pencere titrerdi.
    /// Durmuş bir servise "Durdur", çalışan birine "Başlat" göstermek anlamsız olacağı için
    /// yalnız o an geçerli olanlar konur.
    /// </remarks>
    private void DugmeleriYenile(Panel kart, ServisDurumu servis)
    {
        foreach (var eski in kart.Controls.OfType<Button>().ToList())
        {
            kart.Controls.Remove(eski);
            eski.Dispose();
        }

        var sag = kart.Width - 12;
        if (servis.Calisiyor)
        {
            sag = DugmeKoy(kart, "Durdur", sag, 78, () => Islet(servis.Anahtar, "stop"));
            _ = DugmeKoy(kart, "Yeniden başlat", sag, 108, () => Islet(servis.Anahtar, "restart"));
        }
        else
        {
            _ = DugmeKoy(kart, "Başlat", sag, 78, () => Islet(servis.Anahtar, "start"));
        }
    }

    private int DugmeKoy(Control kart, string metin, int sagKenar, int genislik, Action eylem)
    {
        var dugme = Dugme(metin, genislik);
        dugme.Location = new Point(sagKenar - genislik, 11);
        dugme.Click += (_, _) => eylem();
        kart.Controls.Add(dugme);
        return sagKenar - genislik - 8;
    }

    private static Color RenkSec(ServisDurumu servis)
    {
        if (!servis.ServisiVar)
        {
            // Servisi OLMAYAN bileşen (araç): kuruluysa yeşil. Amber yapmak, hiçbir sorunu
            // olmayan bir aracı arıza gibi göstermek olurdu.
            return servis.DosyalarKurulu ? Yesil : Gri;
        }

        // ÖLÇÜLEMEDİ ≠ ARIZA. Yetkisiz oturumda zamanlanmış görevin durumu okunamıyor; bunu
        // kırmızı ya da amber göstermek, sağlıklı bir posta sunucusunu arıza gibi sunmak olurdu.
        if (servis.Olculemedi)
        {
            return Gri;
        }

        if (!servis.ServisKayitli && servis.Bicim != CalismaBicimi.ZamanlanmisGorev)
        {
            // Kurulu değil GRİ, "kurulu ama servisi yok" AMBER (düzeltilebilir arıza),
            // durmuş KIRMIZI. Hepsini kırmızı yapmak, kurulmamış bir bileşeni arıza sanmaya yol açar.
            return servis.DosyalarKurulu ? Amber : Gri;
        }

        if (servis.Bicim == CalismaBicimi.ZamanlanmisGorev && !servis.DosyalarKurulu)
        {
            return Gri; // hiç kurulmamış
        }

        if (servis.Gorev == GorevDurumu.DevreDisi)
        {
            return Amber; // bilerek kapatılmış; arıza değil ama "çalışıyor" da değil
        }

        return servis.Calisiyor ? Yesil : Kirmizi;
    }

    private static Button Dugme(string metin, int genislik, bool birincil = false) => new()
    {
        Text = metin,
        Width = genislik,
        Height = 30,
        FlatStyle = FlatStyle.Flat,
        BackColor = birincil ? Color.FromArgb(47, 143, 111) : Color.FromArgb(31, 62, 53),
        ForeColor = Yazi,
        Margin = new Padding(0, 0, 8, 0),
        Cursor = Cursors.Hand,
        FlatAppearance = { BorderColor = Cizgi, BorderSize = 1 }
    };

    /// <summary>
    /// Servis işlemini ARKA PLANDA yürütür; arayüz açık kalır.
    /// </summary>
    /// <remarks>
    /// İş arayüz iş parçacığında yapılırsa pencere, servis durup kalkana kadar (1-2 dakika
    /// sürebilir) Windows tarafından "Yanıt vermiyor" diye gösterilir. Panelin çöktüğü ve
    /// kullanıcının zaten gergin olduğu bir anda kurtarma aracının donmuş görünmesi, aracın da
    /// bozuk sanılmasına yol açar.
    /// </remarks>
    private async void Islet(string anahtar, string islem)
    {
        var birim = ServisYoneticisi.BirimiBul(anahtar);
        if (birim is null)
        {
            Bildir($"{ServisYoneticisi.BasligiBul(anahtar)}: kayıtlı değil.");
            return;
        }

        var servisAdi = birim.Ad;

        mesgul = true;
        Cursor = Cursors.WaitCursor;
        Bildir(islem switch
        {
            "start" => $"{servisAdi} başlatılıyor…",
            "stop" => $"{servisAdi} durduruluyor…",
            _ => $"{servisAdi} yeniden başlatılıyor…"
        });

        string sonuc;
        try
        {
            sonuc = await Task.Run(() =>
            {
                var sure = TimeSpan.FromSeconds(60);
                if (ServisYoneticisi.YoneticiMi())
                {
                    return islem switch
                    {
                        "start" => ServisYoneticisi.Baslat(birim, sure),
                        "stop" => ServisYoneticisi.Durdur(birim, sure),
                        _ => ServisYoneticisi.YenidenBaslat(birim, sure)
                    };
                }

                return ServisYoneticisi.YetkiliCalistir(anahtar, islem)
                    ? $"{servisAdi}: işlem tamamlandı."
                    // SESSİZ GEÇİLMEZ: UAC reddedilmiş olabilir. "Bir şey olmadı" demek,
                    // işlemin yapıldığı sanılmasına yol açar.
                    : $"{servisAdi}: yapılamadı (yönetici izni verilmedi ya da işlem başarısız).";
            });
        }
        catch (Exception hata)
        {
            sonuc = $"{servisAdi}: {hata.Message}";
        }

        Cursor = Cursors.Default;
        mesgul = false;
        Yenile();
        // Sonuç yenilemeden SONRA yazılır; önceki sırada yenileme mesajı eziyordu.
        Bildir(sonuc);
    }

    private void Bildir(string metin) => durumSatiri.Text = metin;

    private void Ac(string hedef)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = hedef, UseShellExecute = true });
        }
        catch (Exception hata)
        {
            Bildir($"Açılamadı: {hata.Message}");
        }
    }

    /// <summary>Pencereyi imlecin bulunduğu ekranın sağ alt köşesine yerleştirir.</summary>
    public void SagAltaGoster()
    {
        var ekran = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(ekran.Right - Width - 12, ekran.Bottom - Height - 12);
        Yenile();

        // Sıra önemli: önce damga, sonra Show(). Show() sırasında gelen sahte odak kaybı
        // olayının damgayı görmesi gerekiyor, yoksa pencere anında kapanır.
        gosterilmeAni = DateTime.UtcNow;
        TopMost = true;
        Show();
        Activate();
        SetForegroundWindow(Handle);
    }

    /// <summary>Kapatma düğmesi programı SONLANDIRMAZ, yalnız gizler.</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            tazeleyici.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Çift tamponlu <see cref="FlowLayoutPanel"/>.
    /// </summary>
    /// <remarks>
    /// WinForms'ta bu özellik korumalıdır ve varsayılan olarak kapalıdır; kapalıyken satırlar
    /// yeniden düzenlendiğinde ara kareler ekrana düşer ve göz kırpma olarak görünür.
    /// </remarks>
    private sealed class CiftTamponluPanel : FlowLayoutPanel
    {
        public CiftTamponluPanel()
        {
            DoubleBuffered = true;
        }
    }

    /// <summary>
    /// Pencereyi öne getirir. <see cref="Form.Activate"/> tek başına yetmiyor: tepsi
    /// tıklamasında ön plan penceresi kabuktur ve Windows odağı kendiliğinden devretmez.
    /// </summary>
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(
        System.Runtime.InteropServices.DllImportSearchPath.System32)]
    private static extern bool SetForegroundWindow(IntPtr pencere);
}
