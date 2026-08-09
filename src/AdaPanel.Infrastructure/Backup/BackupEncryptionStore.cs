using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Backup;

/// <summary>
/// Yedek şifreleme parolasının panel tarafındaki kasası.
/// <para>
/// <b>Neden saklamak zorundayız:</b> yedeklerin çoğu ZAMANLANMIŞ görevle, kimse ekranda değilken
/// alınır. Parolayı yalnızca istekte kabul etseydik, gecelik yedekler ya şifresiz kalırdı ya da hiç
/// alınmazdı. Bu yüzden parola panelde, projedeki kimlik kasası kalıbının aynısıyla
/// (<see cref="ISecretProtector"/> → DataProtection/DPAPI) ŞİFRELİ saklanır ve düz metin olarak
/// hiçbir yere yazılmaz.
/// </para>
/// <para>
/// <b>Bunun anlamı dürüstçe söylenmelidir:</b> parolayı sunucudaki panel çözebiliyor. Yani bu
/// koruma, "yedeği üçüncü taraf depoda (S3/FTP/WebDAV) ve elden ele dolaşırken okunamaz kılmak"
/// içindir; sunucunun kendisi ele geçerse koruma sağlamaz.
/// </para>
/// <para>
/// <b>PAROLA KAYBOLURSA ŞİFRELİ YEDEKLER GERİ GETİRİLEMEZ.</b> Panel yeniden kurulduğunda
/// DataProtection anahtarları (makineye bağlı) gitmişse buradaki kayıt da çözülemez — bu yüzden
/// parolanın panelin DIŞINDA da bir kopyası olmalıdır.
/// </para>
/// </summary>
public sealed class BackupEncryptionStore(JsonFileStore fileStore, ISecretProtector secretProtector)
{
    private const string StatePath = "config/backup-encryption.json";

    public sealed class State
    {
        /// <summary>Yeni yedekler varsayılan olarak şifrelensin mi.</summary>
        public bool Enabled { get; set; }

        /// <summary>DPAPI/DataProtection ile korunmuş parola. Düz metin ASLA yazılmaz.</summary>
        public string ProtectedPassword { get; set; } = string.Empty;

        public DateTime UpdatedAtUtc { get; set; }
    }

    public Task<State> GetStateAsync(CancellationToken cancellationToken = default) =>
        fileStore.ReadAsync(StatePath, new State(), cancellationToken);

    /// <summary>
    /// Kayıtlı parolayı çözer. Boş dize = "kayıtlı parola YOK"; şifreleme kapalıysa da boş döner
    /// (kapalıyken parolayı kullanmak, kullanıcının kapattığı özelliği arkasından açmak olurdu).
    /// </summary>
    public async Task<string> GetPasswordAsync(CancellationToken cancellationToken = default)
    {
        var state = await GetStateAsync(cancellationToken);
        if (!state.Enabled || string.IsNullOrWhiteSpace(state.ProtectedPassword))
        {
            return string.Empty;
        }

        try
        {
            return secretProtector.Unprotect(state.ProtectedPassword);
        }
        catch
        {
            // Çözülemiyor (DataProtection anahtarları değişmiş/makine taşınmış). Burada eski bir
            // parolayı tahmin etmek yok: "parola yok" deriz, çağıran da şifreleyemediğini görür.
            return string.Empty;
        }
    }

    /// <summary>
    /// Paneldeki varsayılan yedek parolasını kaydeder. Boş parola ile çağrılırsa kayıt TEMİZLENİR
    /// ve şifreleme kapatılır (yeni yedekler yeniden şifresiz alınır).
    /// </summary>
    public async Task<State> SetPasswordAsync(string? password, CancellationToken cancellationToken = default)
    {
        var temiz = (password ?? string.Empty).Trim();
        var state = new State
        {
            Enabled = temiz.Length > 0,
            ProtectedPassword = temiz.Length > 0 ? secretProtector.Protect(temiz) : string.Empty,
            UpdatedAtUtc = DateTime.UtcNow
        };

        await fileStore.WriteAsync(StatePath, state, cancellationToken);
        return state;
    }
}
