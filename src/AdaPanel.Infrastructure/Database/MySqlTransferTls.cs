using System.Net;

namespace AdaPanel.Infrastructure.Database;

/// <summary>
/// Sunucu taraflı veritabanı taşımasında (<c>mysqldump</c> → <c>mysql</c>) BAĞLANTI ŞİFRELEMESİ
/// kararı — tek kaynak.
/// </summary>
/// <remarks>
/// <para>
/// <b>ÖLÇÜLEN AÇIK.</b> Taşıma komut satırında hiç <c>--ssl-mode</c> yoktu. MySQL/MariaDB
/// istemcilerinin varsayılanı <c>PREFERRED</c>'dır: sunucu TLS sunmuyorsa (ya da araya giren biri
/// sunmuyormuş gibi davranırsa) istemci ŞİFRESİZ devam eder. Yani UZAK bir kaynaktan çekilen
/// göçte veritabanı parolası ve tüm tablo içeriği açık metin akabilirdi ve panel bunu hiçbir yerde
/// söylemezdi.
/// </para>
/// <para>
/// <b>ÜÇ DURUM AYRILIYOR — hepsini aynı kefeye koymak yanlış olurdu.</b>
/// </para>
/// <list type="number">
/// <item><b>Yerel/loopback kaynak:</b> araya girecek ağ YOKTUR. Burada TLS zorlamak hiçbir şeyi
/// korumaz, yalnız TLS'i açık olmayan yerel motorlarda çalışan göçü kırardı → zorlanmaz.</item>
/// <item><b>Uzak kaynak, kullanıcı bir şey söylememiş:</b> varsayılan GÜVENLİ olmalı →
/// <c>REQUIRED</c>. Şifresiz bağlantı denenmez bile.</item>
/// <item><b>Uzak kaynak, kullanıcı ŞİFRELEMESİZ dediyse:</b> bu onun kararıdır — TLS desteklemeyen
/// eski bir sunucudan çekiyor olabilir. Engellenmez. Kapatılan şey kullanıcının tercihi değil,
/// panelin "şifreliyorum" deyip şifrelememesidir.</item>
/// </list>
/// <para>
/// <b>DÜRÜST SINIR:</b> <c>REQUIRED</c> bağlantının ŞİFRELENMESİNİ zorunlu kılar ama sunucunun
/// KİMLİĞİNİ doğrulamaz (onun için <c>VERIFY_CA</c> ve bir CA dosyası gerekir; göç edilen eski
/// sunucularda böyle bir CA neredeyse hiç yoktur). Yani bu ayar pasif dinlemeyi kapatır, kimlik
/// doğrulaması ayrı bir iştir. Bunu "TLS var, güvendeyiz" diye yazmak yanlış olurdu.
/// </para>
/// </remarks>
internal static class MySqlTransferTls
{
    /// <summary>Kullanıcı seçimi yok: konağa bakarak karar ver (uzaksa şifrele).</summary>
    public const string Auto = "auto";

    /// <summary>Şifreleme ZORUNLU; sunucu sunmuyorsa bağlantı kurulmaz.</summary>
    public const string Required = "required";

    /// <summary>Şifreleme istenmiyor — kullanıcının BİLİNÇLİ tercihi.</summary>
    public const string Disabled = "disabled";

