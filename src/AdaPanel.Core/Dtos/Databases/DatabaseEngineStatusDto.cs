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
}
