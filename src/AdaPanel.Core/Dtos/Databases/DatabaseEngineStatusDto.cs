namespace AdaPanel.Core.Dtos.Databases;

/// <summary>
/// Bir veritabanı motorunun panel açısından durumu: kurulu mu, yönetilebilir mi
/// (kimlik bilgileri var mı) ve sistemde mi bulundu (AdaPanel mi kurdu).
/// </summary>
public sealed class DatabaseEngineStatusDto
{
    public string Engine { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public bool Installed { get; init; }

    /// <summary>true ise panel bağlanıp yönetebiliyor; false ise yönetici parolası gerekiyor.</summary>
    public bool Ready { get; init; }

    /// <summary>true ise motor Windows'ta zaten kuruluydu ve AdaPanel keşfetti.</summary>
    public bool SystemDetected { get; init; }

    public string Host { get; init; } = string.Empty;

    public int Port { get; init; }

    public string AdminUserName { get; init; } = string.Empty;

    /// <summary>
    /// Bu motorda "guvenlik sikilastirmasi" (<c>POST /api/v1/db/harden</c>) anlamli bir is yapiyor mu.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MOTOR LISTESI ARAYUZDE ELLE YAZILMAZ. Arayuz dugmeyi <c>engine === "mysql" ||
    /// engine === "mariadb"</c> diye sabit bir listeyle gosteriyordu. Sonucu olculdu: PostgreSQL
    /// icin yazilan sertlestirme (ortak PUBLIC erisiminin kaldirilmasi) hicbir yerden
    /// CAGRILAMIYORDU -- uc vardi, kapisi yoktu. Yetenegi motorun kendi saglayicisi bildirir
    /// (<c>IDatabaseEngineProvider.SupportsHardening</c>), arayuz yalnizca bu bayragi okur;
    /// yeni bir motor eklendiginde iki yerde birden guncelleme gerekmez.
    /// </para>
    /// </remarks>
    public bool SupportsHardening { get; init; }
}
