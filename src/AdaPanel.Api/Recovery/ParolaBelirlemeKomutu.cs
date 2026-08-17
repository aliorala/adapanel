using System.Text;
using AdaPanel.Api.Tray;
using AdaPanel.Core.Common;
using AdaPanel.Core.Entities;
using AdaPanel.Data.Extensions;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Security;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AdaPanel.Api.Recovery;

/// <summary>
/// <c>AdaPanel.Api.exe --parola-belirle</c> — panele giremeyen yöneticinin parolasını SUNUCU
/// KONSOLUNDAN belirler. Plesk'teki <c>plesk bin admin --set-password</c> karşılığı.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden gerekliydi (ölçülen olay):</b> kullanıcı panel parolasını unuttu. Var olan
/// <c>config\recover-access.flag</c> yalnız giriş kilitlerini, IP engellerini ve panelin kendi
/// engelleme kurallarını temizliyor — PAROLAYI SIFIRLAMIYOR. Elde kalan tek çare
/// <c>data\adapanel.db</c>'yi silmek oldu: panel kullanıcıları, ayarları ve kurulu bileşen
/// kayıtları da gitti.
/// </para>
/// <para>
/// <b>Neden HTTP ucu YOK ve olmayacak:</b> bu kapı "sunucuda oturum açmış olmak"a dayanır. Ağdan
/// çağrılabilen bir uç, kimlik doğrulamayı tamamen atlayan bir arka kapı demekti.
/// </para>
/// <para>
/// <b>Neden panelin normal açılışından ÖNCE, tepsi programıyla aynı yerde:</b> bu araç tam da panel
/// açılamadığında lazım. Web sunucusunu, servis grafiğini ya da dinleyicileri kurmadan çalışır;
/// yalnız veritabanını açar. Aksi hâlde paneli açamayan bir hata, kurtarma yolunu da kapatırdı.
/// </para>
/// <para>
/// <b>Parola argüman olarak ALINMAZ.</b> Komut satırındaki bir parola süreç listesinde
/// (<c>wmic process get commandline</c>), kabuk geçmişinde ve zamanlanmış görev tanımlarında
/// görünür. Bu yüzden yalnız etkileşimli olarak, ekrana yazılmadan sorulur.
/// </para>
/// </remarks>
public static class ParolaBelirlemeKomutu
{
    // ÇIKIŞ KODLARI bilgi taşır: kurulum/izleme betikleri çıktıyı ayrıştırmak zorunda kalmasın.
    private const int BasariliKod = 0;
    private const int GenelHataKod = 1;
    private const int YetkiYokKod = 2;
    private const int HedefYokKod = 3;
    private const int VazgecildiKod = 4;

    /// <summary>Anahtar bu komuta mı ait? (Panelin normal açılışında hiçbirine dokunulmaz.)</summary>
    public static bool BuBizim(string[] argumanlar)
        => argumanlar.Length > 0 && argumanlar[0] is "--parola-belirle" or "--set-password";

    public static async Task<int> CalistirAsync(string[] argumanlar)
    {
        KonsoluHazirla();

        try
        {
            return await IcCalistirAsync(argumanlar);
        }
        catch (AppException hata)
        {
            Console.Error.WriteLine(HataMetni(hata));
            return hata.MessageKey is ConsolePasswordRecovery.TargetNotFoundKey
                or ConsolePasswordRecovery.TargetNotAdministratorKey
                or ConsolePasswordRecovery.NoAdministratorsKey
                ? HedefYokKod
                : GenelHataKod;
        }
        catch (InvalidOperationException hata) when (hata.Message == "users.password_too_short")
        {
            // Parola kuralı TEK kaynaktan geliyor (PasswordPolicy); burada yalnız Türkçeye çevriliyor.
            Console.Error.WriteLine($"HATA: Parola en az {PasswordPolicy.MinimumLength} karakter olmalı.");
            return VazgecildiKod;
        }
        catch (SqliteException hata)
        {
            // En sık görülecek gerçek arıza: panel servisi veritabanını uzun bir işlem için kilitlemiş
            // olabilir. Kullanıcıya "bilinmeyen hata" demek yerine yapılacak işi söylüyoruz.
            Console.Error.WriteLine($"HATA: Panel veritabanı açılamadı ({hata.SqliteErrorCode}): {hata.Message}");
            Console.Error.WriteLine("Öneri: 'net stop AdaPanel' ile servisi durdurup komutu yeniden çalıştırın.");
            return GenelHataKod;
        }
        catch (Exception hata)
        {
            Console.Error.WriteLine($"HATA: {hata.Message}");
            return GenelHataKod;
        }
    }

