using AdaPanel.Core.Enums;

namespace AdaPanel.Core.Common;

/// <summary>
/// Veritabanı motorlarının hesap adı UZUNLUK sınırlarının TEK KAYNAĞI.
/// </summary>
/// <remarks>
/// <para>
/// <b>ÖLÇÜLEN HATA.</b> Panelin doğrulayıcısı kullanıcı adı için 64 karaktere izin veriyordu;
/// MySQL'in sınırı ise 32'dir ve motorda SABİT KODLUDUR. 33–64 arası bir adla:
/// <c>CREATE DATABASE</c> ÖNCE çalışıyor, ardından <c>CREATE USER</c> 1470
/// (<c>ER_WRONG_STRING_LENGTH</c>) ile düşüyor, geri alma olmadığı için kullanıcıya jenerik 500
/// dönerken sunucuda SAHİPSİZ bir veritabanı kalıyordu. 64 sayısı üstelik beş ayrı yere ELLE
/// yazılmıştı (regex + dört istek DTO'su) ve hiçbiri bir motor sınırını temsil etmiyordu.
/// </para>
/// <para>
/// <b>Sayılar BELGEDEN alındı, tahmin edilmedi:</b>
/// </para>
/// <list type="bullet">
/// <item>
/// <b>MySQL — 32.</b> "MySQL user names are up to 32 characters long." Aynı sayfa sınırın
/// sunucu ve istemcilerde sabit kodlu olduğunu, <c>mysql</c> şemasındaki tabloları değiştirerek
/// aşmanın ÇALIŞMADIĞINI da söyler.
/// (<see href="https://dev.mysql.com/doc/refman/8.4/en/user-names.html"/>)
/// </item>
/// <item>
/// <b>MariaDB — 80.</b> MariaDB 5.5.31'de 16'dan 80'e çıkarıldı (MDEV-4332). 10.6+ 128'e
/// çıkarır; panel hangi yamanın kurulu olduğunu bilemeyeceği için GÜVENLİ TABAN olan 80 alınır.
/// (<see href="https://mariadb.com/kb/en/identifier-names/"/>)
/// </item>
/// <item><b>PostgreSQL — 63.</b> <c>NAMEDATALEN - 1</c>.</item>
/// <item><b>SQL Server — 128.</b> <c>sysname</c> / normal tanımlayıcı sınırı.</item>
/// </list>
/// <para>
/// <b>Neden istek DTO'ları tek bir sayı kullanıyor.</b> <see cref="RequestCeiling"/> bir MOTOR
/// sınırı değil, kaba bir üst sınırdır: DTO doğrulaması hangi motorun seçildiğini bilmeden
/// çalışır, orada 32 dayatmak geçerli bir PostgreSQL/SQL Server adını reddederdi. Gerçek kararı
/// motoru bilen <see cref="ForEngine"/> verir ve hata mesajı hangi motorun kaç izin verdiğini
/// SÖYLER. Aynı ayrım <c>ManagedAppPoolName</c> ve <c>PanelUnits</c> kalıbındaki gibi: tekrar
/// eden sabit tek dosyaya toplanır, ondan sonra "birini düzeltip diğerini unutma" hatası
/// mümkün olmaz.
/// </para>
/// </remarks>
public static class DatabaseIdentifierLimits
{
    /// <summary>MySQL: 32 (sunucuda ve istemcilerde sabit kodlu).</summary>
    public const int MySqlUserNameLength = 32;

    /// <summary>MariaDB: 80 (5.5.31+; 10.6+ 128 verir ama taban 80 alınır).</summary>
    public const int MariaDbUserNameLength = 80;

    /// <summary>PostgreSQL: <c>NAMEDATALEN - 1</c>.</summary>
    public const int PostgreSqlUserNameLength = 63;

    /// <summary>SQL Server: normal tanımlayıcı sınırı.</summary>
    public const int MsSqlUserNameLength = 128;

    /// <summary>
    /// İstek DTO'larının <c>[StringLength]</c> tavanı: desteklenen motorların EN GENİŞİ.
    /// Motora özgü asıl sınır <see cref="ForEngine"/> ile uygulanır.
    /// </summary>
    public const int RequestCeiling = MsSqlUserNameLength;

    /// <summary>Motorun kullanıcı adı için kabul ettiği en büyük uzunluk.</summary>
    public static int ForEngine(DbEngine engine) => engine switch
    {
        DbEngine.MySql => MySqlUserNameLength,
        DbEngine.MariaDb => MariaDbUserNameLength,
        DbEngine.PostgreSql => PostgreSqlUserNameLength,
        DbEngine.MsSql => MsSqlUserNameLength,
        // Bilinmeyen motor: EN DAR sınır uygulanır. Geniş davranmak, düşecek bir adı kabul edip
        // hatayı motora bırakmak olurdu — düzeltilen hatanın ta kendisi.
        _ => MySqlUserNameLength
    };
}
