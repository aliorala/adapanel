using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Backup;

/// <summary>
/// Uzak yedek hedefinin KİMLİK BİLGİLERİNİ, otomatik temizliğin (budama) okuduğu AYNI kaynaktan
/// okur: <c>config/automation.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden gerekiyor:</b> elle silme (<c>DeleteBackupAsync</c>) uzak kopyayı silemiyordu, çünkü
/// kimlik bilgilerine erişimi yoktu — kayıt siliniyor, uzak dosya UZAKTA KALIYORDU. O dosya
/// artık YETİMDİR: budama kayıtlar üzerinden çalıştığı için panel onu bir daha ne görebilir ne
/// silebilir. Kullanıcı "sildim" sanır; veri üçüncü taraf depoda durmaya devam eder.
/// </para>
/// <para>
/// <b>Neden ikinci bir ayar yolu AÇILMADI:</b> "elle silme için ayrı kimlik alanları" eklemek,
/// aynı hedefin iki yerde tanımlandığı ve biri güncellenip diğerinin unutulduğu klasik tuzaktır.
/// Kaynak TEKTİR: kullanıcının Otomasyon ekranında girdiği hedef.
/// </para>
/// <para>
/// <b>Neden <c>IAutomationService</c> enjekte edilmiyor:</b> <c>AutomationService</c> zaten
/// <c>IBackupService</c>'e bağımlıdır; ters yönde bağımlılık DI kapsayıcısında DÖNGÜ olur ve panel
/// açılışta patlar. Bu yüzden dosya doğrudan (salt-okunur) okunur.
/// </para>
/// <para>
/// <b>Sürüklenme riski ve önlemi:</b> alan adları <c>AutomationService.AutomationStateRecord</c>
/// ile aynı olmak ZORUNDA. Adlar orada değişirse burası sessizce boş kimlik döndürür ve hata
/// "uzak dosya silinmiyor" olarak GEÇ fark edilirdi. Bu yüzden testte gerçek
/// <c>AutomationService</c> ile YAZILAN dosya buradan OKUNUR (BackupRemoteDeletionTests); adlar
/// ayrılırsa test kırılır.
/// </para>
/// </remarks>
internal sealed class BackupRemoteCredentialReader(JsonFileStore fileStore, ISecretProtector? secretProtector)
{
    /// <summary>Otomasyon ayarlarının kalıcı dosyası (AutomationService ile AYNI yol).</summary>
    internal const string AutomationStatePath = "config/automation.json";

    /// <summary>
    /// Kayıtlı uzak hedefin silme için gereken kimlik bilgileri. Hedef yerelse ya da hiç
    /// tanımlanmamışsa <c>null</c> döner — kimliksiz uzak silme denemek anlamsız istektir.
    /// </summary>
    public async Task<BackupPruneRemoteOptions?> ReadAsync(CancellationToken cancellationToken)
    {
        var state = await fileStore.ReadAsync(AutomationStatePath, new RemoteTargetProjection(), cancellationToken);

        if (string.IsNullOrWhiteSpace(state.BackupTargetType)
            || string.Equals(state.BackupTargetType, "local", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new BackupPruneRemoteOptions
        {
            UserName = state.BackupRemoteUserName ?? string.Empty,
            Password = Unprotect(state.BackupRemotePasswordProtected),
            Region = state.BackupRemoteRegion ?? string.Empty
        };
    }

    /// <summary>
    /// Yetim taraması için hedefin TABAN adresi ve türü. Silme kaydın adresinden yapılır; yetim
    /// taraması ise kayıtta OLMAYAN dosyaları aradığı için tabandan başlamak zorundadır.
    /// </summary>
    public async Task<(string TargetType, string RemoteUrl)> ReadTargetAsync(CancellationToken cancellationToken)
    {
        var state = await fileStore.ReadAsync(AutomationStatePath, new RemoteTargetProjection(), cancellationToken);
        return (state.BackupTargetType ?? "local", state.BackupRemoteUrl ?? string.Empty);
    }

    private string Unprotect(string? protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue) || secretProtector is null)
        {
            return string.Empty;
        }

        try
        {
            return secretProtector.Unprotect(protectedValue);
        }
        catch
        {
            // Çözülemeyen parola (anahtar döndü, dosya başka makineden geldi) BOŞ döner.
            // Fırlatmak, silme akışının tamamını durdururdu; boş kimlikle uzak silme başarısız
            // olur ve kullanıcı bunu kaydın üzerinde AÇIKÇA görür (sessiz yetim üretmez).
            return string.Empty;
        }
    }

    /// <summary>
    /// <c>automation.json</c>'un YALNIZCA ihtiyacımız olan alanları. Tam kaydı kopyalamak, o
    /// dosyanın her yeni alanını burada da güncellemek zorunda bırakırdı; okunmayan alanlar
    /// <c>JsonFileStore</c> tarafından zaten yok sayılır.
    /// </summary>
    private sealed class RemoteTargetProjection
    {
        public string BackupTargetType { get; set; } = "local";

        public string BackupRemoteUrl { get; set; } = string.Empty;

        public string BackupRemoteUserName { get; set; } = string.Empty;

        public string BackupRemoteRegion { get; set; } = string.Empty;

        public string BackupRemotePasswordProtected { get; set; } = string.Empty;
    }
}
