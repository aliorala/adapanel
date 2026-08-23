using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace AdaPanel.Infrastructure.Htaccess.Hosting;

/// <summary>Bir anahtarın panel tarafındaki görünümü.</summary>
internal sealed class HtaccessSwitchState
{
    internal HtaccessMode Mode { get; set; } = HtaccessMode.Off;

    /// <summary>Dosya diskte var mı. YOK = kapalı, ama "hiç açılmamış" ile "kapatılmış"ı ayırt ettirir.</summary>
    internal bool FileExists { get; set; }

    internal string Path { get; set; } = string.Empty;
}

/// <summary>
/// Anahtar dosyalarını YAZAN taraf (panel servisi). Okuyan taraf modüldür
/// (<see cref="HtaccessRuntimeGate"/>) ve İKİSİ AYNI biçimi/yolları kullanır
/// (<see cref="HtaccessSwitchFile"/>, <see cref="HtaccessHostingPaths"/>).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class HtaccessSwitchStore
{
    private readonly string dataRoot;

    internal HtaccessSwitchStore(string dataRoot)
    {
        this.dataRoot = dataRoot;
    }

    /// <summary>
    /// SUNUCU GENELİ ANAHTAR. <paramref name="mode"/> <see cref="HtaccessMode.Off"/> ise dosya
    /// SİLİNİR — "enabled=0 yaz" yerine silmek bilinçli bir tercih:
    /// silinmiş dosya, ayrıştırıcı ne kadar bozulursa bozulsun KAPALI demektir. Kapatma düğmesinin
    /// doğruluğu, çalışan hiçbir koda bağlı olmamalı.
    /// </summary>
    internal void SetGlobal(HtaccessMode mode, string? note)
    {
        var path = HtaccessHostingPaths.GlobalSwitchPath(dataRoot);
        if (mode == HtaccessMode.Off)
        {
            DeleteIfExists(path);
            return;
        }

        EnsureDirectories();
        WriteAtomic(path, HtaccessSwitchFile.Render(mode, note));
    }

    internal void SetSite(string siteName, HtaccessMode mode, string? note)
    {
        var path = HtaccessHostingPaths.SiteSwitchPath(dataRoot, siteName);
        if (mode == HtaccessMode.Off)
        {
            DeleteIfExists(path);
            return;
        }

        EnsureDirectories();
        WriteAtomic(path, HtaccessSwitchFile.Render(mode, note));
    }

    internal HtaccessSwitchState ReadGlobal() => Read(HtaccessHostingPaths.GlobalSwitchPath(dataRoot));

    internal HtaccessSwitchState ReadSite(string siteName)
        => Read(HtaccessHostingPaths.SiteSwitchPath(dataRoot, siteName));

    private static HtaccessSwitchState Read(string path)
    {
        var state = new HtaccessSwitchState { Path = path };
        try
        {
            if (!File.Exists(path))
            {
                return state;
            }

            state.FileExists = true;
            state.Mode = HtaccessSwitchFile.Parse(File.ReadAllText(path));
        }
        catch
        {
            // Panel tarafında da okunamama = KAPALI göster. Panelin "açık" deyip modülün kapalı
            // olması, tam tersinden çok daha tehlikeli bir yalan olurdu.
            state.Mode = HtaccessMode.Off;
        }

        return state;
    }

    /// <summary>
    /// Klasörleri oluşturur. ERİŞİM İZİNLERİNE DOKUNMAZ.
    ///
    /// <see cref="HardenAcl"/>'den AYRI olması bilinçli ve bir testin bulgusu:
    /// ikisi birlikteyken her anahtar değişikliği ACL'i YENİDEN uyguluyordu. İki zararı vardı —
    /// (a) yöneticinin bilerek yaptığı bir izin düzenlemesi ilk açma/kapamada geri alınırdı,
    /// (b) ACL uygulaması başarısız olduğunda anahtarı yazmak da imkânsız hale geliyordu, yani
    /// ikincil bir adım ASIL işlevi engelliyordu. Sertleştirme kurulumda BİR KEZ yapılır.
    /// </summary>
    internal void EnsureDirectories()
    {
        foreach (var path in new[]
                 {
                     HtaccessHostingPaths.Root(dataRoot),
                     HtaccessHostingPaths.SitesRoot(dataRoot),
                     HtaccessHostingPaths.LogRoot(dataRoot)
                 })
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
        }
    }

    /// <summary>
    /// ERİŞİM İZİNLERİNİ kurar. Kurulumda / panel açılışında BİR KEZ çağrılır.
    ///
    /// GEREKSİNİM ÇİFTİ (ikisi birden sağlanmak zorunda):
    ///  - IIS_IUSRS OKUYABİLMELİ: anahtar dosyasını okuyan taraf, kiracının uygulama havuzu
    ///    kimliğiyle çalışan w3wp'dir. Okuyamazsa modül sessizce KAPALI kalır ve özellik hiç
    ///    çalışmaz (fail-open sayesinde site bozulmaz, ama panel yalan söylemiş olur).
    ///  - IIS_IUSRS YAZAMAMALI: yazabilseydi bir kiracı kendi PHP'siyle genel anahtarı
    ///    değiştirip BÜTÜN SUNUCUDA modülü açıp kapatabilirdi. Bu bir kiracı izolasyonu açığıdır.
    ///
    /// Miras KAPATILIYOR (SetAccessRuleProtection(true, false)) ve DACL SIFIRDAN kuruluyor:
    /// bu deponun 0.6.5 turundaki ölçülmüş dersi tam olarak buydu — kökte ne varsa
    /// (bu makinede Everyone=FullControl) miras alınıp hassas klasörlere KALICI gömülüyordu.
    /// </summary>
    internal void HardenAcl()
    {
        EnsureDirectories();
        var root = HtaccessHostingPaths.Root(dataRoot);

        ApplyAcl(root, allowIisWrite: false);

        // Günlük klasörü TEK İSTİSNA: modül günlüğü w3wp içinden yazılır, dolayısıyla IIS_IUSRS
        // buraya YAZABİLMELİ. Ayrı klasör olmasının sebebi budur — yazma hakkı anahtar
        // dosyalarının bulunduğu klasöre SIZMASIN.
        ApplyAcl(HtaccessHostingPaths.LogRoot(dataRoot), allowIisWrite: true);
    }

    private static void ApplyAcl(string path, bool allowIisWrite)
    {
        var directory = new DirectoryInfo(path);
        var security = new DirectorySecurity();

        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        // S-1-5-32-568 = IIS_IUSRS. WellKnownSidType'ta karşılığı yok, SDDL ile kuruluyor
        // (deponun harden-adapanel.ps1'inde de aynı SID aynı gerekçeyle elle yazılıyor).
        var iisUsers = new SecurityIdentifier("S-1-5-32-568");

        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        security.AddAccessRule(new FileSystemAccessRule(
            administrators,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        security.AddAccessRule(new FileSystemAccessRule(
            system,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        var iisRights = allowIisWrite
            ? FileSystemRights.ReadAndExecute | FileSystemRights.Write
            : FileSystemRights.ReadAndExecute;

        security.AddAccessRule(new FileSystemAccessRule(
            iisUsers,
            iisRights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        // ÖNCE DACL. Güvenlik açısından belirleyici olan budur: kimin okuyup yazabileceği.
        directory.SetAccessControl(security);

        // SAHİPLİK AYRI ve EN İYİ ÇABA — bilerek.
        //
        // Bir test bunu yakaladı: SetOwner(Administrators) çağrısı, çağıran yönetici DEĞİLSE
        // (SeRestorePrivilege yoksa) "The security identifier is not allowed to be the owner of
        // this object" ile PATLIYOR. Aynı nesne üzerinde DACL ile birlikte uygulandığında bu
        // istisna TÜM ACL kurulumunu iptal ediyordu ve klasör MİRAS ALINMIŞ izinlerle
        // (bu makinede Everyone=FullControl) ortada kalıyordu — yani sahiplik gibi İKİNCİL bir
        // ayrıntı, ASIL korumayı düşürüyordu. Tam tersi bir öncelik sıralaması.
        //
        // Üretimde panel LocalSystem olarak çalışır (scripts/install-service.ps1 -> New-Service,
        // -Credential yok => LocalSystem) ve sahipliği atayabilir. Atayamadığı ortamlarda ise
        // DACL yine de yerindedir; kaybedilen tek şey sahipliktir.
        try
        {
            var ownership = new DirectorySecurity();
            ownership.SetOwner(administrators);
            directory.SetAccessControl(ownership);
        }
        catch (Exception)
        {
            // Sahiplik atanamadı. DACL uygulandı; koruma yerinde.
        }
    }

    /// <summary>
    /// ATOMİK yazım: geçici dosyaya yaz + üzerine taşı.
    ///
    /// NEDEN: modül bu dosyayı HER TTL'de okuyor. Doğrudan üzerine yazılsaydı, okuma tam yazımın
    /// ortasına denk geldiğinde YARIM içerik okunur, "enabled=1" satırı henüz yazılmamış olabilir
    /// ve modül o an KAPANIRDI. Kapatma tarafında zararsız, ama AÇMA tarafında rastgele
    /// çalışan/çalışmayan bir özellik demekti.
    /// </summary>
    private static void WriteAtomic(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory!);
        }

        var temporary = path + ".tmp" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, content);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch
                {
                    // En iyi çaba; artık geçici dosya zararsız.
                }
            }
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
