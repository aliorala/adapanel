using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using AdaPanel.Infrastructure.Htaccess.Htpasswd;

namespace AdaPanel.Infrastructure.Htaccess.Hosting;

/// <summary>
/// KORUMALI DİZİN BETİMLEYİCİSİNİ YAZAN TARAF (panel).
/// </summary>
/// <remarks>
/// <para>
/// Okuyan taraf <see cref="ProtectedDirectoryGate"/>'tir (IIS modülü, net48) ve İKİSİ AYNI
/// biçimi/yolları kullanır (<see cref="ProtectedDirectoryFile"/>). Aynı ayrım
/// <see cref="HtaccessSwitchStore"/> ile anahtar dosyalarında da var ve sebebi aynı: yazan taraf
/// ACL kurmak için <c>System.Security.AccessControl</c>'e ihtiyaç duyar, okuyan tarafın buna
/// ihtiyacı yoktur ve o bağımlılığı w3wp içine taşımanın hiçbir karşılığı olmazdı.
/// </para>
/// <para>
/// <b>ERİŞİM İZİNLERİ, bu sınıfın var olma sebeplerinden biridir.</b> Buradaki dosyalar parola
/// ÖZETİ taşır. Özet düz metinden çok daha iyidir ama sınırsız dağıtılacak bir şey değildir:
/// eline geçen kişi çevrimdışı kaba kuvvet deneyebilir. Bu yüzden klasör miras ALMAZ ve DACL
/// sıfırdan kurulur (bu deponun 0.6.5 turundaki ölçülmüş dersi: kökteki
/// <c>Everyone=FullControl</c> hassas klasörlere kalıcı olarak gömülüyordu).
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class ProtectedDirectoryStore(string dataRoot)
{
    /// <summary>
    /// bcrypt maliyeti.
    /// </summary>
    /// <remarks>
    /// <b>10 SEÇİLDİ ve iki sınır arasında sıkışıyor.</b> Alt sınır: özet, panelin veri kökünde
    /// dursa da bir gün bir yedeğe ya da bir destek dökümüne düşebilir; çevrimdışı denemeyi pahalı
    /// tutmak gerekir. Üst sınır: doğrulama İSTEK YOLUNDA yapılıyor ve maliyet her artışta
    /// süreyi İKİYE katlıyor — 14, tek bir sayfa yüklemesini saniyelere çıkarırdı.
    /// <see cref="ProtectedDirectoryGate"/>'teki başarı önbelleği bu bedeli oturum başına bir kez
    /// ödetiyor; ikisi birlikte tasarlandı. Ayrıca <c>HtaccessLimits.MaxBcryptCost</c> (15) ile
    /// uyumlu olmak ZORUNDA: aşsaydık kendi doğrulayıcımız ürettiğimiz özeti reddederdi.
    /// </remarks>
    internal const int BcryptCost = 10;

    /// <summary>Bir sitenin kayıtlarını okur. Dosya yoksa BOŞ liste döner.</summary>
    internal IList<ProtectedDirectoryRecord> Read(string siteName)
    {
        var path = ProtectedDirectoryFile.ListPath(dataRoot, siteName);
        try
        {
            return File.Exists(path)
                ? ProtectedDirectoryFile.Parse(File.ReadAllText(path))
                : new List<ProtectedDirectoryRecord>();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Okunamadı. BOŞ liste dönmek "koruma yok" demek olurdu; çağıran bunu ayırt
            // edebilsin diye istisna YUKARI verilir — panel "ölçemedim" diyebilmeli.
            throw;
        }
    }

    /// <summary>
    /// Bir korumayı kurar/günceller: parola dosyasını (bcrypt) ve betimleyiciyi yazar.
    /// </summary>
    /// <returns>Yazılan kayıt.</returns>
    internal ProtectedDirectoryRecord Upsert(
        string siteName,
        string relativePath,
        string userName,
        string password,
        string realm,
        string? applicationPoolName)
    {
        var siteFolder = ProtectedDirectoryFile.SiteRoot(dataRoot, siteName);

        // ACL YALNIZ KLASÖR YENİ OLUŞTURULURKEN kurulur. Her yazımda yeniden uygulamak,
        // <see cref="HtaccessSwitchStore.EnsureDirectories"/> yorumunda ölçülmüş iki zarar
        // getirirdi: yöneticinin bilerek yaptığı bir izin düzenlemesi ilk değişiklikte geri
        // alınır ve ACL uygulaması başarısız olduğunda ASIL işlev (korumayı kurmak) da engellenirdi.
        var yeni = !Directory.Exists(siteFolder);
        Directory.CreateDirectory(siteFolder);
        if (yeni)
        {
            try
            {
                HardenAcl(siteFolder, applicationPoolName);
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or PrivilegeNotHeldException or IOException)
            {
                // Sertleştirilemedi. Koruma YİNE DE kurulur: üst klasör (%ProgramData%\AdaPanel)
                // kurulum tarafından zaten sertleştiriliyor, yani miras alınan izinler de
                // yönetici/SYSTEM ile sınırlıdır. Korumayı hiç kurmamak, klasörü herkese açık
                // bırakmak olurdu — ölçülebilir biçimde daha kötü.
            }
        }

        var record = new ProtectedDirectoryRecord
        {
            RelativePath = ProtectedDirectoryFile.NormalizePath(relativePath),
            Realm = realm,
            UserName = userName,
            PasswordFileName = ProtectedDirectoryFile.PasswordFileName(relativePath)
        };

        // ÖNCE PAROLA DOSYASI, SONRA BETİMLEYİCİ. Sıra tersine olsaydı, iki yazım arasında gelen
        // bir istek "korumalı ama parola dosyası yok" durumunu görürdü. Kapı o durumda
        // fail-closed davranıp 401 döner — yani en kötüsü geçici bir kilit; ama doğru sırayla
        // o pencere hiç açılmaz.
        WriteAtomic(
            Path.Combine(siteFolder, record.PasswordFileName),
            ProtectedDirectoryFile.RenderPasswordFile(userName, BCryptVerifier.Generate(password, BcryptCost)));

        var records = Read(siteName)
            .Where(item => !string.Equals(item.RelativePath, record.RelativePath, StringComparison.Ordinal))
            .ToList();
        records.Add(record);
        WriteAtomic(ProtectedDirectoryFile.ListPath(dataRoot, siteName), ProtectedDirectoryFile.Render(records));

        return record;
    }

    /// <summary>Bir korumayı kaldırır. <c>true</c> = kayıt vardı ve silindi.</summary>
    internal bool Remove(string siteName, string relativePath)
    {
        var normalized = ProtectedDirectoryFile.NormalizePath(relativePath);
        var records = Read(siteName);
        var kalan = records
            .Where(item => !string.Equals(item.RelativePath, normalized, StringComparison.Ordinal))
            .ToList();

        if (kalan.Count == records.Count)
        {
            return false;
        }

        // ÖNCE BETİMLEYİCİ. Kapı artık bu yolu korumalı saymaz; parola dosyasının kalması
        // güvenlik açığı değil, yalnız çöptür. Ters sırada, betimleyici hâlâ "korumalı" derken
        // parola dosyası gitmiş olur ve klasör fail-closed yüzünden ERİŞİLEMEZ hâle gelirdi.
        WriteAtomic(ProtectedDirectoryFile.ListPath(dataRoot, siteName), ProtectedDirectoryFile.Render(kalan));

        var passwordFile = Path.Combine(
            ProtectedDirectoryFile.SiteRoot(dataRoot, siteName),
            ProtectedDirectoryFile.PasswordFileName(normalized));
        try
        {
            if (File.Exists(passwordFile))
            {
                File.Delete(passwordFile);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Silinemedi (kilit). Koruma zaten kalktı; artakalan dosya bir sonraki kurulumda üzerine yazılır.
        }

        return true;
    }

    /// <summary>Sitenin bütün korumalı dizin verisini siler (site silinirken).</summary>
    internal void RemoveSite(string siteName)
    {
        var siteFolder = ProtectedDirectoryFile.SiteRoot(dataRoot, siteName);
        try
        {
            if (Directory.Exists(siteFolder))
            {
                // Bu klasör PANELİN verisidir, müşterinin içeriği DEĞİL — silmemek, silinmiş bir
                // sitenin parola özetlerini sunucuda süresiz bırakmak olurdu.
                Directory.Delete(siteFolder, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // En iyi çaba: site silme işlemi bu yüzden başarısız SAYILMAZ.
        }
    }

    /// <summary>
    /// Klasörün DACL'ini kurar.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>GEREKSİNİM ÇİFTİ:</b> (a) doğrulamayı yapan taraf w3wp'dir, o yüzden sitenin uygulama
    /// havuzu kimliği OKUYABİLMELİ — okuyamazsa kapı fail-closed davranıp klasörü tamamen
    /// erişilemez yapar; (b) BAŞKA kiracılar OKUYAMAMALI — okuyabilselerdi bir kiracının PHP'si
    /// diğerinin parola özetini alıp çevrimdışı deneyebilirdi.
    /// </para>
    /// <para>
    /// <b>Havuz kimliği çözülemezse IIS_IUSRS'a düşülür ve bu DÜRÜSTÇE bir tavizdir:</b> özel
    /// hesapla çalışan ya da adı okunamayan havuzlarda tek seçenek, korumayı hiç kurmamak ile
    /// özeti bütün kiracılara okutmak arasındadır. Koruma kurulmazsa klasör HERKESE AÇIK kalır;
    /// düşülen hâlde ise sızabilecek şey bir bcrypt özetidir. İkincisi ölçülebilir biçimde daha
    /// az zararlı. Panel bu tavizi kullanıcıya söyleyemiyor — bilinen sınır.
    /// </para>
    /// </remarks>
    private static void HardenAcl(string path, string? applicationPoolName)
    {
        var directory = new DirectoryInfo(path);
        var security = new DirectorySecurity();

        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        // YAZAN, YAZDIĞINA ERİŞİMİNİ KAYBETMEMELİ. Üretimde panel LocalSystem'dir
        // (scripts/install-service.ps1 -> New-Service, -Credential yok) ve zaten aşağıdaki
        // listede; bu ACE orada ETKİSİZDİR. Var olma sebebi, panelin bir hizmet hesabıyla
        // çalıştığı (ya da testin kullanıcı bağlamında koştuğu) kurulumlarda mirası kestikten
        // sonra kendi klasörüne erişemez hâle gelmemesi. Kilitlenen bir panel, korumayı
        // kaldıramayan bir panel demektir.
        var yazan = TryGetCurrentUser();
        foreach (var identity in yazan is null
                     ? new IdentityReference[] { administrators, system }
                     : new IdentityReference[] { administrators, system, yazan })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                identity,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        security.AddAccessRule(new FileSystemAccessRule(
            OkuyanKimlik(applicationPoolName),
            FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        directory.SetAccessControl(security);
    }

    /// <summary>Süreci çalıştıran kimlik; okunamazsa <c>null</c> (ACL yine de kurulur).</summary>
    private static IdentityReference? TryGetCurrentUser()
    {
        try
        {
            return WindowsIdentity.GetCurrent().User;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Parola dosyasını okuyacak kimlik. Havuza özel kimlik ÇÖZÜLEBİLİYORSA o, yoksa IIS_IUSRS.
    /// </summary>
    private static IdentityReference OkuyanKimlik(string? applicationPoolName)
    {
        if (!string.IsNullOrWhiteSpace(applicationPoolName))
        {
            try
            {
                // "IIS AppPool\<havuz>" — ApplicationPoolIdentity'nin sanal hesabı. Havuz özel bir
                // hesapla çalışıyorsa bu ad ÇÖZÜLMEZ ve aşağıdaki geri düşüş devreye girer.
                return (SecurityIdentifier)new NTAccount("IIS AppPool\\" + applicationPoolName!.Trim())
                    .Translate(typeof(SecurityIdentifier));
            }
            catch (Exception exception) when (
                exception is IdentityNotMappedException or SystemException)
            {
                // Çözülemedi. Gerekçesi HardenAcl'in yorumunda.
            }
        }

        // S-1-5-32-568 = IIS_IUSRS. WellKnownSidType'ta karşılığı yok (aynı SID aynı gerekçeyle
        // HtaccessSwitchStore'da ve harden-adapanel.ps1'de de elle yazılıyor).
        return new SecurityIdentifier("S-1-5-32-568");
    }

    /// <summary>
    /// ATOMİK yazım: geçici dosyaya yaz + üzerine taşı.
    /// </summary>
    /// <remarks>
    /// Modül bu dosyaları TTL süresince önbellekleyip yeniden okuyor. Doğrudan üzerine
    /// yazılsaydı, okuma yazımın ortasına denk geldiğinde YARIM içerik okunurdu; yarım bir
    /// betimleyicinin anlamı "bazı korumalar yok"tur — yani sessizce açılan bir klasör.
    /// Aynı gerekçe <see cref="HtaccessSwitchStore"/> için de yazılı.
    /// </remarks>
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
            // UTF-8, BOM'SUZ: ayrıştırıcı ilk satırı okurken BOM'u anahtarın parçası sayardı.
            File.WriteAllText(temporary, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // En iyi çaba; artık geçici dosya zararsız.
                }
            }
        }
    }
}
