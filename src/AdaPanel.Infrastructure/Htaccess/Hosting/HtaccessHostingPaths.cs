using System;
using System.Globalization;
using System.IO;
using System.Text;
// AYRILMIS AYGIT ADLARININ TEK KAYNAGI. Bu dosya net48 modul projesinde de derlendigi icin kural
// bir sure BURADA IKINCI KEZ yaziliydi; artik WindowsReservedNames.cs de ayni projeye kaynak
// olarak bagli (bkz. AdaPanel.HtaccessModule.csproj), dolayisiyla kopyaya gerek kalmadi.
using AdaPanel.Core.Common;

namespace AdaPanel.Infrastructure.Htaccess.Hosting;

/// <summary>
/// Modülün diskteki YERLERİNİN TEK KAYNAĞI.
///
/// NEDEN paylaşılan dosya: bu yolları İKİ AYRI SÜREÇ kullanıyor — panel servisi (net8, yazan taraf)
/// ve IIS içindeki modül (net48, okuyan taraf). İki tarafta ayrı ayrı yazılsaydı biri değişip diğeri
/// değişmediğinde panel "açtım" der, modül hiçbir zaman görmezdi: SESSİZ ARIZA. Bu yüzden dosya
/// <c>AdaPanel.HtaccessModule</c> projesine KAYNAK OLARAK bağlanır (bkz. o projenin csproj'u),
/// derlenmiş bir başvuru olarak değil — net8 derlemesi net48 CLR'ında yüklenemez.
///
/// NEDEN net48 uyumlu yazılmak zorunda: aynı dosya net48'de de derleniyor. Bu yüzden burada
/// System.Text.Json, Path.Join, HashCode.Combine, string.Contains(char) gibi yalnız .NET Core'da
/// bulunan API'ler KULLANILAMAZ. Değiştirirken buna dikkat et; ihlal net48 derlemesini kırar.
/// </summary>
internal static class HtaccessHostingPaths
{
    /// <summary>Veri kökü altındaki modül klasörü. Panel <c>DataRoot</c>'u ile AYNI kök olmalı.</summary>
    internal const string FolderName = "htaccess";

    /// <summary>
    /// Genel (sunucu geneli) anahtar dosyası — ACİL DURUM KAPATMA DÜĞMESİ.
    /// Bu dosya YOKSA modül hiçbir şey yapmaz. "Varsayılan KAPALI" tam olarak budur:
    /// kurulum bu dosyayı OLUŞTURMAZ, dolayısıyla yeni kurulan bir sunucuda modül yüklü ama ÖLÜDÜR.
    /// </summary>
    internal const string GlobalSwitchFileName = "module.switch";

    /// <summary>Site başına anahtar dosyalarının klasörü.</summary>
    internal const string SitesFolderName = "sites";

    /// <summary>Modülün kendi günlükleri (panel günlüğünden AYRI: modül panelin log altyapısını kullanamaz).</summary>
    internal const string LogFolderName = "logs";

    /// <summary>
    /// Modül kökünü çözer. <paramref name="dataRoot"/> panel tarafında <c>PanelRuntimeOptions.DataRoot</c>'tan,
    /// modül tarafında ortam değişkeninden gelir (bkz. <see cref="ResolveDataRootFromEnvironment"/>).
    /// </summary>
    internal static string Root(string dataRoot)
        => Path.Combine(ExpandFull(dataRoot), FolderName);

    internal static string GlobalSwitchPath(string dataRoot)
        => Path.Combine(Root(dataRoot), GlobalSwitchFileName);

    internal static string SitesRoot(string dataRoot)
        => Path.Combine(Root(dataRoot), SitesFolderName);

    internal static string LogRoot(string dataRoot)
        => Path.Combine(Root(dataRoot), LogFolderName);

    /// <summary>Bir sitenin anahtar dosyasının tam yolu.</summary>
    internal static string SiteSwitchPath(string dataRoot, string siteName)
        => Path.Combine(SitesRoot(dataRoot), SiteSwitchFileName(siteName));

