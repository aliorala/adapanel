using System.Text;

namespace AdaPanel.Infrastructure.Htaccess;

/// <summary>
/// Motorun diske dokunduğu TEK kapı.
/// </summary>
/// <remarks>
/// Ayrı bir arayüz olması iki işe yarar: (1) motor gerçek dosya sistemi olmadan test edilebilir,
/// (2) diskle ilgili her istisna TEK yerde yutulur — motorun hiçbir yerinde <c>try/catch</c>
/// dağınıklığı olmaz ve "istisna sızdırma" kuralı yapısal olarak korunur.
/// </remarks>
internal interface IHtaccessFileSystem
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    /// <summary>Sembolik bağlantı / kavşak (junction) mu? <c>RewriteCond ... -l</c> için.</summary>
    bool IsSymbolicLink(string path);

    /// <summary>Dosya var VE boyutu sıfırdan büyük mü? <c>-s</c> için.</summary>
    bool IsNonEmptyFile(string path);

    /// <summary>
    /// Dosyayı okur. Başarısızlık İSTİSNA DEĞİL, <c>false</c> döner ve sebep
    /// <paramref name="hata"/>'ya yazılır.
    /// </summary>
    bool TryReadText(string path, int maxBytes, out string content, out string hata);

    /// <summary>Değişiklik damgası + boyut. Önbelleğin bayatlamasını yakalamak için.</summary>
    (DateTime LastWriteUtc, long Length) Stamp(string path);
}

/// <summary>Gerçek diskin üzerindeki uygulama.</summary>
internal sealed class HtaccessFileSystem : IHtaccessFileSystem
{
    public static HtaccessFileSystem Instance { get; } = new();

    public bool FileExists(string path) => Guvenli(() => File.Exists(path), false);

    public bool DirectoryExists(string path) => Guvenli(() => Directory.Exists(path), false);

    /// <remarks>
    /// <para>
    /// <b><c>FileInfo.LinkTarget</c> KULLANILMIYOR</b> ve bu bilinçli bir değişiklik: o özellik
    /// .NET 6 ile geldi, <c>net48</c>'de YOKTUR. Motorun kaynakları IIS modülü için net48'de de
    /// derleniyor (bkz. <c>HtaccessPortableApi</c>); <c>LinkTarget</c> kalsaydı modül motoru
    /// yükleyemezdi.
    /// </para>
    /// <para>
    /// <b>Neden <c>#if</c> ile iki yazım değil:</b> ikisi AYNI ŞEYİ ÖLÇMÜYOR. <c>LinkTarget</c>
    /// yalnız sembolik bağlantı/kavşakta doludur; <c>ReparsePoint</c> her yeniden ayrıştırma
    /// noktasında (OneDrive yer tutucusu, tekilleştirme, AppExecLink) kuruludur. İki hedefte iki
    /// farklı ölçüm bırakmak, panelin ÖNİZLEMEDE gösterdiği sonucun IIS'te uygulanandan farklı
    /// olması demekti — bu özellikte kabul edilemez. Tek ölçüm seçildi.
    /// </para>
    /// <para>
    /// <b>Sapma DAHA KORUYUCU yöndedir:</b> <c>RewriteCond ... -l</c> artık biraz daha geniş bir
    /// kümeye "bağlantı" der. Yanlış yönde hata yapmaz — dosya sisteminde dolaylı bir hedefe işaret
    /// eden her giriş zaten şüphelidir ve kural yazarının onu ayırt etmesi beklenir.
    /// </para>
    /// <para>
    /// Tek çağrı hem dosyayı hem klasörü kapsar: <c>File.GetAttributes</c> ikisi için de çalışır
    /// (eski kod <c>FileInfo.Exists</c> dizinlerde <c>false</c> döndüğü için iki adım atıyordu).
    /// Yol yoksa istisna fırlar ve <c>Guvenli</c> onu <c>false</c>'a çevirir.
    /// </para>
    /// </remarks>
    public bool IsSymbolicLink(string path) => Guvenli(
        () => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0,
        false);

    public bool IsNonEmptyFile(string path) => Guvenli(() =>
    {
        var bilgi = new FileInfo(path);
        return bilgi.Exists && bilgi.Length > 0;
    }, false);

    public bool TryReadText(string path, int maxBytes, out string content, out string hata)
    {
        content = string.Empty;
        hata = string.Empty;

        try
        {
            var bilgi = new FileInfo(path);
            if (!bilgi.Exists)
            {
                hata = "dosya yok";
                return false;
            }

            if (bilgi.Length > maxBytes)
            {
                hata = $"dosya {bilgi.Length} bayt, sınır {maxBytes} bayt";
                return false;
            }

            // BOM varsa StreamReader düşürür; UTF-8 olmayan baytlar değiştirme karakterine
            // dönüşür ve ayrıştırıcı yine de bir sonuç üretir (istisna atmaz).
            content = File.ReadAllText(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return true;
        }
        catch (Exception istisna) when (istisna is IOException or UnauthorizedAccessException
                                            or System.Security.SecurityException or ArgumentException
                                            or NotSupportedException or PathTooLongException)
        {
            hata = istisna.Message;
            return false;
        }
    }

    public (DateTime LastWriteUtc, long Length) Stamp(string path) => Guvenli(() =>
    {
        var bilgi = new FileInfo(path);
        return bilgi.Exists ? (bilgi.LastWriteTimeUtc, bilgi.Length) : (DateTime.MinValue, -1L);
    }, (DateTime.MinValue, -1L));

    private static T Guvenli<T>(Func<T> islem, T varsayilan)
    {
        try
        {
            return islem();
        }
        catch (Exception istisna) when (istisna is IOException or UnauthorizedAccessException
                                            or System.Security.SecurityException or ArgumentException
                                            or NotSupportedException or PathTooLongException)
        {
            return varsayilan;
        }
    }
}
