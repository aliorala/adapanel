using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Backup;

/// <summary>
/// Uzak yedek hedefinin KİMLİK BİLGİLERİNİ, o yedeği ÜRETEN programın kullandığı kaynaktan okur:
/// sunucu geneli işler için <c>config/automation.json</c>, ABONELİK yedekleri için
/// <c>config/subscription-backups.json</c>.
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
/// <b>ABONELİK YEDEĞİ NEDEN AYRI OKUNUYOR — ölçülmüş hata:</b> abonelik yedekleme programı kendi
/// uzak hedefini ve kimliğini KENDİ dosyasında saklar
/// (<c>SubscriptionBackupScheduleService</c> → <c>config/subscription-backups.json</c>). Bu okuyucu
/// yalnız <c>automation.json</c>'a baktığı sürece, abonelik yedeğinin uzak kopyası HİÇBİR ZAMAN
/// silinemiyordu: elle silme ve budama yanlış (ya da hiç olmayan) kimlikle deniyor, her seferinde
/// başarısız oluyor ve kayıt kalıcı olarak <c>deleted_remote_failed</c> kalıyordu. Eski "sildim"
/// yalanı kapanmıştı; yerine gelen dürüst durum ÇALIŞMIYORDU.
/// </para>
/// <para>
/// <b>Neden ikinci bir AYAR yolu yine açılmadı:</b> burada yeni bir yapılandırma alanı yok —
/// kullanıcının ZATEN girdiği iki kaynak var (Otomasyon ekranı, abonelik yedek programı) ve
/// kayıt hangisiyle üretildiyse (<see cref="BackupRecordDto.SubscriptionUserId"/>) onunla
/// silinir. Kimliği "kayıt başına" çözmek, yükleme ile silmenin AYNI hesabı kullanmasını garanti
/// eden tek yoldur.
/// </para>
/// <para>
/// <b>Sürüklenme riski ve önlemi:</b> alan adları <c>AutomationService.AutomationStateRecord</c>
/// ve <c>SubscriptionBackupScheduleService.ScheduleRecord</c> ile aynı olmak ZORUNDA. Adlar orada
/// değişirse burası sessizce boş kimlik döndürür ve hata "uzak dosya silinmiyor" olarak GEÇ fark
/// edilirdi. Bu yüzden testte gerçek servislerin YAZDIĞI dosya buradan OKUNUR
/// (BackupRemoteDeletionTests, BackupSubscriptionRemoteCredentialTests); adlar ayrılırsa test
/// kırılır.
/// </para>
/// </remarks>
internal sealed class BackupRemoteCredentialReader(JsonFileStore fileStore, ISecretProtector? secretProtector)
{
    /// <summary>Otomasyon ayarlarının kalıcı dosyası (AutomationService ile AYNI yol).</summary>
    internal const string AutomationStatePath = "config/automation.json";

    /// <summary>Abonelik yedek programlarının kalıcı dosyası (SubscriptionBackupScheduleService ile AYNI yol).</summary>
    internal const string SubscriptionStatePath = "config/subscription-backups.json";

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
    /// Kayıt BAŞINA doğru kimliği veren çözümleyici. Varsayılan kimlik
    /// <c>automation.json</c>'dan okunur.
    /// </summary>
    public async Task<BackupRemoteCredentialResolver> ReadResolverAsync(CancellationToken cancellationToken) =>
        await ResolverForAsync(await ReadAsync(cancellationToken), cancellationToken);

