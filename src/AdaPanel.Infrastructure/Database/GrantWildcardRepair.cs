using MySqlConnector;

namespace AdaPanel.Infrastructure.Database;

/// <summary>
/// Kaçırılmamış (joker içeren) veritabanı yetkilerini bulur ve daraltır.
/// <para>
/// AÇIK: MySQL'de <c>GRANT</c> ifadesinin veritabanı adı bölümünde <c>_</c> ve <c>%</c>
/// karakterleri TERS TIRNAK İÇİNDE BİLE joker olarak yorumlanır. Panel yetkileri kaçırmadan
/// veriyordu, dolayısıyla <c>musteri1_db</c> için verilen yetki <c>musteri1Xdb</c> gibi desene
/// uyan HER veritabanını da kapsıyordu. Çok kiracılı bir panelde bu, bir müşterinin diğerinin
/// verisini okuyup yazabilmesi demektir.
/// </para>
/// <para>
/// Üretim tarafı düzeltildi; ama DAHA ÖNCE verilmiş yetkiler sunucuda olduğu gibi durur — kod
/// düzeltmesi onlara dokunmaz. Bu sınıf o mirası temizler.
/// </para>
/// <para>
/// SIRA ÖNEMLİ: önce dar yetki VERİLİR, sonra geniş olan geri alınır. Tersi yapılsaydı, iki işlem
/// arasında kullanıcının erişimi tamamen kesilir ve o anda çalışan siteler hata verirdi.
/// </para>
/// </summary>
internal static class GrantWildcardRepair
{
    /// <summary>
    /// Bir <c>mysql.db</c> satırındaki desen, kendi adından BAŞKA veritabanlarını da kapsıyor mu?
    /// <para>
    /// Kaçırılmış bir kayıt <c>musteri1\_db</c> biçiminde saklanır; kaçırılmamış olan
    /// <c>musteri1_db</c>. Yalnızca ters bölü ile korunmamış <c>_</c> / <c>%</c> tehlikelidir.
    /// </para>
    /// </summary>
    internal static bool IsOverBroad(string pattern)
    {
        for (var sira = 0; sira < pattern.Length; sira++)
        {
            if (pattern[sira] == '\\')
            {
                sira++; // kaçırılmış karakteri atla
                continue;
            }

            if (pattern[sira] is '_' or '%')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Deseni harfi harfine eşleşen biçime çevirir: <c>musteri1_db</c> → <c>musteri1\_db</c>.
    /// Zaten kaçırılmış olanlara dokunmaz.
    /// </summary>
    internal static string Tighten(string pattern)
    {
        var sonuc = new System.Text.StringBuilder(pattern.Length + 8);

        for (var sira = 0; sira < pattern.Length; sira++)
        {
            if (pattern[sira] == '\\' && sira + 1 < pattern.Length)
            {
                sonuc.Append(pattern[sira]).Append(pattern[sira + 1]);
                sira++;
                continue;
            }

            if (pattern[sira] is '_' or '%')
            {
                sonuc.Append('\\');
            }

            sonuc.Append(pattern[sira]);
        }

        return sonuc.ToString();
    }

    /// <summary>
    /// Bir veritabanı adı, <c>mysql.db</c> desenine uyuyor mu (MySQL'in LIKE semantiği).
    /// <para>
    /// <c>_</c> tek karakter, <c>%</c> sıfır veya daha çok karakter demektir; ters bölü ile
    /// korunmuş olanlar harfi harfine eşleşir. Karşılaştırma büyük/küçük harf duyarsızdır —
    /// Windows'ta veritabanı adları da öyledir.
    /// </para>
    /// </summary>
    internal static bool MatchesPattern(string name, string pattern)
    {
        var desen = new System.Text.StringBuilder("^");

        for (var sira = 0; sira < pattern.Length; sira++)
        {
            if (pattern[sira] == '\\' && sira + 1 < pattern.Length)
            {
                desen.Append(System.Text.RegularExpressions.Regex.Escape(pattern[sira + 1].ToString()));
                sira++;
                continue;
            }

            desen.Append(pattern[sira] switch
            {
                '_' => ".",
                '%' => ".*",
                var karakter => System.Text.RegularExpressions.Regex.Escape(karakter.ToString())
            });
        }

        desen.Append('$');

        return System.Text.RegularExpressions.Regex.IsMatch(
            name,
            desen.ToString(),
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Geniş yetkileri daraltır; daraltılan her yetkinin açıklamasını döndürür.
    /// <para>
    /// Yalnızca desenin GERÇEKTEN var olan bir veritabanına karşılık geldiği durumlarda işlem
    /// yapılır. Karşılığı olmayan bir desen (ör. kullanıcının bilerek verdiği <c>site%</c> gibi
    /// bir toplu yetki) panelin uydurduğu bir şey değildir; ona dokunmak kullanıcının kararını
    /// ezmek olurdu.
    /// </para>
    /// </summary>
    internal static async Task<IReadOnlyList<string>> RepairAsync(
        MySqlConnection connection,
        CancellationToken cancellationToken)
    {
        var semalar = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT SCHEMA_NAME FROM information_schema.SCHEMATA;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                semalar.Add(reader.GetString(0));
            }
        }

        var adaylar = new List<(string User, string Host, string Db)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT User, Host, Db FROM mysql.db WHERE User <> '';";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                adaylar.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        var daraltilan = new List<string>();

        foreach (var (kullanici, konak, desen) in adaylar)
        {
            if (!IsOverBroad(desen))
            {
                continue;
            }

            // DESENİN GERÇEKTE KAPSADIĞI HER VERİTABANI bulunur -- yalnızca adı desene birebir
            // eşit olan değil.
            //
            // Neden şart: geniş yetki, var olan BİRDEN ÇOK veritabanını kapsıyor olabilir ve
            // kullanıcının uygulaması gerçekten hepsini kullanıyor olabilir. Yalnızca birebir
            // eşleşene dar yetki verip geniş olanı geri almak, diğerlerine erişimi SESSİZCE
            // koparırdı -- yani bir güvenlik düzeltmesi çalışan bir siteyi bozardı. Kapsanan her
            // veritabanına ayrı ayrı dar yetki verilir; hiçbir erişim kaybolmaz, yalnızca
            // GELECEKTE açılacak eşleşen adlar kapsam dışı kalır.
            var kapsanan = semalar.Where(sema => MatchesPattern(sema, desen)).ToList();
            if (kapsanan.Count == 0)
            {
                // Desenin karşılığı olan hiçbir veritabanı yok. Bu, panelin ürettiği bir kayıt
                // değil, kullanıcının bilerek verdiği toplu bir yetki olabilir; dokunmak onun
                // kararını ezmek olurdu.
                continue;
            }

            var kullaniciAlan = $"'{kullanici.Replace("'", "''")}'@'{konak.Replace("'", "''")}'";

            try
            {
                // 1) ÖNCE kapsanan HER veritabanına dar yetki: erişim tek an bile kesilmesin.
                foreach (var sema in kapsanan)
                {
                    await using var grant = connection.CreateCommand();
                    grant.CommandText = $"GRANT ALL PRIVILEGES ON `{Tighten(sema)}`.* TO {kullaniciAlan};";
                    await grant.ExecuteNonQueryAsync(cancellationToken);
                }

                // 2) SONRA geniş olanı geri al.
                await using (var revoke = connection.CreateCommand())
                {
                    revoke.CommandText = $"REVOKE ALL PRIVILEGES ON `{desen}`.* FROM {kullaniciAlan};";
                    await revoke.ExecuteNonQueryAsync(cancellationToken);
                }

                daraltilan.Add($"{kullanici}@{konak} → {string.Join(", ", kapsanan)}");
            }
            catch (MySqlException)
            {
                // Tek bir yetkinin daraltılamaması diğerlerini engellememeli. Başarısız olan
                // GENİŞ hâlinde kalır -- yani hiçbir erişim kaybolmaz, yalnızca temizlik eksik
                // kalır ve bir sonraki çalıştırmada yeniden denenir.
            }
        }

        if (daraltilan.Count > 0)
        {
            await using var flush = connection.CreateCommand();
            flush.CommandText = "FLUSH PRIVILEGES;";
            await flush.ExecuteNonQueryAsync(cancellationToken);
        }

        return daraltilan;
    }
}
