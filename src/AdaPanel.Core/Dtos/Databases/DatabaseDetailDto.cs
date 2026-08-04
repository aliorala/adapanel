namespace AdaPanel.Core.Dtos.Databases;

/// <summary>Bir veritabanına yetkili kullanıcı (konağıyla birlikte).</summary>
public sealed class DatabaseUserRefDto
{
    public string UserName { get; init; } = string.Empty;

    /// <summary>Hangi konaktan bağlanabildiği ("localhost", "%").</summary>
    public string Host { get; init; } = string.Empty;
}

/// <summary>
/// Tek bir veritabanının detay kartı: bağlantı ucu, kullanıcıları ve boyutu.
/// <para>
/// TEK UÇTAN gelir. Boyut, kullanıcı listesi ve bağlantı bilgisi için ayrı ayrı istek atmak,
/// on veritabanlı bir sunucuda kart açılışını üç katına çıkarırdı.
/// </para>
/// </summary>
public sealed class DatabaseDetailDto
{
    public string Engine { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    /// <summary>Uygulamanın bağlanacağı adres ("localhost").</summary>
    public string Host { get; init; } = string.Empty;

    public int Port { get; init; }

    /// <summary>Ekranda gösterilen motor adı ("MariaDB").</summary>
    public string EngineName { get; init; } = string.Empty;

    public IReadOnlyList<DatabaseUserRefDto> Users { get; init; } = [];

    /// <summary>
    /// Veri + indeks boyutu (bayt). Ölçülemediyse null — SIFIR DEĞİL.
    /// <para>
    /// Ayrım önemli: "0 bayt" boş bir veritabanı demektir, "ölçülemedi" ise bilmediğimiz.
    /// İkisini aynı göstermek, kullanıcının sorunu olmadığını sanmasına yol açardı.
    /// </para>
    /// <para>
    /// Değer YAKLAŞIKTIR. InnoDB'de <c>information_schema</c> boyutları gerçek zamanlı tutmaz;
    /// sunucu bunları düzensiz aralıklarla tazeler. Doğruluğu artırmak için <c>ANALYZE TABLE</c>
    /// çalıştırmak tabloyu kilitler — boyut göstermek için kabul edilemez bir bedel.
    /// </para>
    /// </summary>
    public long? SizeBytes { get; init; }

    /// <summary>Kaç tablo olduğu; boyut anlamlandırmayı kolaylaştırır.</summary>
    public int TableCount { get; init; }

    /// <summary>Bağlı olduğu site (panelin kendi kaydı); boş olabilir.</summary>
    public string SiteName { get; init; } = string.Empty;
}
