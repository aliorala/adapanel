using AdaPanel.Core.Common;
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
        // Missing/null legacy metadata is not evidence that the panel created an account.
        public bool? CreatedByPanel { get; set; }
        public bool PendingGrant { get; set; }
        // A durable candidate is not a usable credential until ALTER returned successfully.
        public string PendingProtectedPassword { get; set; } = string.Empty;
    }

    public Task UpsertAsync(
        string engine,
        string userName,
        string host,
        string databaseName,
        string password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
        {
            return Task.CompletedTask;
        }
        return WriteCredentialAsync(engine, userName, host, databaseName, password, created: false, cancellationToken);
    }

    internal Task RecordCreatedAsync(string engine, string userName, string host, string databaseName, string password, CancellationToken cancellationToken) =>
        WriteCredentialAsync(engine, userName, host, databaseName, password, created: true, cancellationToken);

    internal Task BeginPasswordChangeAsync(string engine, string userName, string host, string password, CancellationToken cancellationToken)
    {
        // Protect and persist before SQL. A protection/write failure must leave the server untouched.
        var candidate = secretProtector.Protect(password);
        return fileStore.UpdateAsync(StatePath, Array.Empty<Record>(), existing =>
        {
            var records = existing.ToList();
            var account = records.Where(item => DatabaseAccountIdentity.Same(
                item.Engine, item.UserName, item.Host, engine, userName, host)).ToArray();
            if (account.Length == 0)
            {
                var record = NewRecord(engine, userName, host, string.Empty, string.Empty, created: false, pending: false);
                records.Add(record);
                account = [record];
            }
            foreach (var item in account) item.PendingProtectedPassword = candidate;
            return records.ToArray();
        }, cancellationToken);
    }

    internal Task ConfirmPasswordChangeAsync(string engine, string userName, string host, CancellationToken cancellationToken) =>
        fileStore.UpdateAsync(StatePath, Array.Empty<Record>(), existing =>
        {
            var account = existing.Where(item => DatabaseAccountIdentity.Same(
                item.Engine, item.UserName, item.Host, engine, userName, host)).ToArray();
            if (account.Length == 0 || account.Any(item => item.PendingProtectedPassword.Length == 0))
                throw new InvalidOperationException("The durable database password change is missing.");
            foreach (var item in account)
            {
                item.ProtectedPassword = item.PendingProtectedPassword;
                item.PendingProtectedPassword = string.Empty;
            }
            return existing;
        }, cancellationToken);

    private async Task WriteCredentialAsync(string engine, string userName, string host, string databaseName, string password,
        bool created, CancellationToken cancellationToken)
    {
        // Atomik oku-değiştir-yaz (eşzamanlı DB oluşturmalarında kayıp güncelleme olmasın).
        await fileStore.UpdateAsync(StatePath, Array.Empty<Record>(), existing =>
        {
            var records = existing.ToList();
            var account = records.Where(item => DatabaseAccountIdentity.Same(
                item.Engine, item.UserName, item.Host, engine, userName, host)).ToArray();
            var protectedPassword = string.IsNullOrWhiteSpace(password) ? string.Empty : secretProtector.Protect(password);
            // One account can serve several DBs. A password update affects every reference;
            // linking another DB must not erase any existing reference.
            foreach (var item in account)
            {
                item.Engine = DatabaseResourceIdentity.NormalizeEngine(engine);
                item.ProtectedPassword = protectedPassword;
                if (created)
                {
                    item.CreatedByPanel = true;
                    // A positively confirmed CREATE establishes a new account/password;
                    // stale metadata from an externally removed account must not hide it.
                    item.PendingProtectedPassword = string.Empty;
                }
            }
            if (account.Length == 0 || (!string.IsNullOrWhiteSpace(databaseName) &&
                !account.Any(item => DatabaseResourceIdentity.Same(item.Engine, item.DatabaseName, engine, databaseName))))
            {
                var record = NewRecord(engine, userName, host, databaseName, protectedPassword,
                    created || account.Any(item => item.CreatedByPanel == true), pending: created && databaseName.Length > 0);
                record.PendingProtectedPassword = account.FirstOrDefault(item => item.PendingProtectedPassword.Length > 0)?.PendingProtectedPassword ?? string.Empty;
                records.Add(record);
            }
            else if (created && databaseName.Length > 0)
                foreach (var item in account.Where(item => DatabaseResourceIdentity.Same(item.Engine, item.DatabaseName, engine, databaseName)))
                    item.PendingGrant = true;

            return records.ToArray();
        }, cancellationToken);
    }

    internal async Task BeginReferenceAsync(string engine, string userName, string host, string databaseName, CancellationToken cancellationToken)
    {
        await fileStore.UpdateAsync(StatePath, Array.Empty<Record>(), existing =>
        {
            var account = existing.Where(item => DatabaseAccountIdentity.Same(
                item.Engine, item.UserName, item.Host, engine, userName, host)).ToArray();
            if (account.Any(item => DatabaseResourceIdentity.Same(item.Engine, item.DatabaseName, engine, databaseName)))
                return existing;
            // Save intent before GRANT: an uncertain SQL result or a failed confirmation
            // must retain the account across requests and process restarts.
            var password = account.FirstOrDefault(item => item.ProtectedPassword.Length > 0)?.ProtectedPassword ?? string.Empty;
            var record = NewRecord(engine, userName, host, databaseName, password,
                account.Any(item => item.CreatedByPanel == true), pending: true);
            record.PendingProtectedPassword = account.FirstOrDefault(item => item.PendingProtectedPassword.Length > 0)?.PendingProtectedPassword ?? string.Empty;
            return [.. existing, record];
        }, cancellationToken);
    }

    internal Task ConfirmReferenceAsync(string engine, string userName, string host, string databaseName, CancellationToken cancellationToken) =>
        fileStore.UpdateAsync(StatePath, Array.Empty<Record>(), existing =>
        {
            var matches = existing.Where(item => DatabaseAccountIdentity.Same(item.Engine, item.UserName, item.Host, engine, userName, host) &&
                DatabaseResourceIdentity.Same(item.Engine, item.DatabaseName, engine, databaseName)).ToArray();
            if (matches.Length == 0) throw new InvalidOperationException("The durable database account reference is missing.");
            foreach (var item in matches) item.PendingGrant = false;
            return existing;
        }, cancellationToken);

    internal async Task<IReadOnlyList<(string UserName, string Host)>> FindCleanupAccountsAsync(string engine, string databaseName, CancellationToken cancellationToken)
    {
        var records = await fileStore.ReadAsync(StatePath, Array.Empty<Record>(), cancellationToken);
        return records.Where(item => item.CreatedByPanel == true && DatabaseResourceIdentity.Same(item.Engine, item.DatabaseName, engine, databaseName))
            .DistinctBy(item => (DatabaseAccountIdentity.FamilyKey(item.Engine, item.UserName), DatabaseAccountIdentity.Host(item.Engine, item.Host)))
            .Select(item => (item.UserName, item.Host)).ToArray();
    }

    private static Record NewRecord(string engine, string userName, string host, string databaseName, string password, bool created, bool pending) => new()
    {
        Engine = DatabaseResourceIdentity.NormalizeEngine(engine), UserName = userName.Trim(),
        Host = DatabaseAccountIdentity.Host(engine, host), DatabaseName = databaseName.Trim(), ProtectedPassword = password,
        CreatedByPanel = created, PendingGrant = pending
    };

    /// <summary>Verilen veritabanına bağlı (sahip) kullanıcıyı + çözülmüş parolasını döner.</summary>
    public async Task<(string UserName, string Password, string Host)?> FindForDatabaseAsync(
        string engine,
        string databaseName,
        CancellationToken cancellationToken)
    {
        var records = await fileStore.ReadAsync(StatePath, Array.Empty<Record>(), cancellationToken);
        var match = records.Where(item => !item.PendingGrant && item.PendingProtectedPassword.Length == 0 && DatabaseResourceIdentity.Same(item.Engine, item.DatabaseName, engine, databaseName))
            .OrderByDescending(item => MySqlAccountHosts.IsLocal(item.Host)).FirstOrDefault();

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
        var family = records.Where(item => DatabaseAccountIdentity.SameFamily(item.Engine, item.UserName, engine, userName)).ToArray();
        // Remote access copies a family password to other hosts. Do not propagate a stale
        // host's value while another host's password result remains uncertain.
        if (family.Any(item => item.PendingProtectedPassword.Length > 0)) return string.Empty;
        foreach (var record in family)
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
            .Where(item => !DatabaseResourceIdentity.Same(item.Engine, item.DatabaseName, engine, databaseName))
            .ToArray(), cancellationToken);
    }

    /// <summary>Bu kullanıcı (engine+user+host) hâlâ herhangi bir kayıtta (başka DB) geçiyor mu?</summary>
    public async Task<bool> IsUserReferencedAsync(string engine, string userName, string host, CancellationToken cancellationToken)
    {
        var records = await fileStore.ReadAsync(StatePath, Array.Empty<Record>(), cancellationToken);
        return records.Any(item =>
            DatabaseAccountIdentity.Same(item.Engine, item.UserName, item.Host, engine, userName, host));
    }

    /// <summary>Verilen kullanıcıya ait tüm kimlik kayıtlarını siler (kullanıcı silinince çağrılır).</summary>
    public async Task RemoveUserAsync(string engine, string userName, string host, CancellationToken cancellationToken)
    {
        await fileStore.UpdateAsync(StatePath, Array.Empty<Record>(), existing => existing
            .Where(item => !DatabaseAccountIdentity.Same(item.Engine, item.UserName, item.Host, engine, userName, host))
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