    /// <summary>
    /// Veri kökünü ORTAMDAN okur — modül tarafının tek yolu, çünkü w3wp içinde panelin
    /// appsettings.json'ı ya da DI kabı YOKTUR.
    ///
    /// Sıra: <c>ADAPANEL_DATA_ROOT</c> (kurulum yazar) → <c>%ProgramData%\AdaPanel</c> (varsayılan).
    /// NEDEN ProgramData'yı Environment.GetFolderPath ile alıyoruz: sabit "C:\ProgramData" yazmak
    /// sistem sürücüsü D: olan sunucularda yanlış yere bakardı.
    /// </summary>
    internal static string ResolveDataRootFromEnvironment()
    {
        string? fromEnvironment = null;
        try
        {
            fromEnvironment = Environment.GetEnvironmentVariable("ADAPANEL_DATA_ROOT");
        }
        catch
        {
            // Ortam değişkeni okunamıyorsa (kısıtlı güven ortamı) varsayılana düş — HATA FIRLATMA.
        }

        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return ExpandFull(fromEnvironment!);
        }

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(programData, "AdaPanel");
    }

    /// <summary>
    /// Site adını GÜVENLİ bir dosya adına çevirir.
    ///
    /// NEDEN sadece "geçersiz karakterleri değiştir" YETMEZ — üç ayrı tuzak var ve üçü de gerçek:
    /// 1) Windows AYRILMIŞ AYGIT ADLARI (CON, PRN, AUX, NUL, COM1..9, LPT1..9): "con" adlı bir site
    ///    açılırsa "con.switch" dosyası OLUŞTURULAMAZ — panel "açtım" der, modül hiç göremez.
    /// 2) ÇAKIŞMA: "a:b" ve "a_b" temizlenince aynı ada düşerdi → bir kiracının anahtarı diğerininkini
    ///    ezerdi. Bu bir KİRACI İZOLASYONU açığı olurdu.
    /// 3) YOL KAÇIŞI: "..\..\evil" gibi bir ad dizin dışına yazdırabilirdi.
    ///
    /// Çözüm: temizlenmiş ad + orijinalin kısa özeti. Özet ÇAKIŞMAYI kırar, temizlenmiş ön ek dosyayı
    /// insan gözüyle okunur tutar (bir sorunda yönetici klasöre bakıp hangi site olduğunu görebilmeli).
    /// </summary>
    internal static string SiteSwitchFileName(string siteName)
        => SafeSiteKey(siteName) + ".switch";

    internal static string SafeSiteKey(string siteName)
    {
        var source = siteName ?? string.Empty;

        // Özet HER ZAMAN orijinal (kırpılmamış, küçük harfe çevrilmiş) addan alınır: temizlenmiş addan
        // alınsaydı çakışan iki ad aynı özeti üretir ve çakışmayı kırma amacı boşa giderdi.
        var digest = ShortDigest(source.Trim().ToLowerInvariant());

        // KÜÇÜK HARFE ÇEVRİLİR. Sebebi bir testin yakaladığı gerçek bir kırılganlık:
        // özet zaten küçük harfli addan alınıyordu ama ÖN EK orijinal harf durumunu koruyordu,
        // yani panel "MUSTERI.EXAMPLE-<ozet>.switch", modül "musteri.example-<ozet>.switch"
        // üretiyordu. NTFS harf duyarsız olduğu için bugün AYNI dosyaya düşüyorlar; ama
        // eşleme "dosya sistemi bizi kurtarsın"a bırakılamaz — harf duyarlı bir birimde
        // (ağ paylaşımı, ileride konteyner) panel ile modül AYRI dosyalara bakardı.
        var builder = new StringBuilder(source.Length + 1);
        foreach (var character in source.ToLowerInvariant())
        {
            var safe =
                (character >= 'a' && character <= 'z') ||
                (character >= '0' && character <= '9') ||
                character == '-' || character == '.';
            builder.Append(safe ? character : '_');
        }

        var cleaned = builder.ToString().Trim('.', '_', ' ');
        if (cleaned.Length > 48)
        {
            cleaned = cleaned.Substring(0, 48);
        }

        // Boş ya da ayrılmış ad kalırsa yalnız özet kullanılır.
        //
        // DÜRÜSTLÜK NOTU (mutasyon testiyle ölçüldü): bu satırdaki
        // "|| WindowsReservedNames.IsReserved(cleaned)" koşulu ARTIK YÜK TAŞIMIYOR. Kaldırıldığında hiçbir
        // test kırılmıyor, çünkü ayrılmış adı gerçekten etkisizleştiren şey aşağıda eklenen
        // ÖZET SON EKİ: "con" -> "con-<ozet>" zaten ayrılmış bir ad değildir. Özeti kaldıran
        // mutasyon ise 5 testi birden kırıyor — yani KORUMA TEST ALTINDA, sadece bu dal değil.
        //
        // Yine de BIRAKILIYOR: özetin biçimi ileride değişirse (kısaltma, farklı ayraç) bu dal
        // tek başına doğru cevabı vermeye devam eder. Bedeli bir karşılaştırma; kazancı,
        // sessizce oluşturulamayan bir dosyaya düşmemek.
        if (cleaned.Length == 0 || WindowsReservedNames.IsReserved(cleaned))
        {
            return "site-" + digest;
        }

        return cleaned + "-" + digest;
    }

    /// <summary>
    /// Kısa, kararlı, ÇAPRAZ SÜREÇ aynı sonucu veren özet.
    ///
    /// NEDEN string.GetHashCode DEĞİL: .NET Core'da karma her süreçte RASTGELELEŞTİRİLMİŞTİR
    /// (randomized hashing) ve net8 panel ile net48 modül FARKLI değerler üretirdi — panel bir dosya
    /// adına yazar, modül başka bir addan okurdu. Sessiz ve teşhisi çok zor bir arıza olurdu.
    /// Bu yüzden ELDE hesaplanan, sürümden bağımsız bir FNV-1a kullanılıyor.
    /// </summary>
    internal static string ShortDigest(string value)
    {
        unchecked
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;

            var hash = offset;
            var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            foreach (var b in bytes)
            {
                hash ^= b;
                hash *= prime;
            }

            return hash.ToString("x16", CultureInfo.InvariantCulture);
        }
    }

    private static string ExpandFull(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables((path ?? string.Empty).Trim());
        return expanded.Length == 0 ? expanded : Path.GetFullPath(expanded);
    }
}
