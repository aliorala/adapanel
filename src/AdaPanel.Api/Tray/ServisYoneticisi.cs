using System.Diagnostics;
using System.ServiceProcess;

namespace AdaPanel.Api.Tray;

/// <summary>
/// Bir bileşenin ARKA PLAN BİRİMİ: adı ve o adın hangi alt sistemde aranacağı.
/// </summary>
/// <remarks>
/// Yalnız ad taşımak yetmiyordu: <c>AdaStalwart</c> bir servis adı gibi görünüyor ama Task
/// Scheduler'da duruyor. Adı biçiminden ayrı taşımak, başlat/durdur işlemlerinin yanlış alt
/// sisteme gitmesine yol açıyordu.
/// </remarks>
public sealed record ArkaPlanBirimi(string Ad, CalismaBicimi Bicim);

/// <summary>Bir bileşenin ekranda gösterilecek hâli.</summary>
/// <param name="ServisKayitli">Servis Windows'a kayıtlı mı.</param>
/// <param name="DosyalarKurulu">Bileşenin dosyaları diskte duruyor mu.</param>
/// <param name="Portlar">O an dinlediği TCP portları (ölçüldü; boşsa yok ya da ölçülemedi).</param>
/// <param name="Bicim">Bileşen arka planda NASIL çalışıyor: servis mi, zamanlanmış görev mi.</param>
/// <param name="Gorev">Zamanlanmış görevin ölçülen durumu (yalnız görev biçiminde anlamlı).</param>
/// <param name="SurecGorundu">
/// Bildirimde adı yazan sürecin çalışıyor olup olmadığı; <c>null</c> = ölçülmedi (süreç adı
/// bildirilmemiş).
/// </param>
public sealed record ServisDurumu(
    string Anahtar,
    string Baslik,
    bool ServisKayitli,
    bool DosyalarKurulu,
    ServiceControllerStatus? Durum,
    IReadOnlyList<int> Portlar,
    bool ServisiVar = true,
    CalismaBicimi Bicim = CalismaBicimi.Servis,
    GorevDurumu Gorev = GorevDurumu.Olculemedi,
    bool? SurecGorundu = null)
{
    public bool Calisiyor => Bicim == CalismaBicimi.ZamanlanmisGorev
        ? Gorev == GorevDurumu.Calisiyor || SurecGorundu == true
        : Durum == ServiceControllerStatus.Running;

    /// <summary>
    /// Bu bileşenin durumu HİÇ ölçülemedi mi.
    /// </summary>
    /// <remarks>
    /// Ölçülemeyen bir bileşen ARIZA SAYILMAZ. Tepsi normal kullanıcı olarak çalışırken Task
    /// Scheduler <c>E_ACCESSDENIED</c> veriyor (ölçüldü); bileşen için süreç adı da bildirilmemişse
    /// elimizde tek bir gerçek kalmıyor. O durumda "arızalı" demek uydurma olurdu — ve izleme
    /// betiğini her turda boşuna uyandırırdı.
    /// </remarks>
    public bool Olculemedi => ServisiVar
        && Bicim == CalismaBicimi.ZamanlanmisGorev
        && Gorev == GorevDurumu.Olculemedi
        && SurecGorundu is null;

    /// <summary>ÜÇ AYRI GERÇEK, tek duruma indirgenmez.</summary>
    /// <remarks>
    /// Önce yalnız "kurulu / kurulu değil" vardı ve YANLIŞ bilgi üretiyordu: ölçüldü, bu makinede
    /// Stalwart'ın 121 MB dosyası duruyordu ama AdaStalwart servisi Windows'a hiç kayıtlı değildi.
    /// Tepsi buna "kurulu değil" diyordu — oysa kurulu, yalnızca servisi yok. Biri "hiç yok",
    /// diğeri düzeltilebilir bir arıza; aynı metni yazmak kullanıcıyı yanlış yöne gönderiyordu.
    /// </remarks>
    public string DurumMetni
    {
        get
        {
            // SERVİSİ OLMAYAN BİLEŞEN (Roundcube, phpMyAdmin, PHP…): tek soru dosyaların
            // kurulu olup olmadığı. "Servisi kayıtlı değil" demek burada YANLIŞ olurdu —
            // öyle bir servisi hiç olmamalı.
            if (!ServisiVar)
            {
                return DosyalarKurulu ? "kurulu" : "kurulu değil";
            }

            if (Bicim == CalismaBicimi.ZamanlanmisGorev)
            {
                return GorevMetni();
            }

            if (!ServisKayitli)
            {
                return DosyalarKurulu ? "kurulu, servisi kayıtlı değil" : "kurulu değil";
            }

            var temel = Durum switch
            {
                ServiceControllerStatus.Running => "çalışıyor",
                ServiceControllerStatus.Stopped => "durdu",
                ServiceControllerStatus.StartPending => "başlatılıyor",
                ServiceControllerStatus.StopPending => "durduruluyor",
                null => "okunamadı",
                _ => Durum.ToString()!.ToLowerInvariant()
            };

            // PORT = "hangisi gerçekten kullanılıyor" sorusunun ÖLÇÜLEBİLİR cevabı. İki veritabanı
            // motoru birden kuruluysa portu yalnız biri tutar; kullanıcı diğerini gönül rahatlığıyla
            // durdurabilsin diye yazılıyor. Varsayılan port listesi TUTULMUYOR — o tahmin olurdu.
            return Portlar.Count > 0 ? temel + " · port " + string.Join(", ", Portlar) : temel;
        }
    }

    /// <summary>
    /// ZAMANLANMIŞ GÖREVLE çalışan bileşenin durum metni.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Servis metninden AYRI, çünkü sorular farklı. Burada "servisi kayıtlı değil" cümlesi HİÇ
    /// kurulamaz: öyle bir servis zaten olmamalı. Eski hâlde bu bileşenler SCM'de aranıyor,
    /// bulunamıyor ve kurulu bir posta sunucusu kalıcı olarak "kurulu, servisi kayıtlı değil"
    /// görünüp kullanıcıya "panelden yeniden kurun" deniyordu — yani ÇALIŞAN bir kurulumu bozacak
    /// bir tavsiye.
    /// </para>
    /// <para>
    /// Sıra kasıtlı: ÇALIŞIYOR olduğunu gösteren herhangi bir ölçüm (görev durumu ya da sürecin
    /// kendisi) her şeyin önüne geçer. Ondan sonra kesin bilgiler (devre dışı / kayıtsız) gelir.
    /// En sonda "ölçülemedi" durur; ölçemediğimizi arıza diye yazmayız.
    /// </para>
    /// </remarks>
    private string GorevMetni()
    {
        if (Calisiyor)
        {
            return Portlar.Count > 0 ? "çalışıyor · port " + string.Join(", ", Portlar) : "çalışıyor";
        }

        return Gorev switch
        {
            // Bilerek kapatılmış olabilir (ör. MailEnable'a geçilince Stalwart görevi devre dışı
            // bırakılır); "durdu" demek kullanıcıyı olmayan bir arızanın peşine düşürürdü.
            GorevDurumu.DevreDisi => "devre dışı — açılışta başlamaz",
            GorevDurumu.Kayitsiz => DosyalarKurulu ? "kurulu, görevi kayıtlı değil" : "kurulu değil",
            GorevDurumu.Durdu => "durdu",
            _ when !DosyalarKurulu => "kurulu değil",
            _ when SurecGorundu == false => "durdu",
            _ => "kurulu · durumu okunamadı"
        };
    }
}

