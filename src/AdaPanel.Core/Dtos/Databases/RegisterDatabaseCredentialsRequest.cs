namespace AdaPanel.Core.Dtos.Databases;

/// <summary>
/// Windows'ta zaten kurulu (AdaPanel tarafından kurulmamış) bir veritabanı motorunu
/// panele tanıtmak için yönetici kimlik bilgileri. Panel bağlantıyı doğrular ve şifrelenmiş
/// olarak saklar; ardından bu motoru tıpkı kendi kurduğu motor gibi yönetir.
/// </summary>
public sealed class RegisterDatabaseCredentialsRequest
{
    public string Engine { get; init; } = string.Empty;

    /// <summary>Yönetici kullanıcı (boşsa motorun varsayılanı: MySQL/MariaDB=root, PostgreSQL=postgres).</summary>
    public string? AdminUserName { get; init; }

    public string Password { get; init; } = string.Empty;

    /// <summary>Sunucunun dinlediği port (0/boş ise motorun varsayılan portu kullanılır).</summary>
    public int Port { get; init; }
}
