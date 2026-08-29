using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AdaPanel.Core.Dtos.Htaccess;
using AdaPanel.Infrastructure.Htaccess.Htpasswd;

namespace AdaPanel.Infrastructure.Htaccess.Hosting;

/// <summary>Korumalı dizin kapısının bir istek için verdiği karar.</summary>
internal enum ProtectedDirectoryOutcome
{
    /// <summary>İstek korumalı bir dizine ait değil. İstek DOKUNULMADAN geçer.</summary>
    NotProtected = 0,

    /// <summary>Kimlik doğrulandı; istek geçebilir.</summary>
    Granted = 1,

    /// <summary>401 döndürülecek. <see cref="ProtectedDirectoryDecision.Realm"/> başlığa yazılır.</summary>
    Challenge = 2
}

/// <summary>Kapının kararı. <c>Challenge</c> dışındaki hâllerde <see cref="Realm"/> anlamsızdır.</summary>
internal sealed class ProtectedDirectoryDecision
{
    internal static readonly ProtectedDirectoryDecision None =
        new ProtectedDirectoryDecision { Outcome = ProtectedDirectoryOutcome.NotProtected };

    internal ProtectedDirectoryOutcome Outcome;

    internal string Realm = string.Empty;
}

/// <summary>
/// KORUMALI DİZİN KAPISI — parola korumasının istek yolundaki tek karar noktası.
/// </summary>
/// <remarks>
/// <para>
/// <b>NEDEN <c>.htaccess</c> ANAHTARINDAN AYRI:</b> ölçüldü — <see cref="HtaccessEngineBridge"/>
/// <c>AuthenticationRequired</c> sonucunu bilerek "karar yok"a çeviriyor, yani bugün bir
/// <c>.htaccess</c> içindeki <c>Require valid-user</c> HİÇBİR ŞEY yapmıyor. Üstelik
/// <c>.htaccess</c> desteği sunucuda VARSAYILAN KAPALIDIR (<see cref="HtaccessRuntimeGate"/>).
/// Parola korumasını o anahtara bağlamak, kullanıcının "korumalı" dediği bir klasörün
/// SESSİZCE herkese açık kalması demekti. Bu kapının kendi durumu vardır ve
/// <c>.htaccess</c> kipi kapalıyken de çalışır.
/// </para>
/// <para>
/// <b>NEDEN FAIL-OPEN DEĞİL (modülün geri kalanının aksine):</b> modülün her yerinde "bilmiyorsan
/// dokunma" kuralı geçerlidir, çünkü oradaki hata müşterinin sitesini bozar. Burada tersi
/// geçerlidir: bir korumanın VAR OLDUĞUNU ölçtükten sonra doğrulayamamak (parola dosyası
/// okunamıyor, biçimi bozuk) isteği içeri almak için sebep değildir — açık kapı, bozuk kapıdan
/// beterdir. Sıra şudur: <b>koruma var mı</b> sorusuna cevap verilemiyorsa DOKUNMA (fail-open);
/// koruma OLDUĞU ölçüldüyse ve doğrulama yapılamıyorsa 401 (fail-closed).
/// </para>
/// <para>
/// <b>SICAK YOL:</b> koruması olmayan bir sitede maliyet, alan okuması + bir karşılaştırmadır
/// (TTL dolmadıkça diske gidilmez). <see cref="HtaccessRuntimeGate"/> ile aynı ölçülmüş desen:
/// yol KURUCUDA bir kez üretilir, çünkü orada ölçüldüğü gibi her istekte dizgi üretmek de
/// kabul edilemez.
/// </para>
/// <para>
/// <b>ÖRNEKLENMİŞ, static DEĞİL:</b> her uygulama havuzu ayrı AppDomain → bir kiracının
/// önbelleği diğerine GÖRÜNMEZ.
/// </para>
/// </remarks>
internal sealed class ProtectedDirectoryGate
{
    /// <summary>
    /// BAŞARILI doğrulama önbelleğinin ömrü.
    /// </summary>
    /// <remarks>
    /// <b>Bu sabit olmadan özellik kullanılamazdı.</b> bcrypt bilerek yavaştır: maliyet 10'da tek
    /// doğrulama bu sınıfın hedeflediği donanımda on milisaniyeler sürer. Tarayıcı korunan bir
    /// sayfadaki HER varlık için (css, js, resim) <c>Authorization</c> başlığını yeniden gönderir;
    /// önbelleksiz bir sayfa yüklemesi otuz bcrypt turu demek olurdu. Ayrıca bu, parolayı bilmeyen
    /// birinin bile sunucuya CPU harcatabildiği bir hizmet dışı bırakma yüzeyidir.
    /// YALNIZCA BAŞARI önbelleğe alınır: başarısızlık her seferinde yeniden hesaplanır, yani kaba
    /// kuvvet denemesi bcrypt'in tam bedelini ödemeye devam eder.
    /// </remarks>
    private static readonly TimeSpan BasariOnbellegiOmru = TimeSpan.FromSeconds(60);

