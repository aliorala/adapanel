using AdaPanel.Core.Common;

namespace AdaPanel.Infrastructure.Database;

/// <summary>
/// MySQL/MariaDB hesabının host kısmıyla ilgili SAF kurallar (bağlantı gerektirmez).
/// </summary>
/// <remarks>
/// MySQL'de bir hesap <c>(kullanici, host)</c> ÇİFTİDİR; "aynı kullanıcı ama başka adresten"
/// demek AYRI BİR HESAP demektir. Uzak erişim açmak/kapatmak da bu yüzden hesap kümesini
/// eşitlemek anlamına gelir. Buradaki kurallar, o eşitlemenin karar veren kısmıdır ve canlı bir
/// motor olmadan sınanabilsin diye ayrı tutulmuştur.
/// </remarks>
internal static class MySqlAccountHosts
{
    /// <summary>
    /// Sunucunun kendisini temsil eden host değerleri.
    /// </summary>
    /// <remarks>
    /// Üçü birden gerekir çünkü hangisinin eşleşeceği motorun ayarına bağlıdır:
    /// <c>skip-name-resolve</c> KAPALIYKEN (varsayılan) 127.0.0.1'den gelen bağlantı
    /// <c>localhost</c> olarak çözülür; AÇIKKEN çözüm yapılmaz ve yalnız sayısal <c>127.0.0.1</c>
    /// eşleşir. Aynı ikilem IPv6 loopback için de geçerlidir. Panelin kendi kök hesabı için de
    /// (<c>SetRootPasswordAsync</c>) tam olarak bu üçlü kullanılıyor — burada da aynısı kullanılır
    /// ki "yerel" tanımı panelin iki yerinde iki farklı şey olmasın.
    /// </remarks>
    public static readonly IReadOnlyList<string> LocalHosts = ["localhost", "127.0.0.1", "::1"];

    /// <summary>
    /// Panelin yeni oluşturduğu kullanıcılar için GÜVENLİ varsayılan host.
    /// </summary>
    /// <remarks>
    /// Eskiden <c>%</c> (her yer) idi: oluşturulan her veritabanı kullanıcısı, dünyanın her
    /// yerinden geçerli bir hesaptı ve tek engel güvenlik duvarıydı. 3306'ya herhangi bir yol
    /// açıldığı anda (yönetici kuralı, başka bir bileşen, yanlış yapılandırma) SUNUCUDAKİ BÜTÜN
    /// müşteri hesapları aynı anda dışarıdan denenebilir olurdu. Kimlik doğrulama sınırı
    /// güvenlik duvarına devredilemez; artık hesabın kendisi yerelle sınırlı doğar ve uzak
    /// erişim ayrıca, açık onayla açılır.
    /// </remarks>
    public const string SafeCreationHost = "localhost";

