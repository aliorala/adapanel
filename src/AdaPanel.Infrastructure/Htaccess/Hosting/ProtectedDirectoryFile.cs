using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AdaPanel.Infrastructure.Htaccess.Hosting;

/// <summary>Korumalı bir dizinin, istek yolunda uygulanabilir tanımı.</summary>
internal sealed class ProtectedDirectoryRecord
{
    /// <summary>Site köküne göre yol, eğik çizgiyle ve küçük harfle ("yonetim", "a/b").</summary>
    internal string RelativePath = string.Empty;

    /// <summary>Tarayıcının parola kutusunda göstereceği ad.</summary>
    internal string Realm = string.Empty;

    internal string UserName = string.Empty;

    /// <summary>
    /// Parola dosyasının ADI (yol DEĞİL). Betimleyicinin yanında çözülür.
    /// </summary>
    /// <remarks>
    /// Tam yol yazılmıyor: dosya ADI yazıldığında, betimleyiciyi yazma hakkı olan biri bile
    /// okumayı başka bir klasöre yönlendiremez. Tam yol yazılsaydı, betimleyiciye yazabilen bir
    /// saldırgan <c>C:\ProgramData\AdaPanel\config\...</c> gibi başka bir dosyayı parola dosyası
    /// diye okutmayı deneyebilirdi.
    /// </remarks>
    internal string PasswordFileName = string.Empty;
}

/// <summary>
/// KORUMALI DİZİN BETİMLEYİCİSİ — panel YAZAR, IIS modülü OKUR.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden site kökünde DEĞİL:</b> koruma eskiden korunan klasörün içindeki bir
/// <c>web.config</c>'de yaşıyordu ve o dosya <c>base64(kullanıcı:parola)</c> taşıyordu — yani
/// müşterinin kendi FTP kullanıcısı, sitede çalışan herhangi bir PHP betiği ve site yedeğini
/// indiren herkes parolayı DÜZ METİN okuyabiliyordu (bu turda canlı ölçüldü:
/// <c>YWRha3VsbGFuaWNpOkdpemxpITIwMjZBZGE=</c> → <c>adakullanici:Gizli!2026Ada</c>).
/// Betimleyici ve parola özeti panelin veri kökünde durur; site kökünde hiçbir sır kalmaz.
/// İkinci bir kazanç: koruma artık bir <c>git reset --hard</c> ya da yeniden yayınla
/// SİLİNEMEZ — eski tasarımda tek dosya gidince klasör sessizce herkese açılıyordu.
/// </para>
/// <para>
/// <b>Neden JSON değil:</b> bu dosyayı okuyan taraf net48'deki IIS modülüdür ve net48'in çekirdek
/// kütüphanesinde <c>System.Text.Json</c> YOKTUR (aynı gerekçe <see cref="HtaccessSwitchFile"/>
/// için de yazılı). Ayrıca dosya bir kurtarma aracıdır: yönetici Not Defteri'yle açıp bir satır
/// silerek bir korumayı kaldırabilmelidir.
/// </para>
/// <para>
/// <b>AYRIŞTIRICI HİÇBİR KOŞULDA FIRLATMAZ.</b> Bozuk satır ATLANIR, eksik alanı olan kayıt
/// KABUL EDİLMEZ. "Yarım kayıt" kabul etmek, kullanıcı adı boş bir korumaya — yani
/// doğrulanamayacağı için herkesi içeri alan ya da kimseyi almayan bir kapıya — yol açardı.
/// </para>
/// </remarks>
internal static class ProtectedDirectoryFile
{
    /// <summary>Veri kökü altındaki klasör adı.</summary>
    internal const string FolderName = "protected-dirs";

    /// <summary>Site başına betimleyici dosyasının adı.</summary>
    internal const string ListFileName = "dirs.list";

    /// <summary>Parola dosyalarının uzantısı — Apache <c>.htpasswd</c> biçimi.</summary>
    internal const string PasswordFileExtension = ".htpasswd";