    /// <summary>
    /// Kayıt başına çözümleyici, AMA varsayılan kimliği ÇAĞIRAN verir.
    /// </summary>
    /// <remarks>
    /// Budama uçları (<c>PruneAsync</c>, <c>PruneSubscriptionAsync</c>) kimliği kendi
    /// çağıranından (otomasyon işi ya da abonelik programı) alır; onu burada dosyadan yeniden
    /// okumak, çağıranın açıkça verdiği kimliği yok saymak olurdu. Abonelik kimlikleri yine de
    /// EKLENİR: sunucu geneli budama, kapsamına giren abonelik yedeklerini de silmeye çalışır ve
    /// onların kimliği otomasyon ayarlarında YOKTUR.
    /// </remarks>
    public async Task<BackupRemoteCredentialResolver> ResolverForAsync(
        BackupPruneRemoteOptions? varsayilan,
        CancellationToken cancellationToken)
    {
        var abonelikler = new Dictionary<string, BackupPruneRemoteOptions>(StringComparer.OrdinalIgnoreCase);

        // TEK OKUMA, çağrı başına: bu dosya kayıt başına okunsaydı yüz kayıtlık bir budamada yüz
        // kez açılırdı. Sonuç, o çağrının turu boyunca geçerlidir.
        var kayitlar = await fileStore.ReadAsync(
            SubscriptionStatePath,
            Array.Empty<SubscriptionTargetProjection>(),
            cancellationToken);

        foreach (var kayit in kayitlar)
        {
            if (string.IsNullOrWhiteSpace(kayit.UserId)
                || string.IsNullOrWhiteSpace(kayit.TargetType)
                || string.Equals(kayit.TargetType, "local", StringComparison.OrdinalIgnoreCase))
            {
                // Hedefi YEREL olan programın uzak kimliği yoktur; boş bir kimlik eklemek,
                // varsayılanı (otomasyon kimliğini) gölgeleyip silmeyi imkânsızlaştırırdı.
                continue;
            }

            abonelikler[kayit.UserId] = new BackupPruneRemoteOptions
            {
                UserName = kayit.RemoteUserName ?? string.Empty,
                Password = Unprotect(kayit.RemotePasswordProtected),
                Region = kayit.RemoteRegion ?? string.Empty
            };
        }

        return new BackupRemoteCredentialResolver(varsayilan, abonelikler);
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

    internal async Task<(string TargetType, string RemoteUrl, BackupPruneRemoteOptions Credentials)> ReadUploadTargetAsync(CancellationToken cancellationToken)
    {
        var state = await fileStore.ReadAsync(AutomationStatePath, new RemoteTargetProjection(), cancellationToken);
        return (state.BackupTargetType ?? "local", state.BackupRemoteUrl ?? string.Empty, new BackupPruneRemoteOptions
        {
            UserName = state.BackupRemoteUserName ?? string.Empty,
            Password = Unprotect(state.BackupRemotePasswordProtected),
            Region = state.BackupRemoteRegion ?? string.Empty
        });
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
    /// <c>subscription-backups.json</c>'un YALNIZCA ihtiyacımız olan alanları. Adlar
    /// <c>SubscriptionBackupScheduleService.ScheduleRecord</c> ile AYNI olmak zorundadır
    /// (sürüklenme alarmı: BackupSubscriptionRemoteCredentialTests).
    /// </summary>
    private sealed class SubscriptionTargetProjection
    {
        public string UserId { get; set; } = string.Empty;

        public string TargetType { get; set; } = "local";

        public string RemoteUserName { get; set; } = string.Empty;

        public string RemoteRegion { get; set; } = string.Empty;

        public string RemotePasswordProtected { get; set; } = string.Empty;
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

/// <summary>
/// Bir yedek kaydını, onu ÜRETEN programın uzak kimliğiyle eşleştirir.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden kayıt başına:</b> panelde iki ayrı uzak hedef tanımı olabilir — sunucu geneli
/// otomasyon ve abonelik başına yedek programı. Yükleme her zaman kaydı ÜRETEN programın
/// kimliğiyle yapılır; silme başka bir kimlikle denenirse hedef reddeder ve kayıt kalıcı olarak
/// <c>deleted_remote_failed</c> kalır. Ölçülen hata tam buydu.
/// </para>
/// <para>
/// <b>Bulunamazsa VARSAYILANA düşülür, boş kimlik ÜRETİLMEZ:</b> abonelik programı silinmiş ya da
/// yerele çevrilmiş olabilir; o durumda bilinen tek kimlik sunucu genelidir. Denemek ve
/// başarısızlığı kullanıcıya göstermek, hiç denememekten iyidir (yetim üretmez).
/// </para>
/// </remarks>
internal sealed class BackupRemoteCredentialResolver(
    BackupPruneRemoteOptions? varsayilan,
    IReadOnlyDictionary<string, BackupPruneRemoteOptions> abonelikKimlikleri)
{
    /// <summary>Sunucu geneli (otomasyon ya da çağıranın verdiği) kimlik.</summary>
    public BackupPruneRemoteOptions? Default => varsayilan;

    /// <summary>Bu kaydın uzak kopyasına dokunmak için kullanılacak kimlik.</summary>
    public BackupPruneRemoteOptions? For(BackupRecordDto record)
    {
        if (!string.IsNullOrWhiteSpace(record.SubscriptionUserId)
            && abonelikKimlikleri.TryGetValue(record.SubscriptionUserId, out var abonelik))
        {
            return abonelik;
        }

        return varsayilan;
    }
}