/// <summary>
/// Windows servislerini okur ve yeniden başlatır.
/// </summary>
/// <remarks>
/// Panel çöktüğünde kullanılacak araç olduğu için panelin HİÇBİR parçasına bağımlı değildir:
/// ne HTTP ucuna, ne yapılandırma dosyasına, ne veritabanına. Yalnız Windows'un servis
/// yöneticisiyle konuşur.
/// </remarks>
public static class ServisYoneticisi
{
    /// <summary>
    /// İzlenen servislerin anlık durumu. Liste PANELİN BİLDİRİMİNDEN gelir
    /// (bkz. <see cref="BilesenListesi"/>); burada tahmin edilen hiçbir servis adı yoktur.
    /// </summary>
    public static IReadOnlyList<ServisDurumu> Oku()
    {
        var izlenenler = BilesenListesi.Oku();
        // TEK seferde okunur. Her aday için ayrı ServiceController açmak, servis yöneticisi
        // yanıt vermediğinde arayüzü saniyelerce kilitliyordu.
        ServiceController[] hepsi;
        try
        {
            hepsi = ServiceController.GetServices();
        }
        catch
        {
            // Servis yöneticisi hiç okunamadı: "okunamadı" (kurulu değil DEĞİL).
            return izlenenler
                .Select(t => new ServisDurumu(
                    t.Anahtar, t.Baslik, true, DosyaVar(t), null, [], t.ServisiVar, t.Bicim))
                .ToList();
        }

        try
        {
            var portTablosu = DinlenenPortlar.SurecBasinaOku();
            var surecKimlikleri = ServisSurecleri();
            var surecler = SurecKimlikleriAdBasina();
            var liste = new List<ServisDurumu>();

            foreach (var izlenen in izlenenler)
            {
                // ZAMANLANMIŞ GÖREV: SCM'de aranmaz. Aramak, kurulu bir bileşene her turda
                // "servisi kayıtlı değil" dedirtiyordu — soru yanlıştı, cevap değil.
                if (izlenen.Bicim == CalismaBicimi.ZamanlanmisGorev)
                {
                    liste.Add(GorevDurumunuOku(izlenen, surecler, portTablosu));
                    continue;
                }

                var bulunan = hepsi.FirstOrDefault(s =>
                    string.Equals(s.ServiceName, izlenen.ServisAdi, StringComparison.OrdinalIgnoreCase));

                if (bulunan is null)
                {
                    liste.Add(new ServisDurumu(
                        izlenen.Anahtar, izlenen.Baslik, false, DosyaVar(izlenen), null, [], izlenen.ServisiVar,
                        izlenen.Bicim));
                    continue;
                }

                ServiceControllerStatus? durum;
                try
                {
                    bulunan.Refresh();
                    durum = bulunan.Status;
                }
                catch
                {
                    // Okuyamamak "durdu" DEĞİLDİR; ayrı gösterilir.
                    durum = null;
                }

                IReadOnlyList<int> portlar = [];
                if (surecKimlikleri.TryGetValue(izlenen.ServisAdi, out var surecId)
                    && surecId > 0
                    && portTablosu.TryGetValue(surecId, out var bulunanPortlar))
                {
                    portlar = bulunanPortlar;
                }

                liste.Add(new ServisDurumu(
                    izlenen.Anahtar, izlenen.Baslik, true, DosyaVar(izlenen), durum, portlar, izlenen.ServisiVar,
                    izlenen.Bicim));
            }

            return liste;
        }
        finally
        {
            foreach (var s in hepsi)
            {
                s.Dispose();
            }
        }
    }

