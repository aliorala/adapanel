using System.Diagnostics;
using System.ServiceProcess;

namespace AdaPanel.Api.Tray;

/// <summary>Bir servisin ekranda gösterilecek hâli.</summary>
/// <summary>Bir bileşenin ekranda gösterilecek hâli.</summary>
/// <param name="ServisKayitli">Servis Windows'a kayıtlı mı.</param>
/// <param name="DosyalarKurulu">Bileşenin dosyaları diskte duruyor mu.</param>
/// <param name="Portlar">O an dinlediği TCP portları (ölçüldü; boşsa yok ya da ölçülemedi).</param>
public sealed record ServisDurumu(
    string Anahtar,
    string Baslik,
    bool ServisKayitli,
    bool DosyalarKurulu,
    ServiceControllerStatus? Durum,
    IReadOnlyList<int> Portlar,
    bool ServisiVar = true)
{
    public bool Calisiyor => Durum == ServiceControllerStatus.Running;

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
                .Select(t => new ServisDurumu(t.Anahtar, t.Baslik, true, DosyaVar(t), null, [], t.ServisiVar))
                .ToList();
        }

        try
        {
            var portTablosu = DinlenenPortlar.SurecBasinaOku();
            var surecKimlikleri = ServisSurecleri();
            var liste = new List<ServisDurumu>();

            foreach (var izlenen in izlenenler)
            {
                var bulunan = hepsi.FirstOrDefault(s =>
                    string.Equals(s.ServiceName, izlenen.ServisAdi, StringComparison.OrdinalIgnoreCase));

                if (bulunan is null)
                {
                    liste.Add(new ServisDurumu(
                        izlenen.Anahtar, izlenen.Baslik, false, DosyaVar(izlenen), null, [], izlenen.ServisiVar));
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
                    izlenen.Anahtar, izlenen.Baslik, true, DosyaVar(izlenen), durum, portlar, izlenen.ServisiVar));
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

    /// <summary>Anahtarın karşılığı olan, sistemde KURULU servis adı; yoksa null.</summary>
    public static string? ServisAdiniBul(string anahtar)
    {
        var izlenen = BilesenListesi.Oku().FirstOrDefault(t => t.Anahtar == anahtar);
        if (izlenen is null || !izlenen.ServisiVar)
        {
            return null;
        }

        try
        {
            using var servis = new ServiceController(izlenen.ServisAdi);
            _ = servis.Status; // kurulu değilse fırlatır
            return izlenen.ServisAdi;
        }
        catch
        {
            return null;
        }
    }

    public static string BasligiBul(string anahtar)
        => BilesenListesi.Oku().FirstOrDefault(t => t.Anahtar == anahtar)?.Baslik ?? anahtar;

    /// <summary>
    /// Servisi yeniden başlatır. YÖNETİCİ yetkisi gerekir; bu metot yetkili süreçte çağrılır.
    /// </summary>
    /// <remarks>
    /// Durdurma ve başlatma AYRI beklemelerle yapılır. Tek bir "restart" çağrısı yok; servis
    /// durmadan başlatmaya çalışmak "hizmet zaten çalışıyor/geçiş hâlinde" hatası verir ve
    /// kullanıcı servisin yeniden başladığını sanırken hiçbir şey olmamış olurdu.
    /// </remarks>
    public static string YenidenBaslat(string servisAdi, TimeSpan zamanAsimi)
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

    /// <summary>Servisi başlatır.</summary>
    public static string Baslat(string servisAdi, TimeSpan zamanAsimi)
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

    /// <summary>Servisi durdurur.</summary>
    public static string Durdur(string servisAdi, TimeSpan zamanAsimi)
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