    private static async Task<int> IcCalistirAsync(string[] argumanlar)
    {
        // 1) YÖNETİCİ YETKİSİ ŞART.
        //
        // Veri kökü zaten yalnız yöneticilere açık (ACL sertleştirmesi), yani yetkisiz bir kullanıcı
        // veritabanını açamaz. Yine de burada AÇIKÇA denetleniyor: aksi hâlde kullanıcı anlaşılmaz bir
        // "erişim reddedildi" yığın izi görür ve sebebi yetki mi bozukluk mu bilemez.
        if (!ServisYoneticisi.YoneticiMi())
        {
            Console.Error.WriteLine("HATA: Bu komut YÖNETİCİ olarak çalıştırılmalı.");
            Console.Error.WriteLine("Komut istemini 'Yönetici olarak çalıştır' ile açıp tekrar deneyin.");
            return YetkiYokKod;
        }

        var ikiAdimliKapat = argumanlar.Any(deger => deger is "--2fa-kapat" or "--disable-2fa");
        var istenenKullanici = argumanlar
            .Skip(1)
            .FirstOrDefault(deger => !deger.StartsWith("--", StringComparison.Ordinal));

        await using var saglayici = SaglayiciKur();
        using var kapsam = saglayici.CreateScope();
        var dbContext = kapsam.ServiceProvider.GetRequiredService<AdaPanelDbContext>();

        // VERİTABANI VAR MI: yoksa sorgu "no such table: Users" ile patlar ve kullanıcı, panelin
        // hangi dosyayı aradığını göremez. Kurtarma anında en çok ihtiyaç duyulan bilgi tam olarak
        // budur (yanlış veri kökü, taşınmış kurulum, silinmiş dosya).
        var veritabaniYolu = new SqliteConnectionStringBuilder(dbContext.Database.GetConnectionString()).DataSource;
        if (!File.Exists(veritabaniYolu))
        {
            Console.Error.WriteLine($"HATA: Panel veritabanı bulunamadı: {veritabaniYolu}");
            Console.Error.WriteLine("Veri kökü farklıysa 'Panel__DataRoot' ortam değişkeniyle belirtin.");
            return HedefYokKod;
        }

        Console.WriteLine("AdaPanel — yönetici parolası belirleme (sunucu konsolu)");
        Console.WriteLine();

        // 2) HEDEF SEÇİMİ. Birden fazla yönetici olabilir; hangisinin parolasının değiştiği ASLA
        //    örtük kalmamalı.
        var hedef = istenenKullanici is null
            ? await SecimYapAsync(dbContext)
            : await ConsolePasswordRecovery.FindAdministratorAsync(dbContext, istenenKullanici);

        if (hedef is null)
        {
            return VazgecildiKod;
        }

        Console.WriteLine();
        Console.WriteLine($"Seçilen yönetici : {hedef.UserName} <{hedef.Email}>");
        Console.WriteLine($"Hesap durumu     : {(hedef.IsActive ? "aktif" : "PASİF")}");
        Console.WriteLine($"İki adımlı       : {(hedef.IsTwoFactorEnabled ? "açık" : "kapalı")}");
        Console.WriteLine();

        // 3) İKİ ADIMLI DOĞRULAMA KARARI.
        //
        // Varsayılan: KORUNUR. Parolayı sıfırlarken 2FA'yı da sessizce kapatmak, ikinci faktörü tam
        // da var olma sebebinin gerçekleştiği anda (parola kayboldu/ele geçirildi) işlevsiz kılardı.
        // Doğrulayıcısını da kaybetmiş kullanıcı için kapatma AYRI bir istek ve AÇIK bir onaydır.
        if (ikiAdimliKapat && hedef.IsTwoFactorEnabled)
        {
            Console.WriteLine("UYARI: İki adımlı doğrulama KAPATILACAK ve kurtarma kodları geçersiz kılınacak.");
            Console.WriteLine("Bu, hesabın ikinci savunma katmanını kaldırır; girişten sonra yeniden açın.");
            Console.Write("Onaylıyorsanız EVET yazın: ");
            if (!string.Equals(Console.ReadLine()?.Trim(), "EVET", StringComparison.Ordinal))
            {
                Console.WriteLine("Vazgeçildi. Hiçbir değişiklik yapılmadı.");
                return VazgecildiKod;
            }

            Console.WriteLine();
        }
        else if (ikiAdimliKapat)
        {
            Console.WriteLine("Not: Hesapta iki adımlı doğrulama zaten kapalı; --2fa-kapat yok sayıldı.");
            Console.WriteLine();
        }

        // 4) PAROLA. Ekrana YAZILMAZ, iki kez sorulur (yazım hatası kullanıcıyı yeniden dışarıda bırakır).
        var parola = ParolaOku($"Yeni parola (en az {PasswordPolicy.MinimumLength} karakter): ");
        if (parola.Length == 0)
        {
            Console.WriteLine("Vazgeçildi. Hiçbir değişiklik yapılmadı.");
            return VazgecildiKod;
        }

        var tekrar = ParolaOku("Yeni parola (tekrar): ");
        if (!string.Equals(parola, tekrar, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("HATA: Parolalar aynı değil. Hiçbir değişiklik yapılmadı.");
            return VazgecildiKod;
        }

        var sonuc = await ConsolePasswordRecovery.SetPasswordAsync(
            dbContext,
            kapsam.ServiceProvider.GetRequiredService<IPasswordHasher<PanelUser>>(),
            kapsam.ServiceProvider.GetRequiredService<JsonFileStore>(),
            hedef.Id,
            parola,
            ikiAdimliKapat,
            IsletmenAdi());

        Console.WriteLine();
        Console.WriteLine($"TAMAM: '{sonuc.UserName}' kullanıcısının parolası belirlendi.");
        Console.WriteLine($"  Giriş kilidi     : temizlendi");
        Console.WriteLine($"  Açık oturumlar   : düşecek (çerez damgası parola karmasından türer)");
        Console.WriteLine($"  API anahtarları  : {sonuc.RevokedApiTokens} adet iptal edildi");
        Console.WriteLine($"  İki adımlı       : {(sonuc.TwoFactorDisabled ? "KAPATILDI" : "değiştirilmedi")}");
        Console.WriteLine($"  Denetim kaydı    : auth.password_set_from_console (parola KAYDEDİLMEDİ)");

        if (!sonuc.IsActive)
        {
            // Sessiz geçilseydi kullanıcı "parolayı değiştirdim ama giremiyorum" duvarına toslardı.
            Console.WriteLine();
            Console.WriteLine("UYARI: Hesap PASİF; parola doğru olsa da giriş reddedilir.");
            Console.WriteLine("Başka bir yönetici hesabıyla girip bu kullanıcıyı etkinleştirin.");
        }

        Console.WriteLine();
        Console.WriteLine($"Panel adresi: {PanelAdresi.Bul()}");
        return BasariliKod;
    }

    /// <summary>Yönetici listesini yazar ve kullanıcıdan AÇIKÇA birini seçmesini ister.</summary>
    private static async Task<ConsoleAdminAccount?> SecimYapAsync(AdaPanelDbContext dbContext)
    {
        var yoneticiler = await ConsolePasswordRecovery.ListAdministratorsAsync(dbContext);
        if (yoneticiler.Count == 0)
        {
            throw new AppException(ConsolePasswordRecovery.NoAdministratorsKey, 404, 404);
        }

        Console.WriteLine("Yönetici hesapları:");
        for (var sira = 0; sira < yoneticiler.Count; sira++)
        {
            var hesap = yoneticiler[sira];
            var isaretler = new List<string>();
            if (!hesap.IsActive)
            {
                isaretler.Add("PASİF");
            }

            if (hesap.IsTwoFactorEnabled)
            {
                isaretler.Add("2FA açık");
            }

            var ek = isaretler.Count > 0 ? $"  [{string.Join(", ", isaretler)}]" : string.Empty;
            var sonGiris = hesap.LastLoginAtUtc is { } an ? an.ToLocalTime().ToString("g") : "hiç";
            Console.WriteLine($"  {sira + 1}) {hesap.UserName,-24} {hesap.Email,-32} son giriş: {sonGiris}{ek}");
        }

        // TEK yönetici olsa bile seçim SORULUR: "hangi hesabın parolası değişti" sorusunun yanıtı
        // varsayıma değil, kullanıcının verdiği bir girdiye dayanmalı.
        Console.WriteLine();
        Console.Write($"Numara seçin (1-{yoneticiler.Count}, boş bırakınca vazgeçilir): ");
        var girdi = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(girdi))
        {
            Console.WriteLine("Vazgeçildi. Hiçbir değişiklik yapılmadı.");
            return null;
        }

        if (!int.TryParse(girdi, out var secim) || secim < 1 || secim > yoneticiler.Count)
        {
            Console.Error.WriteLine("HATA: Geçersiz numara. Hiçbir değişiklik yapılmadı.");
            return null;
        }

        return yoneticiler[secim - 1];
    }