    private const string DirectoryKey = "dir";
    private const string RealmKey = "realm";
    private const string UserKey = "user";
    private const string PasswordFileKey = "passwd";

    internal static string Root(string dataRoot)
        => Path.Combine(HtaccessHostingPaths.ExpandFull(dataRoot), FolderName);

    /// <summary>Bir sitenin klasörü. Site adı → dosya adı eşlemesi anahtar dosyalarıyla AYNI kaynaktan.</summary>
    internal static string SiteRoot(string dataRoot, string siteName)
        => Path.Combine(Root(dataRoot), HtaccessHostingPaths.SafeSiteKey(siteName));

    internal static string ListPath(string dataRoot, string siteName)
        => Path.Combine(SiteRoot(dataRoot, siteName), ListFileName);

    /// <summary>
    /// Bir korumalı dizinin parola dosyasının ADI. Göreli yoldan TÜRETİLİR ki panel ile modül
    /// aynı adı bulsun; ayrıca özet eki iki farklı yolun aynı dosyaya düşmesini engeller
    /// (<c>a/b</c> ile <c>a_b</c> temizlenince aynı ada düşerdi).
    /// </summary>
    internal static string PasswordFileName(string relativePath)
        => HtaccessHostingPaths.SafeSiteKey(NormalizePath(relativePath)) + PasswordFileExtension;

    /// <summary>
    /// Göreli yolu KARŞILAŞTIRILABİLİR biçime getirir: ters bölü → eğik çizgi, boş parçalar atılır,
    /// küçük harf. Windows'ta dosya sistemi harf duyarsızdır; karşılaştırmayı harf duyarlı bırakmak
    /// <c>/Yonetim/</c> isteğinin <c>yonetim</c> korumasını ATLAMASI demek olurdu.
    /// </summary>
    internal static string NormalizePath(string? value)
    {
        var raw = (value ?? string.Empty).Replace('\\', '/');
        var parts = raw.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('/');
            }