    /// <summary>
    /// Uzak erişim ayarına KAPALI olan hesaplar. Bunlara dokunmak paneli ya da motoru kilitler.
    /// </summary>
    /// <remarks>
    /// <c>root</c> panelin motora bağlandığı hesaptır; host kümesi değiştirilirse panel kendi
    /// veritabanı erişimini kaybeder ve geri dönüş yolu kalmaz. Diğerleri motorun kilitli iç
    /// hesaplarıdır. Bu liste, kullanıcı listesini süzen sorgudaki (<c>GetUsersAsync</c>) liste
    /// ile aynı olmalıdır: orada gizlenen bir hesabın buradan yönetilebilmesi anlamsızdır.
    /// </remarks>
    private static readonly HashSet<string> ProtectedUsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "root", "mariadb.sys", "mysql.sys", "mysql.session", "mysql.infoschema", "debian-sys-maint"
    };

    public static bool IsLocal(string? host) =>
        LocalHosts.Contains((host ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Sistem/yönetici hesabıysa isteği reddeder.</summary>
    public static void EnsureManageable(string? userName)
    {
        var name = (userName ?? string.Empty).Trim();
        if (name.Length == 0 || ProtectedUsers.Contains(name))
        {
            throw new AppException("database.remote_user_protected", 403, 403);
        }
    }

    /// <summary>
    /// Hesabın olması gereken host kümesini hesaplar.
    /// </summary>
    /// <param name="currentHosts">Motorda ŞU AN var olan host'lar.</param>
    /// <param name="remoteHosts">İzin verilecek uzak host'lar (kipe göre; kapalıda boş).</param>
    /// <remarks>
    /// <para>
    /// Var olan yerel hesaplara DOKUNULMAZ. Uzak erişimi kapatmak, çalışan bir sitenin
    /// veritabanı bağlantısını koparmamalıdır; kapatma yalnız uzak hesapları kaldırır.
    /// </para>
    /// <para>
    /// Yerel hesap HİÇ yoksa (örneğin hesap yalnızca <c>%</c> olarak varsa — panelin eski
    /// varsayılanı) yerel küme OLUŞTURULUR. Aksi hâlde "kapat" demek hesabı tümüyle silmek
    /// olurdu: kullanıcı uzak erişimi kapatırken sitesini de kapatmış olurdu.
    /// </para>
    /// </remarks>
    public static AccountHostPlan Plan(IReadOnlyCollection<string> currentHosts, IReadOnlyCollection<string> remoteHosts)
    {
        var current = new HashSet<string>(
            currentHosts.Select(item => (item ?? string.Empty).Trim()).Where(item => item.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        var currentLocal = current.Where(IsLocal).ToArray();
        var target = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (currentLocal.Length > 0)
        {
            foreach (var host in currentLocal)
            {
                target.Add(host);
            }
        }
        else
        {
            foreach (var host in LocalHosts)
            {
                target.Add(host);
            }
        }

        foreach (var host in remoteHosts)
        {
            var trimmed = (host ?? string.Empty).Trim();
            if (trimmed.Length > 0)
            {
                target.Add(trimmed);
            }
        }

        return new AccountHostPlan(
            [.. target.Where(host => !current.Contains(host))],
            [.. current.Where(host => !target.Contains(host))],
            [.. target]);
    }

    /// <summary>
    /// <c>mysql.db</c> satırındaki yetki sütunlarından GRANT tümcesi kurar.
    /// </summary>
    /// <param name="privilegeColumns">Sütun adı → 'Y' mi, sözlüğü (büyük/küçük harf duyarsız).</param>
    /// <returns>GRANT tümcesi; kopyalanacak yetki yoksa <c>null</c>.</returns>
    /// <remarks>
    /// <para>
    /// Yetkiler <c>SHOW GRANTS</c> ÇIKTISI AYRIŞTIRILARAK değil, sütunlardan okunarak kopyalanır.
    /// Metin ayrıştırmak motor sürümüne göre değişen bir biçime (MariaDB'nin
    /// <c>IDENTIFIED BY PASSWORD</c> eki gibi) bağlanmak olurdu; yanlış ayrıştırma ya sessizce
    /// yetkisiz bir hesap ya da fazladan yetki üretirdi.
    /// </para>
    /// <para>
    /// <c>Grant_priv</c> BİLEREK kopyalanmaz: bir barındırma hesabının yetki dağıtabilmesi
    /// istenmez ve klonlanan hesabın kaynağından DAHA GENİŞ olmaması gerekir. Eksik olması
    /// güvenli yöndedir ve raporlanabilir; fazla olması geri alınamaz.
    /// </para>
    /// </remarks>
    public static string? BuildGrantClause(IReadOnlyDictionary<string, bool> privilegeColumns)
    {
        var verilen = new List<string>();
        var eksikVar = false;

        foreach (var (column, privilege) in PrivilegeColumns)
        {
            // Sütun motorda yoksa (sürüm farkı) o yetki hiç yok sayılır; "hepsi var" kararını
            // bozmasın diye eksik sayılmaz.
            if (!privilegeColumns.TryGetValue(column, out var granted))
            {
                continue;
            }

            if (granted)
            {
                verilen.Add(privilege);
            }
            else
            {
                eksikVar = true;
            }
        }

        if (verilen.Count == 0)
        {
            return null;
        }

        return eksikVar ? string.Join(", ", verilen) : "ALL PRIVILEGES";
    }

    /// <summary>
    /// <c>mysql.db</c> yetki sütunu → SQL yetki adı. Sıra GRANT tümcesindeki sırayı belirler.
    /// </summary>
    public static readonly IReadOnlyList<(string Column, string Privilege)> PrivilegeColumns =
    [
        ("Select_priv", "SELECT"),
        ("Insert_priv", "INSERT"),
        ("Update_priv", "UPDATE"),
        ("Delete_priv", "DELETE"),
        ("Create_priv", "CREATE"),
        ("Drop_priv", "DROP"),
        ("References_priv", "REFERENCES"),
        ("Index_priv", "INDEX"),
        ("Alter_priv", "ALTER"),
        ("Create_tmp_table_priv", "CREATE TEMPORARY TABLES"),
        ("Lock_tables_priv", "LOCK TABLES"),
        ("Create_view_priv", "CREATE VIEW"),
        ("Show_view_priv", "SHOW VIEW"),
        ("Create_routine_priv", "CREATE ROUTINE"),
        ("Alter_routine_priv", "ALTER ROUTINE"),
        ("Execute_priv", "EXECUTE"),
        ("Event_priv", "EVENT"),
        ("Trigger_priv", "TRIGGER")
    ];
}

/// <summary>Hesap host kümesini eşitlemek için yapılacaklar.</summary>
internal sealed record AccountHostPlan(
    IReadOnlyList<string> ToCreate,
    IReadOnlyList<string> ToDrop,
    IReadOnlyList<string> Final);