    /// <summary>
    /// Konak yerel mi (loopback / Windows adlandırılmış kanal)? Yerelde araya girecek ağ yoktur.
    /// </summary>
    /// <remarks>
    /// Boş konak UZAK sayılır: burada yanılmanın bedeli asimetriktir — yerel sanıp şifrelememek
    /// parolayı tele verir, uzak sanıp şifrelemek yalnızca anlaşılır bir hata üretir.
    /// MySQL istemcisinde <c>.</c> Windows'ta adlandırılmış kanal demektir, yani yereldir.
    /// </remarks>
    internal static bool YerelKonak(string? host)
    {
        var deger = (host ?? string.Empty).Trim();
        if (deger.Length == 0)
        {
            return false;
        }

        if (deger is "." or "localhost" || deger.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Köşeli parantezli IPv6 yazımı (`[::1]`) — bağlantı dizelerinde yaygın.
        if (deger.Length > 2 && deger[0] == '[' && deger[^1] == ']')
        {
            deger = deger[1..^1];
        }

        if (!IPAddress.TryParse(deger, out var adres))
        {
            return false;
        }

        if (IPAddress.IsLoopback(adres))
        {
            return true;
        }

        // IPv4 eşlemeli IPv6 (`::ffff:127.0.0.1`) aynı makineye gider; eşlemeyi açıp tekrar bakılır.
        return adres.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(adres.MapToIPv4());
    }

    /// <summary>
    /// İstenen kipi ve konağı birleştirip UYGULANACAK kipi verir: <see cref="Required"/> ya da
    /// <see cref="Disabled"/>.
    /// </summary>
    /// <remarks>
    /// Tanınmayan değer <see cref="Auto"/> gibi ele alınır — sessizce şifresize düşmek, bu
    /// düzeltmenin kapatmaya çalıştığı hatanın ta kendisi olurdu.
    /// </remarks>
    internal static string Coz(string? host, string? istenenKip)
    {
        var kip = (istenenKip ?? string.Empty).Trim().ToLowerInvariant();

        return kip switch
        {
            Required => Required,
            Disabled => Disabled,
            _ => YerelKonak(host) ? Disabled : Required
        };
    }

    /// <summary>Çözülmüş kipin komut satırı karşılığı (boş dize = argüman eklenmez).</summary>
    /// <remarks>
    /// <c>DISABLED</c> AÇIKÇA YAZILMAZ. Yazılsaydı, <c>--ssl-mode</c> tanımayan eski istemcilerde
    /// (MariaDB 11.4 öncesi) taşıma "unknown variable" ile ölürdü — yani şifreleme İSTEMEYEN
    /// kullanıcının yolunu, ona hiçbir şey kazandırmayan bir bayrak kırardı.
    /// </remarks>
    internal static string KomutArgumani(string cozulmusKip)
        => cozulmusKip == Required ? "--ssl-mode=REQUIRED" : string.Empty;

    /// <summary>
    /// İstemcinin hata çıktısı ŞİFRELEME kaynaklı mı — kullanıcıya doğru sebebi söyleyebilmek için.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Neden gerekli: TLS zorlandığında iki yeni başarısızlık türü doğar ve ikisi de "kaynaktan
    /// dump alınamadı" gibi görünür — (1) sunucu TLS sunmuyor, (2) istemci <c>--ssl-mode</c>
    /// tanımıyor. Kullanıcı sebebi görmezse yapabileceği tek şey denemekten vazgeçmektir.
    /// </para>
    /// <para>
    /// Desenler DAR tutuldu: yalnız "ssl" aramak, adında ssl geçen bir veritabanı/kullanıcı
    /// yüzünden yetki hatasını da şifreleme hatası diye etiketlerdi.
    /// </para>
    /// </remarks>
    internal static bool TlsKaynakliHata(string? standardError)
    {
        if (string.IsNullOrWhiteSpace(standardError))
        {
            return false;
        }

        var metin = standardError.ToLowerInvariant();
        return metin.Contains("ssl-mode", StringComparison.Ordinal)
            || metin.Contains("ssl connection", StringComparison.Ordinal)
            || metin.Contains("tls/ssl", StringComparison.Ordinal)
            || metin.Contains("insecure transport", StringComparison.Ordinal)
            || metin.Contains("secure transport", StringComparison.Ordinal)
            || metin.Contains("unknown variable 'ssl", StringComparison.Ordinal)
            || metin.Contains("unknown option '--ssl", StringComparison.Ordinal);
    }
}
