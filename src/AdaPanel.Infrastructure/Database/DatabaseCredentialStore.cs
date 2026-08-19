using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Database;

/// <summary>
/// AdaPanel'in oluşturduğu veritabanı kullanıcılarının parolalarını ŞİFRELİ saklar.
/// phpMyAdmin'i o kullanıcı olarak (signon) açıp yalnız kendi veritabanını göstermek için
/// gerekir. Parolalar yalnız panelde, korumalı (ISecretProtector) tutulur.
/// </summary>
public sealed class DatabaseCredentialStore(JsonFileStore fileStore, ISecretProtector secretProtector)
{
    private const string StatePath = "config/db-credentials.json";

    public sealed class Record
    {
        public string Engine { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        public string Host { get; set; } = "%";
        public string DatabaseName { get; set; } = string.Empty;
        public string ProtectedPassword { get; set; } = string.Empty;
    }

    public async Task UpsertAsync(
        string engine,
        string userName,
        string host,
        string databaseName,
        string password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        // Atomik oku-değiştir-yaz (eşzamanlı DB oluşturmalarında kayıp güncelleme olmasın).
        await fileStore.UpdateAsync(StatePath, Array.Empty<Record>(), existing =>
        {
            var records = existing.ToList();
            var index = records.FindIndex(item =>
                string.Equals(item.Engine, engine, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.UserName, userName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Host, host, StringComparison.OrdinalIgnoreCase));

            var record = new Record
            {
                Engine = engine,
                UserName = userName,
                Host = string.IsNullOrWhiteSpace(host) ? "%" : host,
                // Veritabanı adı, mevcut kayıt varsa korunur (parola değişiminde DB bilgisini kaybetme).
                DatabaseName = string.IsNullOrWhiteSpace(databaseName) && index >= 0 ? records[index].DatabaseName : databaseName,
                ProtectedPassword = secretProtector.Protect(password)
            };

            if (index >= 0)
            {
                records[index] = record;
            }
            else
            {
                records.Add(record);
            }

            return records.ToArray();
        }, cancellationToken);
    }

    /// <summary>Verilen veritabanına bağlı (sahip) kullanıcıyı + çözülmüş parolasını döner.</summary>
    public async Task<(string UserName, string Password, string Host)?> FindForDatabaseAsync(
        string engine,
        string databaseName,
        CancellationToken cancellationToken)
    {
        var records = await fileStore.ReadAsync(StatePath, Array.Empty<Record>(), cancellationToken);
        var match = records.FirstOrDefault(item =>
            string.Equals(item.Engine, engine, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.DatabaseName, databaseName, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            return null;
        }

        return (match.UserName, Unprotect(match.ProtectedPassword), match.Host);
    }

    /// <summary>
    /// Kullanıcının panelde saklı parolasını döner (host'tan bağımsız); bilinmiyorsa boş.
    /// </summary>
    /// <remarks>
    /// MySQL'de uzak erişim açmak, aynı kullanıcı için BAŞKA bir host kaydı (yani yeni bir hesap)
    /// oluşturmak demektir ve yeni hesabın parolası mevcut hesapla AYNI olmalıdır. Parola panelde
    /// yoksa hesap oluşturulmaz: kullanıcıya "erişim açıldı" deyip bağlanamayacağı bir hesap
    /// vermek, hiç açmamaktan kötüdür.
    /// </remarks>
    public async Task<string> FindPasswordAsync(string engine, string userName, CancellationToken cancellationToken)
    {
        var records = await fileStore.ReadAsync(StatePath, Array.Empty<Record>(), cancellationToken);
        foreach (var record in records.Where(item =>
                     string.Equals(item.Engine, engine, StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(item.UserName, userName, StringComparison.OrdinalIgnoreCase)))
        {
            var password = Unprotect(record.ProtectedPassword);
            if (password.Length > 0)
            {
                return password;
            }
        }

        return string.Empty;
    }

    public async Task RemoveDatabaseAsync(string engine, string databaseName, CancellationToken cancellationToken)
    {
        await fileStore.UpdateAsync(StatePath, Array.Empty<Record>(), existing => existing
            .Where(item => !(string.Equals(item.Engine, engine, StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(item.DatabaseName, databaseName, StringComparison.OrdinalIgnoreCase)))
            .ToArray(), cancellationToken);
    }

    /// <summary>Bu kullanıcı (engine+user+host) hâlâ herhangi bir kayıtta (başka DB) geçiyor mu?</summary>
    public async Task<bool> IsUserReferencedAsync(string engine, string userName, string host, CancellationToken cancellationToken)
    {
        var records = await fileStore.ReadAsync(StatePath, Array.Empty<Record>(), cancellationToken);
        return records.Any(item =>
            string.Equals(item.Engine, engine, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.UserName, userName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Host, string.IsNullOrWhiteSpace(host) ? "%" : host, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Verilen kullanıcıya ait tüm kimlik kayıtlarını siler (kullanıcı silinince çağrılır).</summary>
    public async Task RemoveUserAsync(string engine, string userName, string host, CancellationToken cancellationToken)
    {
        var normalizedHost = string.IsNullOrWhiteSpace(host) ? "%" : host;
        await fileStore.UpdateAsync(StatePath, Array.Empty<Record>(), existing => existing
            .Where(item => !(string.Equals(item.Engine, engine, StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(item.UserName, userName, StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(item.Host, normalizedHost, StringComparison.OrdinalIgnoreCase)))
            .ToArray(), cancellationToken);
    }

    private string Unprotect(string protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            return string.Empty;
        }

        try
        {
            return secretProtector.Unprotect(protectedValue);
        }
        catch
        {
            return string.Empty;
        }
    }
}