    /// <summary>Önbellek üst sınırı: uydurma kimliklerle bellek şişirilmesin.</summary>
    private const int MaxBasariGirdisi = 256;

    /// <summary>Site betimleyicisi önbelleğinin üst sınırı (bkz. <see cref="HtaccessRuntimeGate"/>).</summary>
    private const int MaxSiteEntries = 64;

    private sealed class CachedList
    {
        internal List<ProtectedDirectoryRecord> Records = new List<ProtectedDirectoryRecord>();
        internal long ExpiresAtTicks;
        internal string ListPath = string.Empty;
        internal string SiteFolder = string.Empty;

        /// <summary>
        /// Betimleyici OKUNABİLDİ mi. false = "bakamadım" (fail-open); true + boş liste =
        /// "baktım, koruma yok". İkisini ayırmayan bir alan, tam da bu depoda daha önce
        /// yakalanmış "ölçemedin diye yok deme" hatasını tekrarlardı.
        /// </summary>
        internal bool Readable;
    }

    private readonly string dataRoot;
    private readonly long ttlTicks;
    private readonly long basariTicks;
    private readonly IHtaccessSwitchReader reader;
    private readonly Func<long> monotonicTicks;

    private readonly ConcurrentDictionary<string, CachedList> siteLists =
        new ConcurrentDictionary<string, CachedList>(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, long> basariOnbellegi =
        new ConcurrentDictionary<string, long>(StringComparer.Ordinal);

    internal ProtectedDirectoryGate(
        string dataRoot,
        TimeSpan ttl,
        IHtaccessSwitchReader? reader = null,
        Func<long>? monotonicTicks = null)
    {
        this.dataRoot = dataRoot;
        this.reader = reader ?? new FileSystemSwitchReader();
        this.monotonicTicks = monotonicTicks ?? Stopwatch.GetTimestamp;

        var ticks = (long)(ttl.TotalSeconds * Stopwatch.Frequency);
        this.ttlTicks = ticks < 0 ? 0 : ticks;
        this.basariTicks = (long)(BasariOnbellegiOmru.TotalSeconds * Stopwatch.Frequency);
    }

    /// <summary>
    /// Bir istek için kararı üretir. <b>HİÇBİR KOŞULDA istisna fırlatmaz.</b>
    /// </summary>
    /// <param name="siteName">IIS site adı; boşsa hangi kiracı olduğumuzu bilmiyoruz → dokunma.</param>
    /// <param name="documentRoot">Uygulamanın fiziksel kökü (panelin kaydı da buna göredir).</param>
    /// <param name="physicalPath">İsteğin fiziksel karşılığı. Boşsa <paramref name="virtualPath"/> kullanılır.</param>
    /// <param name="virtualPath">İsteğin sanal yolu (<c>/yonetim/index.php</c>).</param>
    /// <param name="authorizationHeader">Ham <c>Authorization</c> başlığı (<c>null</c> olabilir).</param>
    internal ProtectedDirectoryDecision Decide(
        string? siteName,
        string? documentRoot,
        string? physicalPath,
        string? virtualPath,
        string? authorizationHeader)
    {
        try
        {
            if (string.IsNullOrEmpty(siteName))
            {
                return ProtectedDirectoryDecision.None;
            }

            var entry = OkuOnbellekli(siteName!);
            if (!entry.Readable || entry.Records.Count == 0)
            {
                // Betimleyici yok ya da okunamadı: KORUMA OLDUĞUNU ÖLÇEMEDİK → dokunma.
                return ProtectedDirectoryDecision.None;
            }

            var istekYolu = IstekYolu(documentRoot, physicalPath, virtualPath);
            if (istekYolu is null)
            {
                // Yol çözülemedi. Burada 401 dönmek, koruma OLMAYAN dizinleri de kapatabilirdi;
                // "koruma var mı" sorusuna cevap veremediğimiz için fail-open doğru olan.
                return ProtectedDirectoryDecision.None;
            }

            ProtectedDirectoryRecord? eslesen = null;
            foreach (var record in entry.Records)
            {
                if (!ProtectedDirectoryFile.Covers(record.RelativePath, istekYolu))
                {
                    continue;
                }

                // EN UZUN eşleşme kazanır: /a ve /a/b ayrı korunuyorsa istek /a/b/c için geçerli
                // olan /a/b'dir. Kısa olanı seçmek, iç içe konulmuş bir korumayı dıştakinin
                // parolasıyla açılabilir kılardı.
                if (eslesen is null || record.RelativePath.Length > eslesen.RelativePath.Length)
                {
                    eslesen = record;
                }
            }

            if (eslesen is null)
            {
                return ProtectedDirectoryDecision.None;
            }

            // BURADAN SONRASI FAIL-CLOSED: korumanın varlığı ÖLÇÜLDÜ.
            return Dogrula(entry, eslesen, authorizationHeader)
                ? new ProtectedDirectoryDecision { Outcome = ProtectedDirectoryOutcome.Granted }
                : new ProtectedDirectoryDecision
                {
                    Outcome = ProtectedDirectoryOutcome.Challenge,
                    Realm = TemizRealm(eslesen.Realm)
                };
        }
        catch (Exception)
        {
            // Buraya düşmek sözleşmenin ihlalidir. Bedelini müşterinin sitesi ÖDEMEZ:
            // "koruma var mı" sorusunun cevabını da bilmediğimiz için istek dokunulmadan geçer.
            return ProtectedDirectoryDecision.None;
        }
    }

    /// <summary>
    /// İsteğin, uygulama köküne göre göreli yolu. Fiziksel yol TERCİH EDİLİR.
    /// </summary>
    /// <remarks>
    /// Sanal yol saldırganın yazdığı dizgedir (yüzde kodlaması, çift eğik çizgi, nokta parçaları);
    /// fiziksel yolu IIS zaten kanonikleştirmiştir. Kapsam kararını kanonik olana dayandırmak,
    /// <c>/yonetim/../yonetim/gizli</c> gibi kaçış denemelerini yapısal olarak elerdi.
    /// Fiziksel yol yoksa (eşlenemeyen istek) sanal yola düşülür — o hâlde de karşılaştırma
    /// <see cref="ProtectedDirectoryFile.NormalizePath"/>'ten geçer.
    /// </remarks>
    private static string? IstekYolu(string? documentRoot, string? physicalPath, string? virtualPath)
    {
        if (!string.IsNullOrEmpty(documentRoot) && !string.IsNullOrEmpty(physicalPath))
        {
            var kok = documentRoot!.TrimEnd('/', '\\');
            if (physicalPath!.Length > kok.Length &&
                physicalPath.StartsWith(kok, StringComparison.OrdinalIgnoreCase) &&
                (physicalPath[kok.Length] == '\\' || physicalPath[kok.Length] == '/'))
            {
                return ProtectedDirectoryFile.NormalizePath(physicalPath.Substring(kok.Length + 1));
            }

            if (string.Equals(physicalPath.TrimEnd('/', '\\'), kok, StringComparison.OrdinalIgnoreCase))
            {
                // İstek doğrudan uygulama kökü: hiçbir alt klasör koruması kapsamaz.
                return string.Empty;
            }
        }

        return string.IsNullOrEmpty(virtualPath) ? null : ProtectedDirectoryFile.NormalizePath(virtualPath);
    }

    private bool Dogrula(CachedList entry, ProtectedDirectoryRecord record, string? authorizationHeader)
    {
        if (string.IsNullOrEmpty(authorizationHeader))
        {
            return false;
        }

        var onbellekAnahtari = OnbellekAnahtari(entry.SiteFolder, record.RelativePath, authorizationHeader!);
        var now = monotonicTicks();
        if (basariOnbellegi.TryGetValue(onbellekAnahtari, out var expires) && now < expires)
        {
            return true;
        }

        if (!BasicCoz(authorizationHeader!, out var kullanici, out var parola))
        {
            return false;
        }

        // Kullanıcı adı KAYITTAKİ ile aynı olmak zorunda. Parola dosyasında birden çok satır
        // olsaydı (yönetici elle eklemiş olabilir) yalnız kayıtlı ad kabul edilir: panelin
        // gösterdiği kullanıcı ile içeri giren kullanıcı AYNI olmalı.
        if (!string.Equals(kullanici, record.UserName, StringComparison.Ordinal))
        {
            return false;
        }

        var parolaDosyasi = Path.Combine(entry.SiteFolder, record.PasswordFileName);
        var sonuc = HtpasswdFile.Verify(
            HtaccessFileSystem.Instance,
            parolaDosyasi,
            kullanici,
            parola,
            HtaccessLimits.Default);

        if (!sonuc.Succeeded)
        {
            // Dosya okunamadı / özet biçimi desteklenmiyor da buraya düşer ve cevap AYNI: 401.
            // Fail-closed kararı sınıf yorumunda gerekçelendirildi.
            return false;
        }

        Onbellege(onbellekAnahtari, now + basariTicks);
        return true;
    }

    private void Onbellege(string key, long expiresAt)
    {
        if (basariOnbellegi.Count >= MaxBasariGirdisi && !basariOnbellegi.ContainsKey(key))
        {
            // Sınıra dayandık: yeni girdi EKLENMEZ. Süresi dolanları taramak yerine tamamını
            // boşaltmak da olurdu ama o, tek bir kötü niyetli istekle bütün oturumları
            // yeniden bcrypt'e sokardı. Eklememek yalnızca o isteğe bedel ödetir.
            return;
        }

        basariOnbellegi[key] = expiresAt;
    }

    /// <summary>
    /// Önbellek anahtarı. <b>Ham başlık SAKLANMAZ</b> — içinde parola vardır ve bu sözlük
    /// dakikalarca yaşar; bir bellek dökümü parolaları düz metin verirdi. SHA-256 özeti
    /// eşitlik için yeterlidir.
    /// </summary>
    private static string OnbellekAnahtari(string siteFolder, string relativePath, string authorizationHeader)
    {
        using (var sha = SHA256.Create())
        {
            var girdi = Encoding.UTF8.GetBytes(siteFolder + "\n" + relativePath + "\n" + authorizationHeader);
            return Convert.ToBase64String(sha.ComputeHash(girdi));
        }
    }

    /// <summary>
    /// <c>Basic &lt;base64&gt;</c> başlığını kullanıcı/parolaya çevirir. Bozuk her girdi
    /// <c>false</c> döner; İSTİSNA FIRLATMAZ.
    /// </summary>
    internal static bool BasicCoz(string header, out string kullanici, out string parola)
    {
        kullanici = string.Empty;
        parola = string.Empty;

        var temiz = header.Trim();
        const string onEk = "Basic ";
        if (temiz.Length <= onEk.Length ||
            !temiz.Substring(0, onEk.Length).Equals(onEk, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        byte[] baytlar;
        try
        {
            baytlar = Convert.FromBase64String(temiz.Substring(onEk.Length).Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        // RFC 7617: kodlama belirsizdir, pratikte UTF-8 gönderilir. Latin-1 ile çözmek,
        // Türkçe karakter içeren parolaları sessizce yanlış eşleştirirdi.
        var cozulen = Encoding.UTF8.GetString(baytlar);

        // İLK iki nokta ayırır: parolanın kendisi iki nokta İÇEREBİLİR (RFC'ye göre kullanıcı adı
        // içeremez). Son iki noktadan bölmek, ':' içeren parolaları çalışmaz hâle getirirdi.
        var ayrac = cozulen.IndexOf(':');
        if (ayrac <= 0)
        {
            return false;
        }

        kullanici = cozulen.Substring(0, ayrac);
        parola = cozulen.Substring(ayrac + 1);
        return true;
    }

    /// <summary>
    /// <c>WWW-Authenticate</c> başlığına yazılacak realm.
    /// </summary>
    /// <remarks>
    /// Tırnak ve ters bölü ELENİR (kaçırılmaz): realm kullanıcıdan gelir ve tırnak içinde
    /// yazılır; kaçırma kuralını yanlış uygulamak başlığa yeni bir parametre enjekte edilmesine
    /// yol açabilirdi. Panel zaten süzüyor; bu, ikinci katman.
    /// </remarks>
    private static string TemizRealm(string? realm)
    {
        var builder = new StringBuilder();
        foreach (var character in realm ?? string.Empty)
        {
            if (character is '"' or '\\' or '\r' or '\n')
            {
                continue;
            }

            builder.Append(character);
        }

        var temiz = builder.ToString().Trim();
        return temiz.Length == 0 ? "Protected" : temiz;
    }

    private CachedList OkuOnbellekli(string siteName)
    {
        if (!siteLists.TryGetValue(siteName, out var entry))
        {
            if (siteLists.Count >= MaxSiteEntries)
            {
                // Sınır aşıldı: "bakamadım" döner (fail-open). Pratikte bir AppDomain tek siteye
                // hizmet eder; bu dal yalnız hatalı/kötü niyetli bir çağrıya karşıdır.
                return new CachedList { Readable = false, ExpiresAtTicks = long.MaxValue };
            }

            entry = siteLists.GetOrAdd(siteName, name => new CachedList
            {
                ExpiresAtTicks = long.MinValue,
                // Yollar site başına BİR KEZ üretilir (ölçülmüş gerekçe: HtaccessRuntimeGate).
                SiteFolder = ProtectedDirectoryFile.SiteRoot(dataRoot, name),
                ListPath = ProtectedDirectoryFile.ListPath(dataRoot, name)
            });
        }

        var now = monotonicTicks();
        if (now < entry.ExpiresAtTicks)
        {
            return entry;
        }

        try
        {
            var content = reader.Read(entry.ListPath);
            if (content is null)
            {
                // Dosya YOK = koruma yok. Bu bir ÖLÇÜMDÜR, "bakamadım" değil.
                entry.Records = new List<ProtectedDirectoryRecord>();
                entry.Readable = true;
            }
            else
            {
                entry.Records = ProtectedDirectoryFile.Parse(content);
                entry.Readable = true;
            }
        }
        catch (Exception)
        {
            // Yetki/kilit/disk: BAKAMADIK. Eski kayıtları da düşürüyoruz ki "yarım bilgi" ile
            // karar verilmesin; Readable=false zaten fail-open demek.
            entry.Records = new List<ProtectedDirectoryRecord>();
            entry.Readable = false;
        }

        entry.ExpiresAtTicks = now + ttlTicks;
        return entry;
    }
}