            builder.Append(trimmed.ToLowerInvariant());
        }

        return builder.ToString();
    }

    /// <summary>
    /// İstek bu korumanın KAPSAMINDA mı.
    /// </summary>
    /// <remarks>
    /// <b>SINIR DENETİMİ ŞART.</b> Düz bir <c>StartsWith</c> ile <c>yonetim</c> koruması
    /// <c>yonetim-herkese-acik</c> klasörünü de kapatırdı; tersi daha kötü olurdu — yol ayracı
    /// aranmasaydı <c>yonetimx</c> gibi bir ad korumayı taşırdı. Bu yüzden ya TAM eşitlik ya da
    /// hemen ardından bir <c>/</c> aranır.
    /// </remarks>
    internal static bool Covers(string protectedRelativePath, string requestRelativePath)
    {
        var korunan = NormalizePath(protectedRelativePath);
        var istek = NormalizePath(requestRelativePath);

        if (korunan.Length == 0)
        {
            // Boş yol "site kökü" demek olurdu; betimleyici bunu üretmez ve kabul de etmeyiz.
            // Kabul edilseydi tek bozuk satır BÜTÜN siteyi parolalı hâle getirebilirdi.
            return false;
        }

        if (istek.Length < korunan.Length)
        {
            return false;
        }

        if (string.Equals(istek, korunan, StringComparison.Ordinal))
        {
            return true;
        }

        return istek.Length > korunan.Length &&
               istek[korunan.Length] == '/' &&
               string.Equals(istek.Substring(0, korunan.Length), korunan, StringComparison.Ordinal);
    }

    /// <summary>Betimleyiciyi ayrıştırır. İSTİSNA FIRLATMAZ; bozuk kayıt atlanır.</summary>
    internal static List<ProtectedDirectoryRecord> Parse(string? content)
    {
        var sonuc = new List<ProtectedDirectoryRecord>();
        if (string.IsNullOrEmpty(content))
        {
            return sonuc;
        }

        ProtectedDirectoryRecord? current = null;

        // net48'de string.Split(char, StringSplitOptions) aşırı yüklemesi YOK -> char[].
        var lines = content!.Split(new[] { '\n' }, StringSplitOptions.None);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim().TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';')
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line.Substring(0, separator).Trim().ToLowerInvariant();
            var value = line.Substring(separator + 1).Trim();

            if (string.Equals(key, DirectoryKey, StringComparison.Ordinal))
            {
                // Yeni kayıt BAŞLIYOR: öncekini ancak TAM ise listeye al.
                Tamamla(sonuc, current);
                current = new ProtectedDirectoryRecord { RelativePath = NormalizePath(value) };
                continue;
            }

            if (current is null)
            {
                // "dir=" görülmeden gelen alanların sahibi yok; sessizce atılır.
                continue;
            }

            if (string.Equals(key, RealmKey, StringComparison.Ordinal))
            {
                current.Realm = value;
            }
            else if (string.Equals(key, UserKey, StringComparison.Ordinal))
            {
                current.UserName = value;
            }
            else if (string.Equals(key, PasswordFileKey, StringComparison.Ordinal))
            {
                // Yalnız DOSYA ADI kabul edilir: içinde ayraç olan bir değer, betimleyiciye
                // yazabilen birine "başka klasördeki dosyayı oku" imkânı verirdi.
                current.PasswordFileName = value.IndexOf('/') >= 0 || value.IndexOf('\\') >= 0
                    ? string.Empty
                    : value;
            }
        }

        Tamamla(sonuc, current);
        return sonuc;
    }

    /// <summary>Betimleyici içeriğini üretir (panel yazar).</summary>
    internal static string Render(IList<ProtectedDirectoryRecord> records)
    {
        var builder = new StringBuilder();
        builder.Append("# AdaPanel korumali dizinler. Bu dosyayi SILMEK butun korumalari kaldirir.\n");
        builder.Append("# Parolalar BURADA DEGIL: her kaydin passwd= satirindaki .htpasswd dosyasinda,\n");
        builder.Append("# bcrypt ozeti olarak durur. Site kokunde hicbir sir yoktur.\n");
        builder.Append("# Yazilma zamani (UTC): ");
        builder.Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        builder.Append('\n');

        foreach (var record in records)
        {
            if (record is null)
            {
                continue;
            }

            builder.Append('\n');
            builder.Append(DirectoryKey).Append('=').Append(TekSatir(record.RelativePath)).Append('\n');
            builder.Append(RealmKey).Append('=').Append(TekSatir(record.Realm)).Append('\n');
            builder.Append(UserKey).Append('=').Append(TekSatir(record.UserName)).Append('\n');
            builder.Append(PasswordFileKey).Append('=').Append(TekSatir(record.PasswordFileName)).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Apache <c>.htpasswd</c> biçiminde tek satır. Doğrulayan taraf
    /// <see cref="Htpasswd.HtpasswdFile"/>'dır; biçim ondan TÜREMEK zorunda, tersi değil.
    /// </summary>
    internal static string RenderPasswordFile(string userName, string hash)
        => "# AdaPanel korumali dizin parolasi (bcrypt). 'htpasswd -B' ile de yenilenebilir.\n"
           + TekSatir(userName) + ":" + TekSatir(hash) + "\n";

    /// <summary>
    /// Satır sonlarını temizler. TEMİZLENMESEYDİ: realm alanına <c>"\ndir=/"</c> yazan biri
    /// betimleyiciye YENİ BİR KAYIT enjekte edebilirdi. Değerler kullanıcıdan geliyor.
    /// </summary>
    private static string TekSatir(string? value)
        => (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static void Tamamla(List<ProtectedDirectoryRecord> sonuc, ProtectedDirectoryRecord? record)
    {
        if (record is null)
        {
            return;
        }

        // EKSİK ALAN = KAYIT YOK. Yarım bir kayıt, doğrulanamayacak bir kapı demektir.
        if (record.RelativePath.Length == 0 ||
            record.UserName.Length == 0 ||
            record.PasswordFileName.Length == 0)
        {
            return;
        }

        sonuc.Add(record);
    }
}