    /// <summary>
    /// Parolayı ekrana YAZMADAN okur. Girdi yönlendirilmişse (betikten çalıştırma) tuş tuş okuma
    /// mümkün olmadığı için satır olarak okunur — sessizce boş dönmek yerine çalışması esas.
    /// </summary>
    private static string ParolaOku(string soru)
    {
        Console.Write(soru);

        if (Console.IsInputRedirected)
        {
            var satir = Console.ReadLine() ?? string.Empty;
            Console.WriteLine();
            return satir;
        }

        var yazi = new StringBuilder();
        while (true)
        {
            var tus = Console.ReadKey(intercept: true);
            if (tus.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return yazi.ToString();
            }

            if (tus.Key == ConsoleKey.Escape)
            {
                Console.WriteLine();
                return string.Empty;
            }

            if (tus.Key == ConsoleKey.Backspace)
            {
                if (yazi.Length > 0)
                {
                    yazi.Length--;
                    Console.Write("\b \b");
                }

                continue;
            }

            if (char.IsControl(tus.KeyChar))
            {
                continue;
            }

            yazi.Append(tus.KeyChar);
            Console.Write('*');
        }
    }

    /// <summary>
    /// Kurtarma aracının ihtiyacı olan EN KÜÇÜK servis kümesi: veritabanı, parola karması ve dosya
    /// deposu. Panelin tam servis grafiği BİLEREK kurulmuyor — bu araç, panelin açılamadığı durumda
    /// da çalışmak zorunda.
    /// </summary>
    private static ServiceProvider SaglayiciKur()
    {
        // Yapılandırma panelin kendi katmanlamasıyla AYNI sırada okunur (appsettings → veri
        // kökündeki runtime-settings → ortam değişkenleri); aksi hâlde araç, panelin kullandığından
        // BAŞKA bir veritabanına yazabilirdi: runtime-settings.json bağlantı dizesini ezebiliyor ve
        // servis, `Panel__DataRoot` gibi bir ortam değişkeniyle başlatılmış olabilir.
        var onYukleme = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var veriKoku = Environment.ExpandEnvironmentVariables(
            onYukleme[$"{PanelRuntimeOptions.SectionName}:DataRoot"] ?? @"%ProgramData%\AdaPanel");

        var yapilandirma = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile(Path.Combine(veriKoku, "config", "runtime-settings.json"), optional: true)
            .AddEnvironmentVariables()
            .Build();

        var servisler = new ServiceCollection();
        servisler.AddAdaPanelData(yapilandirma);
        servisler.Configure<PanelRuntimeOptions>(yapilandirma.GetSection(PanelRuntimeOptions.SectionName));
        servisler.AddSingleton<JsonFileStore>();
        servisler.AddScoped<IPasswordHasher<PanelUser>, PasswordHasher<PanelUser>>();
        return servisler.BuildServiceProvider();
    }

