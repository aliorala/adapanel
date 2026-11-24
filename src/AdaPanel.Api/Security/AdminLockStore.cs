using System.Security.Claims;
using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Api.Security;

/// <summary>
/// "Yönetici kilidi": yöneticinin askıya aldığı posta kutusu / kapattığı FTP hesabını MÜŞTERİ yeniden açamaz.
/// </summary>
/// <remarks>
/// Müşteri kendi kutusunu kapatabilir ve açabilir; ama yönetici kötüye kullanım yüzünden kapattıysa
/// aynı yetkiyle kilidi kaldırabilmesi kilidi anlamsız kılar. Kayıt yalnız yönetici işlemlerinde yazılır;
/// yeniden açma yalnız yöneticiye aittir. Kaynak silinince kayıt da silinmelidir (<see cref="ClearAsync"/>).
/// </remarks>
internal static class AdminLockStore
{
    private const string Path = "config/admin-locks.json";

    private static string Key(string kind, string name) => kind + ":" + name.Trim().ToLowerInvariant();

    public static Task MarkAsync(JsonFileStore store, string kind, string name, CancellationToken token) =>
        store.UpdateAsync(Path, new List<string>(), list =>
        {
            if (!list.Contains(Key(kind, name))) list.Add(Key(kind, name));
            return list;
        }, token);

    public static Task ClearAsync(JsonFileStore store, string kind, string name, CancellationToken token) =>
        store.UpdateAsync(Path, new List<string>(), list =>
        {
            list.RemoveAll(item => item == Key(kind, name));
            return list;
        }, token);

    public static async Task<bool> IsLockedAsync(JsonFileStore store, string kind, string name, CancellationToken token) =>
        (await store.ReadAsync(Path, new List<string>(), token)).Contains(Key(kind, name));

    /// <summary>Yeniden açma isteğinde: yönetici kilidi varsa yönetici dışındakiler 403 alır; yönetici açarsa kilit düşer.</summary>
    public static async Task EnsureCanEnableAsync(JsonFileStore store, ClaimsPrincipal user, string kind, string name, CancellationToken token)
    {
        if (!await IsLockedAsync(store, kind, name, token)) return;
        if (!user.IsInRole("Administrator")) throw new AppException(LockedMessageKey(kind), 403, 403);
        await ClearAsync(store, kind, name, token);
    }

    /// <summary>Kapatma isteğinde: yönetici kapatıyorsa kilit yazılır.</summary>
    public static Task RecordDisableAsync(JsonFileStore store, ClaimsPrincipal user, string kind, string name, CancellationToken token) =>
        user.IsInRole("Administrator") ? MarkAsync(store, kind, name, token) : Task.CompletedTask;

    /// <summary>Sabit anahtarlar (dinamik birleştirme yok): çeviri kapsama testi ve arama bunları görebilsin.</summary>
    private static string LockedMessageKey(string kind) => kind switch
    {
        "ftp" => "ftp.locked_by_admin",
        "mail" => "mail.locked_by_admin",
        _ => kind + ".locked_by_admin"
    };
}