    /// <summary>
    /// Zamanlanmış görevle çalışan bir bileşenin durumunu İKİ BAĞIMSIZ KAYNAKTAN ölçer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// (1) Task Scheduler: kesin cevabı yalnız o verir (kayıtlı mı, devre dışı mı). Ama ÖLÇÜLDÜ —
    /// tepsinin normal (yetkisiz) oturumunda <c>GetTask</c> <c>E_ACCESSDENIED</c> ile düşüyor.
    /// </para>
    /// <para>
    /// (2) Sürecin kendisi: yetkisiz oturumda süreç ADI görülebiliyor (yol ve modüller
    /// görülemiyor; <c>Win32_Process.ExecutablePath</c> SYSTEM süreçleri için BOŞ dönüyor —
    /// ölçüldü). Bu yüzden "çalışıyor mu" sorusunun yetkisiz oturumda cevaplanabilen tek biçimi
    /// süreç adıdır; ad da tahmin edilmez, bildirimde <c>processName</c> olarak YAZAR.
    /// </para>
    /// <para>
    /// İkisi de sonuç vermezse cevap "ölçülemedi" olur — arıza değil.
    /// </para>
    /// </remarks>
    private static ServisDurumu GorevDurumunuOku(
        IzlenenServis izlenen,
        IReadOnlyDictionary<string, int> surecler,
        IReadOnlyDictionary<int, IReadOnlyList<int>> portTablosu)
    {
        var gorev = ZamanlanmisGorevler.Oku(izlenen.ServisAdi);

        bool? surecGorundu = null;
        IReadOnlyList<int> portlar = [];
        if (izlenen.SurecAdi.Length > 0)
        {
            if (surecler.TryGetValue(izlenen.SurecAdi, out var surecId))
            {
                surecGorundu = true;
                if (portTablosu.TryGetValue(surecId, out var bulunanPortlar))
                {
                    portlar = bulunanPortlar;
                }
            }
            else
            {
                surecGorundu = false;
            }
        }

        // "Kayıtlı" = görevin sistemde DURDUĞUNU gördük. Ölçülemediğinde false yazılır ama
        // DurumMetni/renk buna değil, Gorev + SurecGorundu ikilisine bakar; yoksa ölçemediğimiz
        // bileşen yine "kayıtlı değil" diye görünürdü.
        var kayitli = gorev is GorevDurumu.Calisiyor or GorevDurumu.Durdu or GorevDurumu.DevreDisi;

        return new ServisDurumu(
            izlenen.Anahtar,
            izlenen.Baslik,
            kayitli,
            DosyaVar(izlenen),
            null,
            portlar,
            izlenen.ServisiVar,
            izlenen.Bicim,
            gorev,
            surecGorundu);
    }