    /// <summary>Denetim kaydına yazılacak Windows kimliği: "kim yaptı"nın tek doğru yanıtı.</summary>
    private static string IsletmenAdi()
    {
        try
        {
            return System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        }
        catch
        {
            return $"{Environment.UserDomainName}\\{Environment.UserName}";
        }
    }

    private static string HataMetni(AppException hata) => hata.MessageKey switch
    {
        ConsolePasswordRecovery.NoAdministratorsKey =>
            "HATA: Panelde hiç yönetici hesabı yok. Kurulum sihirbazı tamamlanmamış olabilir.",
        ConsolePasswordRecovery.TargetNotFoundKey =>
            $"HATA: '{hata.Details}' adında bir kullanıcı yok.",
        ConsolePasswordRecovery.TargetNotAdministratorKey =>
            $"HATA: {hata.Details} YÖNETİCİ değil. Bu araç yalnız yönetici parolası belirler; " +
            "müşteri/bayi parolası panelden değiştirilir.",
        _ => $"HATA: {hata.MessageKey} {hata.Details}".TrimEnd()
    };

    /// <summary>
    /// Çıktı kodlaması AÇIKÇA UTF-8: varsayılan konsol kod sayfasında Türkçe harfler bozuluyor
    /// ("çalışıyor" → "Ã§alÄ±ÅŸÄ±yor"). Kurtarma anında okunamaz çıktı aracı işe yaramaz kılar.
    /// </summary>
    private static void KonsoluHazirla()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;
        }
        catch
        {
            // Konsol yoksa/yönlendirilmişse ayarlanamayabilir; çıktı yine yazılır.
        }
    }
}