    /// <summary>Süreç adı → süreç kimliği (ilk bulunan). Ad, yetkisiz oturumda da okunabilir.</summary>
    private static Dictionary<string, int> SurecKimlikleriAdBasina()
    {
        var tablo = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var surec in Process.GetProcesses())
            {
                try
                {
                    if (!tablo.ContainsKey(surec.ProcessName))
                    {
                        tablo[surec.ProcessName] = surec.Id;
                    }
                }
                catch
                {
                    // Tek bir süreç okunamadı; ölçüm sürer.
                }
                finally
                {
                    surec.Dispose();
                }
            }
        }
        catch
        {
            // Süreç listesi hiç alınamadı: tablo boş kalır, uydurma yapılmaz.
        }

        return tablo;
    }

    /// <summary>Bileşenin dosyaları diskte duruyor mu (bildirimdeki kurulum dizini).</summary>
    private static bool DosyaVar(IzlenenServis izlenen)
    {
        if (izlenen.KurulumDizini.Length == 0)
        {
            return false;
        }

        try
        {
            return Directory.Exists(izlenen.KurulumDizini);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Servis adı → süreç kimliği. Portu sürece bağlamak için gerekiyor.</summary>
    private static Dictionary<string, int> ServisSurecleri()
    {
        var tablo = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var sorgu = new System.Management.ManagementObjectSearcher(
                "SELECT Name, ProcessId FROM Win32_Service WHERE State='Running'");
            foreach (var oge in sorgu.Get())
            {
                var ad = oge["Name"]?.ToString();
                if (string.IsNullOrEmpty(ad))
                {
                    continue;
                }

                tablo[ad] = Convert.ToInt32(oge["ProcessId"] ?? 0);
            }
        }
        catch (Exception)
        {
            // Ölçülemedi: port sütunu boş kalır, uydurma yapılmaz.
        }

        return tablo;
    }

    /// <summary>Anahtarın karşılığı olan arka plan birimi (servis ya da görev); yoksa null.</summary>
    /// <remarks>
    /// Zamanlanmış görevlerde varlık doğrulaması YAPILMAZ: ölçüldü, yetkisiz oturumda Task
    /// Scheduler <c>E_ACCESSDENIED</c> veriyor. "Doğrulayamadım" yüzünden null dönseydik tepsi,
    /// KURULU bir posta sunucusuna "kayıtlı değil" deyip işlemi hiç denemezdi. İşlem zaten
    /// yetkili alt süreçte çalışır ve sonucunu orada ölçer.
    /// </remarks>
    public static ArkaPlanBirimi? BirimiBul(string anahtar)
    {
        var izlenen = BilesenListesi.Oku().FirstOrDefault(t => t.Anahtar == anahtar);
        if (izlenen is null || !izlenen.ServisiVar)
        {
            return null;
        }

        if (izlenen.Bicim == CalismaBicimi.ZamanlanmisGorev)
        {
            return ZamanlanmisGorevler.Oku(izlenen.ServisAdi) == GorevDurumu.Kayitsiz
                ? null
                : new ArkaPlanBirimi(izlenen.ServisAdi, izlenen.Bicim);
        }

        try
        {
            using var servis = new ServiceController(izlenen.ServisAdi);
            _ = servis.Status; // kurulu değilse fırlatır
            return new ArkaPlanBirimi(izlenen.ServisAdi, CalismaBicimi.Servis);
        }
        catch
        {
            return null;
        }
    }

    public static string BasligiBul(string anahtar)
        => BilesenListesi.Oku().FirstOrDefault(t => t.Anahtar == anahtar)?.Baslik ?? anahtar;

    /// <summary>
    /// İzleme betiğine "arıza var" (çıkış kodu 1) denecek mi.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ARIZA = sistemde VAR OLDUĞU görülen (dosyaları kurulu ya da birimi kayıtlı) ve çalışmadığı
    /// ÖLÇÜLEN bileşen. Hiç kurulmamış bileşen arıza değildir; ÖLÇÜLEMEYEN bileşen de arıza
    /// değildir. BİLEREK devre dışı bırakılmış görev de arıza değildir: panel MailEnable'a
    /// geçildiğinde <c>AdaStalwart</c> görevini kendisi devre dışı bırakıyor, buna alarm vermek
    /// yöneticinin kendi kararını arıza diye bildirmek olurdu.
    /// </para>
    /// <para>
    /// ÖLÇÜLDÜ — eski kural <c>!ServisKayitli &amp;&amp; DosyalarKurulu</c> içeriyordu ve Stalwart
    /// bir Windows servisi OLMADIĞI için bu koşul her turda doğruydu: tepsi kurulu ve sağlıklı bir
    /// posta sunucusu yüzünden KALICI olarak 1 döndürüyordu. Kalıcı alarm, alarmın kendisini
    /// işe yaramaz hâle getirir.
    /// </para>
    /// </remarks>
    public static bool ArizaVar(IEnumerable<ServisDurumu> durumlar)
        => durumlar.Any(x => x.ServisiVar
            && (x.DosyalarKurulu || x.ServisKayitli)
            && !x.Olculemedi
            && x.Gorev != GorevDurumu.DevreDisi
            && !x.Calisiyor);

    /// <summary>
    /// Servisi yeniden başlatır. YÖNETİCİ yetkisi gerekir; bu metot yetkili süreçte çağrılır.
    /// </summary>
    /// <remarks>
    /// Durdurma ve başlatma AYRI beklemelerle yapılır. Tek bir "restart" çağrısı yok; servis
    /// durmadan başlatmaya çalışmak "hizmet zaten çalışıyor/geçiş hâlinde" hatası verir ve
    /// kullanıcı servisin yeniden başladığını sanırken hiçbir şey olmamış olurdu.
    /// </remarks>
    public static string YenidenBaslat(ArkaPlanBirimi birim, TimeSpan zamanAsimi)
    {
        if (birim.Bicim == CalismaBicimi.ZamanlanmisGorev)
        {
            // Zaten durmuş bir görevde /End hata döner; bu bir arıza değil, o yüzden sonucuna
            // bakılmadan geçilir. Karar YALNIZ ölçülen son duruma göre verilir.
            GorevKomutu(birim.Ad, "/End");
            return GorevIslemi(birim.Ad, "/Run", GorevDurumu.Calisiyor, "yeniden başlatıldı", zamanAsimi);
        }

        return YenidenBaslat(birim.Ad, zamanAsimi);
    }

    private static string YenidenBaslat(string servisAdi, TimeSpan zamanAsimi)
    {
        using var servis = new ServiceController(servisAdi);

        if (servis.Status != ServiceControllerStatus.Stopped && servis.CanStop)
        {
            servis.Stop();
            servis.WaitForStatus(ServiceControllerStatus.Stopped, zamanAsimi);
        }

        servis.Start();
        servis.WaitForStatus(ServiceControllerStatus.Running, zamanAsimi);
        servis.Refresh();

        return servis.Status == ServiceControllerStatus.Running
            ? $"{servisAdi}: yeniden başlatıldı."
            : $"{servisAdi}: durum {servis.Status} — beklenen 'Running' değil.";
    }

    /// <summary>Arka plan birimini başlatır.</summary>
    public static string Baslat(ArkaPlanBirimi birim, TimeSpan zamanAsimi)
        => birim.Bicim == CalismaBicimi.ZamanlanmisGorev
            ? GorevIslemi(birim.Ad, "/Run", GorevDurumu.Calisiyor, "başlatıldı", zamanAsimi)
            : Baslat(birim.Ad, zamanAsimi);

    private static string Baslat(string servisAdi, TimeSpan zamanAsimi)
    {
        using var servis = new ServiceController(servisAdi);
        if (servis.Status == ServiceControllerStatus.Running)
        {
            return $"{servisAdi}: zaten çalışıyor.";
        }

        servis.Start();
        servis.WaitForStatus(ServiceControllerStatus.Running, zamanAsimi);
        servis.Refresh();
        return servis.Status == ServiceControllerStatus.Running
            ? $"{servisAdi}: başlatıldı."
            : $"{servisAdi}: durum {servis.Status} — beklenen 'Running' değil.";
    }

    /// <summary>Arka plan birimini durdurur.</summary>
    public static string Durdur(ArkaPlanBirimi birim, TimeSpan zamanAsimi)
        => birim.Bicim == CalismaBicimi.ZamanlanmisGorev
            ? GorevIslemi(birim.Ad, "/End", GorevDurumu.Durdu, "durduruldu", zamanAsimi)
            : Durdur(birim.Ad, zamanAsimi);

    private static string Durdur(string servisAdi, TimeSpan zamanAsimi)
    {
        using var servis = new ServiceController(servisAdi);
        if (servis.Status == ServiceControllerStatus.Stopped)
        {
            return $"{servisAdi}: zaten durmuş.";
        }

        if (!servis.CanStop)
        {
            // "Durduramadım" ile "durdurdum" karıştırılmamalı.
            return $"{servisAdi}: bu servis durdurulmaya izin vermiyor.";
        }

        servis.Stop();
        servis.WaitForStatus(ServiceControllerStatus.Stopped, zamanAsimi);
        servis.Refresh();
        return servis.Status == ServiceControllerStatus.Stopped
            ? $"{servisAdi}: durduruldu."
            : $"{servisAdi}: durum {servis.Status} — beklenen 'Stopped' değil.";
    }

    /// <summary>
    /// Zamanlanmış görev üzerinde bir işlem yapar ve sonucu KOMUTUN ÇIKIŞ KODUNA DEĞİL, işlemden
    /// sonra yeniden ÖLÇÜLEN duruma göre bildirir.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>schtasks /Run</c> görevi tetikler ve hemen döner — "SUCCESS" yazması sürecin ayakta
    /// kaldığı anlamına GELMEZ (bu depoda ölçülmüş bir tuzak: kurulum "SUCCESS" alıyor ama görev
    /// 72 saat sınırıyla ölüyordu). Bu yüzden beklenen duruma ulaşılana kadar Task Scheduler
    /// yeniden okunur.
    /// </para>
    /// <para>
    /// Durum HİÇ okunamıyorsa (yetkisiz oturum) "başarılı" DENMEZ: çağıran, metinde başarı
    /// sözcüğü göremediği için işlemi doğrulanmamış sayar. Doğrulanmamış bir işlemi başarılı
    /// göstermek, kullanıcının posta sunucusunu başlattığını sanmasına yol açardı.
    /// </para>
    /// </remarks>
    private static string GorevIslemi(
        string gorevAdi,
        string komut,
        GorevDurumu beklenen,
        string basariSozcugu,
        TimeSpan zamanAsimi)
    {
        var sonuc = GorevKomutu(gorevAdi, komut);

        var bitis = DateTime.UtcNow + (zamanAsimi > TimeSpan.Zero ? zamanAsimi : TimeSpan.FromSeconds(30));
        var son = GorevDurumu.Olculemedi;
        do
        {
            son = ZamanlanmisGorevler.Oku(gorevAdi);
            if (son == beklenen)
            {
                return $"{gorevAdi}: {basariSozcugu}.";
            }

            if (son == GorevDurumu.Olculemedi)
            {
                break; // okuyamıyoruz; beklemek boşuna
            }

            Thread.Sleep(500);
        }
        while (DateTime.UtcNow < bitis);

        return son == GorevDurumu.Olculemedi
            ? $"{gorevAdi}: komut gönderildi ({(sonuc ? "kabul edildi" : "reddedildi")}) ama görev durumu OKUNAMADI — sonuç doğrulanamıyor."
            : $"{gorevAdi}: durum {son} — beklenen '{beklenen}' değil.";
    }

    /// <summary><c>schtasks.exe</c>'i çalıştırır; çıkış kodu 0 ise true.</summary>
    private static bool GorevKomutu(string gorevAdi, string komut)
    {
        try
        {
            using var surec = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"{komut} /TN \"{gorevAdi}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (surec is null)
            {
                return false;
            }

            surec.WaitForExit(30_000);
            return surec.HasExited && surec.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Süreç yönetici yetkisiyle mi çalışıyor.</summary>
    public static bool YoneticiMi()
    {
        try
        {
            using var kimlik = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(kimlik)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Kendini YÖNETİCİ olarak, tek bir yeniden başlatma işi için yeniden çalıştırır.
    /// </summary>
    /// <returns>Kullanıcı UAC'yi onayladıysa ve iş bittiyse true.</returns>
    public static bool YetkiliCalistir(string anahtar, string islem = "restart")
    {
        var yol = Environment.ProcessPath;
        if (string.IsNullOrEmpty(yol))
        {
            return false;
        }

        var baslangic = new ProcessStartInfo
        {
            FileName = yol,
            Arguments = $"--{islem} {anahtar}",
            UseShellExecute = true, // runas YALNIZ shell ile çalışır
            Verb = "runas"
        };

        try
        {
            using var surec = Process.Start(baslangic);
            if (surec is null)
            {
                return false;
            }

            surec.WaitForExit(120_000);
            return surec.HasExited && surec.ExitCode == 0;
        }
        catch (Exception)
        {
            // Kullanıcı UAC'yi reddetti ya da başlatılamadı. Sessiz geçilmez; çağıran bildirir.
            return false;
        }
    }
}
