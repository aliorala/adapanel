using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;

namespace AdaPanel.Infrastructure.Backup;

/// <summary>
/// BULUT hedefleri: Dropbox, OneDrive (Microsoft Graph) ve Azure Blob Storage.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden ayrı dosya:</b> <c>BackupService.cs</c> zaten 4000 satırın üzerinde. Üç sağlayıcının
/// yükleme/silme/listeleme yolunu oraya eklemek dosyayı okunamaz hâle getirirdi. Sınıf AYNI sınıf
/// (partial): hedef tipi çözümü, yer kararı (<see cref="BackupLayout"/>), adres temizliği
/// (<c>SanitizeUri</c>) ve "uzak hata yedeği ÖLDÜRMEZ" davranışı tek kaynakta kalır — yani bu
/// dosya PARALEL bir mekanizma değil, aynı mekanizmanın üç yeni dalıdır.
/// </para>
/// <para>
/// <b>Üçünde de dizin AÇILMAZ (MKD/MKCOL yok)</b> ve bu bir eksiklik değil, ölçülmüş bir farktır:
/// <list type="bullet">
/// <item><b>Azure Blob</b>: S3 gibi dizin kavramı YOKTUR; "klasör" yalnızca blob adının önekidir.</item>
/// <item><b>Dropbox</b>: <c>files/upload</c> yoldaki ara klasörleri KENDİLİĞİNDEN açar.</item>
/// <item><b>OneDrive</b>: Graph'ın yol adresleme biçimi (<c>root:/a/b/c:</c>) ara klasörleri
/// kendiliğinden açar.</item>
/// </list>
/// Yani gün → kategori → kaynak düzeni üçünde de KORUNUR, ama fazladan istek atılmaz.
/// </para>
/// <para>
/// <b>Kimlik bilgileri adrese ASLA girmez.</b> Dropbox/OneDrive jetonu <c>Authorization</c>
/// başlığında taşınır; Azure SAS'ı sorgu dizesinde taşır ve kayda yazılırken <c>SanitizeUri</c>
/// ile düşürülür (S3 <c>X-Amz-Signature</c> sızıntısıyla aynı sınıf). OneDrive'ın oturumlu
/// yüklemesinde Graph'ın döndüğü <c>uploadUrl</c> sorgusunda <c>tempauth</c> jetonu taşır —
/// bu adres kayda da hata ayrıntısına da HAM hâliyle yazılmaz.
/// </para>
/// </remarks>
public sealed partial class BackupService
{
    // =====================================================================================
    // SABİTLER
    // =====================================================================================

    private const string DropboxContentHost = "https://content.dropboxapi.com";
    private const string DropboxApiHost = "https://api.dropboxapi.com";

    /// <summary>
    /// OAuth 2.0 jeton ucu. RPC uçlarından (<see cref="DropboxApiHost"/>) AYRI tutuldu: Dropbox
    /// belgeleri jeton ucunu <c>api.dropbox.com</c> altında verir ve ikisini tek sabite bağlamak,
    /// birini değiştirmenin diğerini de sessizce oynatması demekti.
    /// </summary>
    private const string DropboxOAuthHost = "https://api.dropbox.com";
    private const string GraphHost = "https://graph.microsoft.com/v1.0";
    private const string MicrosoftLoginHost = "https://login.microsoftonline.com";

    /// <summary>
    /// Dropbox <c>files/upload</c>'ın ÜST SINIRI 150 MB'dır; üstünde istek reddedilir. Sınıra
    /// dayanmak yerine 120 MB'da oturumlu yüklemeye geçilir — yedek boyutu sıkıştırma/şifreleme
    /// sonrası tam kestirilemez ve "birkaç MB fazla geldi" diye gecelik yedeğin uçmasına değmez.
    /// </summary>
    private const long DropboxSimpleUploadLimit = 120L * 1024 * 1024;

    /// <summary>Dropbox oturumlu yüklemede parça boyutu (belgelenen öneri 8-150 MB arası).</summary>
    private const int DropboxChunkSize = 8 * 1024 * 1024;

    /// <summary>
    /// OneDrive'da 4 MB üstü için Graph OTURUMLU yükleme ister. Eşiği düşük tutmak, tek istekle
    /// giden küçük dosyayı (bağlantı testi) hızlı bırakırken gerçek yedeği doğru yola sokar.
    /// </summary>
    private const long OneDriveSimpleUploadLimit = 4L * 1024 * 1024;

    /// <summary>
    /// OneDrive parça boyutu 320 KiB'ın TAM KATI olmak ZORUNDA (Graph kuralı); 10 MiB = 320 KiB × 32.
    /// Katı olmayan bir parça sunucuda <c>invalidRange</c> ile reddedilir.
    /// </summary>
    private const int OneDriveChunkSize = 320 * 1024 * 32;

    private static readonly JsonSerializerOptions CloudJsonOptions = new();

    // =====================================================================================
    // HEDEF TÜRÜ SÖZLÜĞÜ (tek kaynak)
    //
    // Bu listeler ÜÇ yerde birden gerekiyordu: hedef doğrulama, uzak silme kapsamı ve doğrulama
    // (uzlaştırma). Üçünü ayrı ayrı yazmak, yeni bir hedef eklendiğinde birinin unutulması
    // demekti — nitekim ölçülen hata tam buydu: yeni hedef eklense uzak silme listesi
    // ("s3"/"ftp"/"webdav"/"http") onu DIŞARIDA bırakır ve her silme bir YETİM dosya üretirdi.
    // =====================================================================================

    /// <summary>Panelin tanıdığı bütün uzak hedef türleri (yerel hariç).</summary>
    internal static readonly string[] RemoteTargetTypes =
        ["ftp", "s3", "webdav", "http", "dropbox", "onedrive", "azure"];

    /// <summary>
    /// UZAK kopyası silinebilen hedefler. <c>http</c> dâhildir (DELETE denenir); hepsi burada
    /// olduğu için "yeni hedef ekleyip silme listesini güncellemeyi unutma" hatası artık
    /// yapılamaz — liste tek ve ortaktır.
    /// </summary>
    internal static bool SupportsRemoteDeletion(string? targetType) =>
        !string.IsNullOrWhiteSpace(targetType)
        && RemoteTargetTypes.Contains(targetType.Trim().ToLowerInvariant(), StringComparer.Ordinal);

    /// <summary>
    /// Zamanlama kayıtlarının hedef alanını normalleştirir: TANINMAYAN hedef "local"e düşer.
    ///
    /// NEDEN BURADA: bu kural iki ayrı zamanlayıcıda (AutomationService, SubscriptionBackupSchedule
    /// Service) ELLE yazılmış bir listeyle kopyalanmıştı. Ölçüldü: yeni hedefler (dropbox/onedrive/
    /// azure) o listelerde olmadığı için kullanıcı "Dropbox" seçip kaydettiğinde ayar sessizce
    /// "local"e düşüyor, panel kaydı kabul etmiş gibi görünüyor ve GECE YEDEĞİ UZAĞA HİÇ GİTMİYORDU.
    /// Kullanıcı bunu ancak yedeğe ihtiyaç duyduğunda öğrenirdi. Liste tek kaynağa bağlandı ki
    /// "yeni hedef ekledim, üç ayrı listeyi güncellemeyi unuttum" hatası bir daha yazılamasın.
    /// </summary>
    internal static string NormalizeScheduleTargetType(string? targetType)
    {
        var normalized = (targetType ?? "local").Trim().ToLowerInvariant();
        return RemoteTargetTypes.Contains(normalized, StringComparer.Ordinal) ? normalized : "local";
    }

    /// <summary>Jetonla (OAuth 2.0) konuşan hedefler.</summary>
    private static bool IsOAuthTarget(string targetType) => targetType is "dropbox" or "onedrive";

    // =====================================================================================
    // ADRES NORMALLEŞTİRME
    // =====================================================================================

    /// <summary>
    /// Dropbox/OneDrive için kullanıcının yazdığını adres hâline getirir.
    /// </summary>
    /// <remarks>
    /// Bu iki serviste "URL" diye bir kavram YOKTUR — yalnızca hesap içindeki KLASÖR YOLU vardır.
    /// Kullanıcıyı uydurma bir adres yazmaya zorlamak yerine düz yol (<c>/yedekler</c>) kabul
    /// edilir ve şema eklenir. Şema korunur çünkü kayda yazılan adres (<c>dropbox:/yedekler/...</c>)
    /// hem insan için okunur hem de silme/doğrulama için yeniden ayrıştırılabilir olmalıdır.
    /// </remarks>
    private static string NormalizeCloudFolderUrl(string targetType, string value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.StartsWith($"{targetType}:", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        // Başka bir şema yazılmışsa DOKUNULMAZ: geçersizliği ResolveTarget söylesin, burada
        // sessizce "düzeltmek" kullanıcının yanlış hedefe yedek göndermesine yol açardı.
        if (text.Contains("://", StringComparison.Ordinal))
        {
            return text;
        }

        var path = text.Replace('\\', '/').Trim();
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        return $"{targetType}:{path.TrimEnd('/')}";
    }

    /// <summary>
    /// Adresten servis içindeki YOLU çıkarır (<c>dropbox:/yedekler/x.zip</c> → <c>/yedekler/x.zip</c>).
    /// Yüzde kodlaması çözülür: Dropbox ve Graph yolu HAM (kodlanmamış) bekler.
    /// </summary>
    private static string CloudPathOf(Uri uri) => Uri.UnescapeDataString(uri.AbsolutePath);

    // =====================================================================================
    // JETON ÇÖZÜMÜ (OAuth 2.0)
    // =====================================================================================

    /// <summary>
    /// Dropbox/OneDrive için <c>Bearer</c> jetonunu çözer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// İki kullanım vardır ve ayrım KULLANICI ADI ALANININ DOLU OLUP OLMAMASINDAN okunur:
    /// <list type="bullet">
    /// <item>Kullanıcı adı BOŞ → parola alanındaki değer doğrudan erişim jetonudur. Elle test
    /// için pratiktir ama Dropbox'ta 4, Graph'ta 1 saat sonra ÖLÜR.</item>
    /// <item>Kullanıcı adı DOLU → uygulama kimliği (client id, gizli değil) demektir; parola
    /// alanı YENİLEME (refresh) jetonudur ve her çalıştırmada erişim jetonuna çevrilir. Gecelik
    /// yedeğin aylarca çalışmasının tek yolu budur.</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Uygulama gizli anahtarı (client secret) İSTENMEZ:</b> onu istemek, koruma alanı
    /// olmayan "kullanıcı adı" kutusuna bir SIR yazdırmak olurdu. PKCE ile üretilmiş yenileme
    /// jetonları yalnızca client id ile tazelenir.
    /// </para>
    /// </remarks>
    private async Task<string> ResolveCloudBearerTokenAsync(
        string targetType,
        string clientId,
        string secret,
        string tenant,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new AppException("backup.remote_auth_failed", 401, 400, "Jeton (parola alanı) bos.");
        }

        if (string.IsNullOrWhiteSpace(clientId))
        {
            return secret.Trim();
        }

        var (endpoint, form) = targetType == "dropbox"
            ? ($"{DropboxOAuthHost}/oauth2/token", new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = secret.Trim(),
                ["client_id"] = clientId.Trim()
            })
            : ($"{MicrosoftLoginHost}/{(string.IsNullOrWhiteSpace(tenant) ? "common" : tenant.Trim())}/oauth2/v2.0/token",
                new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = secret.Trim(),
                    ["client_id"] = clientId.Trim(),
                    ["scope"] = "https://graph.microsoft.com/Files.ReadWrite.All offline_access"
                });

        var client = CreateRemoteHttpClient();
        client.Timeout = RemoteUploadStallTimeout;

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // GÖVDE KIRPILARAK taşınır ama jeton ASLA yazılmaz: istek gövdesini (refresh_token
            // içerir) değil, yalnız SUNUCU YANITINI aktarıyoruz.
            throw new AppException(
                "backup.remote_auth_failed",
                (int)response.StatusCode,
                502,
                $"Jeton tazelenemedi ({(int)response.StatusCode} {response.ReasonPhrase}): {Truncate(body, 500)}");
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("access_token", out var token)
                && token.GetString() is { Length: > 0 } value)
            {
                return value;
            }
        }
        catch (JsonException)
        {
            // Aşağıdaki ortak hataya düşer.
        }

        throw new AppException("backup.remote_auth_failed", 502, 502, "Jeton yanitinda access_token yok.");
    }

    private Task<string> ResolveCloudBearerTokenAsync(BackupTarget target, CancellationToken cancellationToken) =>
        ResolveCloudBearerTokenAsync(target.Type, target.UserName, target.Password, target.Region, cancellationToken);

    private Task<string> ResolveCloudBearerTokenAsync(
        string targetType,
        BackupPruneRemoteOptions remote,
        CancellationToken cancellationToken) =>
        ResolveCloudBearerTokenAsync(targetType, remote.UserName, remote.Password, remote.Region, cancellationToken);

    // =====================================================================================
    // DROPBOX
    // =====================================================================================

    /// <summary>
    /// Dropbox'a yükler. 120 MB altındaki dosya TEK istekle, üstündekiler OTURUMLU (parçalı)
    /// gider — <c>files/upload</c>'ın 150 MB sınırı gerçek bir yedeği tek başına reddederdi.
    /// </summary>
    private async Task UploadDropboxAsync(
        string localPath,
        Uri uri,
        BackupTarget target,
        CancellationToken cancellationToken)
    {
        var token = await ResolveCloudBearerTokenAsync(target, cancellationToken);
        var path = CloudPathOf(uri);
        var length = new FileInfo(localPath).Length;

        if (length <= DropboxSimpleUploadLimit)
        {
            await DropboxSimpleUploadAsync(localPath, path, token, cancellationToken);
            return;
        }

        await DropboxSessionUploadAsync(localPath, path, token, length, cancellationToken);
    }

    private async Task DropboxSimpleUploadAsync(
        string localPath,
        string path,
        string token,
        CancellationToken cancellationToken)
    {
        var argument = DropboxArgument(new
        {
            path,
            mode = "overwrite",
            autorename = false,
            mute = true,
            strict_conflict = false
        });

        await using var source = File.OpenRead(localPath);
        using var content = new StreamContent(source);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var response = await SendDropboxAsync(
            $"{DropboxContentHost}/2/files/upload", token, argument, content, cancellationToken);

        await EnsureDropboxSuccessAsync(response, "files/upload", cancellationToken);
    }

    private async Task DropboxSessionUploadAsync(
        string localPath,
        string path,
        string token,
        long length,
        CancellationToken cancellationToken)
    {
        await using var source = File.OpenRead(localPath);
        var buffer = new byte[DropboxChunkSize];

        // 1) Oturumu aç ve İLK parçayı gönder.
        var first = await ReadChunkAsync(source, buffer, cancellationToken);
        using var startResponse = await SendDropboxAsync(
            $"{DropboxContentHost}/2/files/upload_session/start",
            token,
            DropboxArgument(new { close = false }),
            OctetContent(buffer, first),
            cancellationToken);

        var startBody = await EnsureDropboxSuccessAsync(startResponse, "upload_session/start", cancellationToken);
        var sessionId = ReadJsonString(startBody, "session_id")
            ?? throw new AppException("backup.remote_upload_failed", 502, 502, "Dropbox oturum kimligi donmedi.");

        long offset = first;

        // 2) Kalan parçalar. Son parça FINISH ile gider; ayrı bir append daha atmak gereksiz istektir.
        while (offset < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await ReadChunkAsync(source, buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            var sonParca = offset + read >= length;
            if (sonParca)
            {
                using var finishResponse = await SendDropboxAsync(
                    $"{DropboxContentHost}/2/files/upload_session/finish",
                    token,
                    DropboxArgument(new
                    {
                        cursor = new { session_id = sessionId, offset },
                        commit = new { path, mode = "overwrite", autorename = false, mute = true }
                    }),
                    OctetContent(buffer, read),
                    cancellationToken);

                await EnsureDropboxSuccessAsync(finishResponse, "upload_session/finish", cancellationToken);
                return;
            }

            using var appendResponse = await SendDropboxAsync(
                $"{DropboxContentHost}/2/files/upload_session/append_v2",
                token,
                DropboxArgument(new { cursor = new { session_id = sessionId, offset }, close = false }),
                OctetContent(buffer, read),
                cancellationToken);

            await EnsureDropboxSuccessAsync(appendResponse, "upload_session/append_v2", cancellationToken);
            offset += read;
        }

        // Dosya tam olarak parça sınırında bittiyse döngü FINISH atmadan çıkabilir; boş gövdeyle kapat.
        using var closeResponse = await SendDropboxAsync(
            $"{DropboxContentHost}/2/files/upload_session/finish",
            token,
            DropboxArgument(new
            {
                cursor = new { session_id = sessionId, offset },
                commit = new { path, mode = "overwrite", autorename = false, mute = true }
            }),
            OctetContent([], 0),
            cancellationToken);

        await EnsureDropboxSuccessAsync(closeResponse, "upload_session/finish", cancellationToken);
    }

    private async Task<HttpResponseMessage> SendDropboxAsync(
        string endpoint,
        string token,
        string argument,
        HttpContent content,
        CancellationToken cancellationToken)
    {
        var client = CreateRemoteHttpClient();
        client.Timeout = RemoteUploadTimeout;

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("Dropbox-API-Arg", argument);

        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static async Task<string> EnsureDropboxSuccessAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return body;
        }

        throw new AppException(
            "backup.remote_upload_failed",
            (int)response.StatusCode,
            502,
            $"Dropbox {operation} -> {(int)response.StatusCode} {response.ReasonPhrase}\r\n{Truncate(body, 1000)}");
    }

    /// <summary>
    /// <c>Dropbox-API-Arg</c> başlığının değerini üretir.
    /// </summary>
    /// <remarks>
    /// <b>ASCII ZORUNLU.</b> Dropbox bu JSON'u HTTP BAŞLIĞINDA bekler ve başlıklar ASCII'dir.
    /// Site adı kullanıcı verisidir; Türkçe bir alan adı (<c>şirket.com</c>) yola girdiğinde ham
    /// UTF-8 başlık ya .NET tarafından reddedilir ya da karşıda bozuk yola yazılırdı. System.Text.Json
    /// varsayılan kodlayıcısı ASCII dışı her karakteri <c>\uXXXX</c> yapar; bu davranışa BİLEREK
    /// güveniliyor ve testle korunuyor.
    /// </remarks>
    private static string DropboxArgument(object argument) =>
        JsonSerializer.Serialize(argument, CloudJsonOptions);

    private static ByteArrayContent OctetContent(byte[] buffer, int count)
    {
        var content = new ByteArrayContent(buffer, 0, count);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return content;
    }

    private static async Task<int> ReadChunkAsync(Stream source, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await source.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    /// <summary>Dropbox'taki kopyayı siler. Zaten yoksa (409 path/not_found) sorun değildir.</summary>
    private async Task DeleteDropboxAsync(Uri uri, BackupPruneRemoteOptions remote, CancellationToken cancellationToken)
    {
        var token = await ResolveCloudBearerTokenAsync("dropbox", remote, cancellationToken);
        var client = CreateRemoteHttpClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{DropboxApiHost}/2/files/delete_v2")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { path = CloudPathOf(uri) }, CloudJsonOptions),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (IsDropboxNotFound(response.StatusCode, body))
        {
            return;
        }

        throw new AppException(
            "backup.remote_delete_failed",
            (int)response.StatusCode,
            502,
            $"Dropbox delete_v2 -> {(int)response.StatusCode} {response.ReasonPhrase}\r\n{Truncate(body, 500)}");
    }

    // Dropbox "yok" durumunu 409 + gövdedeki "not_found" ile bildirir; 404 DEĞİL.
    private static bool IsDropboxNotFound(HttpStatusCode status, string body) =>
        status == HttpStatusCode.Conflict && body.Contains("not_found", StringComparison.OrdinalIgnoreCase);

    // =====================================================================================
    // ONEDRIVE (Microsoft Graph)
    // =====================================================================================

    /// <summary>
    /// OneDrive'a yükler. 4 MB üstü Graph'ın OTURUMLU yolunu kullanır (belgelenen sınır).
    /// </summary>
    private async Task UploadOneDriveAsync(
        string localPath,
        Uri uri,
        BackupTarget target,
        CancellationToken cancellationToken)
    {
        var token = await ResolveCloudBearerTokenAsync(target, cancellationToken);
        var path = CloudPathOf(uri);
        var length = new FileInfo(localPath).Length;

        if (length <= OneDriveSimpleUploadLimit)
        {
            await OneDriveSimpleUploadAsync(localPath, path, token, cancellationToken);
            return;
        }

        await OneDriveSessionUploadAsync(localPath, path, token, length, cancellationToken);
    }

    private async Task OneDriveSimpleUploadAsync(
        string localPath,
        string path,
        string token,
        CancellationToken cancellationToken)
    {
        var client = CreateRemoteHttpClient();
        client.Timeout = RemoteUploadTimeout;

        await using var source = File.OpenRead(localPath);
        using var content = new StreamContent(source);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var request = new HttpRequestMessage(HttpMethod.Put, GraphItemUrl(path, ":/content"))
        {
            Content = content
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureGraphSuccessAsync(response, "PUT content", cancellationToken);
    }

    /// <summary>
    /// Graph oturumlu yükleme: önce oturum açılır, sonra parçalar <c>Content-Range</c> ile gider.
    /// </summary>
    /// <remarks>
    /// <b>SIZINTI NOKTASI:</b> <c>createUploadSession</c>'ın döndüğü <c>uploadUrl</c> sorgu
    /// dizesinde <c>tempauth=</c> jetonu taşır ve o adres KİMLİK DOĞRULAMASIZ yazma hakkı verir.
    /// Bu yüzden adres ne kayda ne de hata ayrıntısına ham yazılır; hata mesajlarında
    /// <c>SanitizeUri</c>'den geçirilir (sorgu düşer). Kayda yazılan adres zaten
    /// <c>onedrive:/...</c> biçimidir, bu geçici adres değil.
    /// </remarks>
    private async Task OneDriveSessionUploadAsync(
        string localPath,
        string path,
        string token,
        long length,
        CancellationToken cancellationToken)
    {
        var client = CreateRemoteHttpClient();
        client.Timeout = RemoteUploadTimeout;

        using var createRequest = new HttpRequestMessage(
            HttpMethod.Post, GraphItemUrl(path, ":/createUploadSession"))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(
                    new { item = new Dictionary<string, string> { ["@microsoft.graph.conflictBehavior"] = "replace" } },
                    CloudJsonOptions),
                Encoding.UTF8,
                "application/json")
        };
        createRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var createResponse = await client.SendAsync(createRequest, cancellationToken);
        var createBody = await EnsureGraphSuccessAsync(createResponse, "createUploadSession", cancellationToken);

        var uploadUrl = ReadJsonString(createBody, "uploadUrl");
        if (string.IsNullOrWhiteSpace(uploadUrl) || !Uri.TryCreate(uploadUrl, UriKind.Absolute, out var sessionUri))
        {
            throw new AppException("backup.remote_upload_failed", 502, 502, "Graph yukleme oturumu adresi donmedi.");
        }

        await using var source = File.OpenRead(localPath);
        var buffer = new byte[OneDriveChunkSize];
        long offset = 0;

        while (offset < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await ReadChunkAsync(source, buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            using var chunk = OctetContent(buffer, read);
            chunk.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + read - 1, length);

            // Oturum adresi jetonu KENDİ İÇİNDE taşır; Authorization başlığı EKLENMEZ (Graph
            // kuralı — eklenmesi bazı uçlarda 401 verir).
            using var chunkRequest = new HttpRequestMessage(HttpMethod.Put, sessionUri) { Content = chunk };
            using var chunkResponse = await client.SendAsync(chunkRequest, cancellationToken);

            if (!chunkResponse.IsSuccessStatusCode)
            {
                var body = await chunkResponse.Content.ReadAsStringAsync(cancellationToken);
                throw new AppException(
                    "backup.remote_upload_failed",
                    (int)chunkResponse.StatusCode,
                    502,
                    $"Graph parca yukleme -> {(int)chunkResponse.StatusCode} {chunkResponse.ReasonPhrase}\r\n"
                    + $"Oturum={SanitizeUri(sessionUri)}\r\n{Truncate(body, 500)}");
            }

            offset += read;
        }
    }

    /// <summary>
    /// Graph'ın YOLA GÖRE öğe adresi: <c>/me/drive/root:/a/b/c.zip</c> (+ istenirse eylem eki).
    /// </summary>
    /// <remarks>
    /// Segmentler ayrı ayrı kodlanır; <c>/</c> korunur. Tek parça hâlinde
    /// <c>Uri.EscapeDataString</c> uygulamak ayırıcıları da kodlayıp bütün yolu TEK bir dosya
    /// adına çevirirdi — gün/kategori düzeni sessizce kaybolurdu.
    /// </remarks>
    private static string GraphItemUrl(string path, string suffix)
    {
        var segments = path
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString);

        return $"{GraphHost}/me/drive/root:/{string.Join('/', segments)}{suffix}";
    }

    private static async Task<string> EnsureGraphSuccessAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return body;
        }

        throw new AppException(
            "backup.remote_upload_failed",
            (int)response.StatusCode,
            502,
            $"OneDrive {operation} -> {(int)response.StatusCode} {response.ReasonPhrase}\r\n{Truncate(body, 1000)}");
    }

    /// <summary>OneDrive'daki kopyayı siler. 404 = zaten yok.</summary>
    private async Task DeleteOneDriveAsync(Uri uri, BackupPruneRemoteOptions remote, CancellationToken cancellationToken)
    {
        var token = await ResolveCloudBearerTokenAsync("onedrive", remote, cancellationToken);
        var client = CreateRemoteHttpClient();

        using var request = new HttpRequestMessage(HttpMethod.Delete, GraphItemUrl(CloudPathOf(uri), string.Empty));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new AppException(
            "backup.remote_delete_failed",
            (int)response.StatusCode,
            502,
            $"OneDrive DELETE -> {(int)response.StatusCode} {response.ReasonPhrase}\r\n{Truncate(body, 500)}");
    }

    // =====================================================================================
    // AZURE BLOB STORAGE
    // =====================================================================================

    /// <summary>
    /// Azure Blob'a blok blob olarak yükler.
    /// </summary>
    /// <remarks>
    /// Kimlik iki biçimden biriyle verilir ve İKİSİ BİRDEN KULLANILMAZ:
    /// <list type="bullet">
    /// <item><b>Hesap anahtarı</b> (kullanıcı adı = hesap adı): istek SharedKey ile imzalanır ve
    /// adresteki sorgu DÜŞÜRÜLÜR — SAS parametreleri imzaya girip onu bozardı.</item>
    /// <item><b>SAS</b> (adresin sorgusunda): istek imzalanmaz, sorgu olduğu gibi taşınır.</item>
    /// </list>
    /// Tek PUT ile giden blok blob sınırı ~5000 MiB'dır; bu, hâlihazırdaki S3 yolunun sınırıyla
    /// (5 GiB) aynı mertebededir, yani yeni bir kısıt getirmez.
    /// </remarks>
    private async Task UploadAzureBlobAsync(
        string localPath,
        Uri uri,
        BackupTarget target,
        CancellationToken cancellationToken)
    {
        var client = CreateRemoteHttpClient();
        client.Timeout = RemoteUploadTimeout;

        var length = new FileInfo(localPath).Length;
        await using var source = File.OpenRead(localPath);
        using var content = new StreamContent(source);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentLength = length;

        var hedef = AzureRequestUri(uri, target.UserName);
        using var request = new HttpRequestMessage(HttpMethod.Put, hedef) { Content = content };
        request.Headers.TryAddWithoutValidation("x-ms-blob-type", AzureBlobRequestSigner.BlockBlobHeader);
        request.Headers.TryAddWithoutValidation("x-ms-version", AzureBlobRequestSigner.ApiVersion);

        if (!string.IsNullOrWhiteSpace(target.UserName))
        {
            var (authorization, msDate) = AzureBlobRequestSigner.Sign(
                "PUT",
                hedef,
                target.UserName,
                target.Password,
                DateTimeOffset.UtcNow,
                length,
                "application/octet-stream",
                new Dictionary<string, string> { ["x-ms-blob-type"] = AzureBlobRequestSigner.BlockBlobHeader });

            request.Headers.TryAddWithoutValidation("x-ms-date", msDate);
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new AppException(
                "backup.remote_upload_failed",
                (int)response.StatusCode,
                502,
                // ADRES TEMİZLENEREK yazılır: SAS jetonu sorguda durur ve hata ayrıntısı panelde
                // görünür — ham yazmak imzayı ekrana ve günlüklere düşürürdü.
                $"Url={SanitizeUri(hedef)}\r\nStatusCode={(int)response.StatusCode} {response.ReasonPhrase}\r\n{Truncate(body, 1000)}");
        }
    }

    /// <summary>
    /// İsteğe gidecek adres: hesap anahtarı varsa SAS sorgusu DÜŞÜRÜLÜR (ikisi bir arada imzayı
    /// bozar), yoksa sorgu (SAS) olduğu gibi korunur.
    /// </summary>
    private static Uri AzureRequestUri(Uri uri, string accountName)
    {
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return uri;
        }

        return new UriBuilder(uri) { Query = string.Empty, Fragment = string.Empty }.Uri;
    }

    /// <summary>Azure'daki kopyayı siler. 404 = zaten yok.</summary>
    private async Task DeleteAzureBlobAsync(Uri uri, BackupPruneRemoteOptions remote, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(remote.UserName))
        {
            // SAS ile yüklenmiş hedef: kayıttaki adres sorgusundan arındırılmıştır, yeniden
            // imzalanamaz. S3 presigned ile AYNI durum — sessizce "sildim" demektense açıkça
            // söyleriz; çağıran bunu kullanıcıya taşır.
            throw new AppException(
                "backup.remote_delete_failed",
                400,
                400,
                "Azure hedefinde silme icin depolama hesabi adi ve anahtari gerekir (SAS ile silinemez).");
        }

        var client = CreateRemoteHttpClient();
        var hedef = AzureRequestUri(uri, remote.UserName);

        var (authorization, msDate) = AzureBlobRequestSigner.Sign(
            "DELETE", hedef, remote.UserName, remote.Password, DateTimeOffset.UtcNow, 0);

        using var request = new HttpRequestMessage(HttpMethod.Delete, hedef);
        request.Headers.TryAddWithoutValidation("x-ms-version", AzureBlobRequestSigner.ApiVersion);
        request.Headers.TryAddWithoutValidation("x-ms-date", msDate);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);

        using var response = await client.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new AppException(
            "backup.remote_delete_failed",
            (int)response.StatusCode,
            502,
            $"Azure DELETE {SanitizeUri(hedef)} -> {(int)response.StatusCode} {response.ReasonPhrase}\r\n{Truncate(body, 500)}");
    }

    // =====================================================================================
    // UZAKTA VAR MI? (uzlaştırma)
    //
    // ÜÇ SONUÇ vardır ve ikiye indirmek en tehlikeli hatadır: "ulaşamadım" ile "yok" aynı şey
    // DEĞİLDİR. Geçici bir ağ arızasını "yedek silinmiş" saymak, kullanıcının bütün yedek
    // geçmişini işaretleyip güvenini bitirirdi.
    // =====================================================================================

    private enum RemotePresence
    {
        Present,
        Missing,
        Unreachable
    }

    private async Task<(RemotePresence Presence, string Detail)> ProbeRemoteAsync(
        BackupRecordDto record,
        BackupPruneRemoteOptions remote,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(record.RemoteUri, UriKind.Absolute, out var uri))
        {
            return (RemotePresence.Unreachable, "Kayittaki uzak adres cozulemedi.");
        }

        try
        {
            return record.TargetType switch
            {
                "dropbox" => await ProbeDropboxAsync(uri, remote, cancellationToken),
                "onedrive" => await ProbeOneDriveAsync(uri, remote, cancellationToken),
                "azure" => await ProbeHttpAsync(uri, remote, azure: true, cancellationToken),
                "s3" or "webdav" or "http" => await ProbeHttpAsync(uri, remote, azure: false, cancellationToken),
                // FTP artık KAPSAMDA: üst dizin listelenip ad aranıyor (bkz. ProbeFtpPresenceAsync).
                // Aynı ölçüm silme yolunda 550'yi ayırmak için zaten yazıldı; doğrulamada
                // "kontrol edemedim" demeye devam etmek, elimizdeki ölçümü kullanmamak olurdu.
                // Ölçüm YAPILAMADIĞINDA sonuç yine Unreachable'dır — "yok" DEĞİL.
                "ftp" => await ProbeFtpPresenceAsync(
                    uri, new NetworkCredential(remote.UserName, remote.Password), cancellationToken),
                _ => (RemotePresence.Unreachable, $"Bu hedef turu icin uzak dogrulama yok: {record.TargetType}")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // AĞ ARIZASI "YOK" DEĞİLDİR.
            return (RemotePresence.Unreachable, exception.Message);
        }
    }

    private async Task<(RemotePresence, string)> ProbeDropboxAsync(
        Uri uri,
        BackupPruneRemoteOptions remote,
        CancellationToken cancellationToken)
    {
        var token = await ResolveCloudBearerTokenAsync("dropbox", remote, cancellationToken);
        var client = CreateRemoteHttpClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{DropboxApiHost}/2/files/get_metadata")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { path = CloudPathOf(uri) }, CloudJsonOptions),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return (RemotePresence.Present, string.Empty);
        }

        return IsDropboxNotFound(response.StatusCode, body)
            ? (RemotePresence.Missing, string.Empty)
            : (RemotePresence.Unreachable, $"{(int)response.StatusCode} {response.ReasonPhrase}");
    }

    private async Task<(RemotePresence, string)> ProbeOneDriveAsync(
        Uri uri,
        BackupPruneRemoteOptions remote,
        CancellationToken cancellationToken)
    {
        var token = await ResolveCloudBearerTokenAsync("onedrive", remote, cancellationToken);
        var client = CreateRemoteHttpClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, GraphItemUrl(CloudPathOf(uri), string.Empty));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return (RemotePresence.Present, string.Empty);
        }

        return response.StatusCode == HttpStatusCode.NotFound
            ? (RemotePresence.Missing, string.Empty)
            : (RemotePresence.Unreachable, $"{(int)response.StatusCode} {response.ReasonPhrase}");
    }

    private async Task<(RemotePresence, string)> ProbeHttpAsync(
        Uri uri,
        BackupPruneRemoteOptions remote,
        bool azure,
        CancellationToken cancellationToken)
    {
        // İMZASIZ ÖLÇÜM YAPILMAZ (Azure SAS / S3 presigned hedefleri).
        //
        // Kayda yazılan adres SanitizeUri'den geçer, yani SAS/imza SORGUSU DÜŞMÜŞTÜR ve yeniden
        // üretilemez. Bu adrese kimliksiz bir HEAD atmak, özel bir kapta 404 döndürür: Azure
        // varlığı gizlemek için "yok" der. O 404'ü "yedek silinmiş" saymak, DURAN bir off-site
        // yedeği kayıp ilan etmek olurdu — üstelik artık bu ölçüm "artık hiç kopya yok"
        // damgasını da besliyor. Ölçemediğimizde ölçemediğimizi söyleriz.
        if (azure && string.IsNullOrWhiteSpace(remote.UserName))
        {
            return (RemotePresence.Unreachable, "credentials=sas_only; probe=skipped; remote_presence=unknown");
        }

        var client = CreateRemoteHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Head, uri);

        if (azure && !string.IsNullOrWhiteSpace(remote.UserName))
        {
            var (authorization, msDate) = AzureBlobRequestSigner.Sign(
                "HEAD", uri, remote.UserName, remote.Password, DateTimeOffset.UtcNow, 0);
            request.Headers.TryAddWithoutValidation("x-ms-version", AzureBlobRequestSigner.ApiVersion);
            request.Headers.TryAddWithoutValidation("x-ms-date", msDate);
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }
        else if (!azure && !string.IsNullOrWhiteSpace(remote.UserName))
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{remote.UserName}:{remote.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        }

        using var response = await client.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return (RemotePresence.Present, string.Empty);
        }

        // 404/410 dışındaki her şey (401, 403, 405 "HEAD desteklenmiyor", 5xx) BELİRSİZDİR.
        return response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone
            ? (RemotePresence.Missing, string.Empty)
            : (RemotePresence.Unreachable, $"{(int)response.StatusCode} {response.ReasonPhrase}");
    }

    // =====================================================================================
    // UZAK LİSTELEME (yetim taraması)
    //
    // Kaydı OLMAYAN ama hedefte duran dosyalar bugüne kadar hiç görünmüyordu: budama kayıtlar
    // üzerinden çalışıyor, kaydı olmayan dosyaya kimse bakmıyordu. Burada yalnızca LİSTELENİR;
    // SİLİNMEZ — hedefte panele ait olmayan dosyalar da olabilir, kararı kullanıcı verir.
    // =====================================================================================

    /// <summary>
    /// Hedefteki dosya yollarını (taban adresin altında) listeler.
    /// </summary>
    /// <returns>
    /// Listelenebildiyse yollar; hedef türü TEK istekle özyinelemeli listeleme sunmuyorsa
    /// <c>null</c> — çağıran bunu "taranmadı" olarak RAPORLAR, "yetim yok" saymaz.
    /// </returns>
    private async Task<IReadOnlyCollection<string>?> ListRemoteObjectsAsync(
        string targetType,
        Uri baseUri,
        BackupPruneRemoteOptions remote,
        CancellationToken cancellationToken)
    {
        return targetType switch
        {
            "dropbox" => await ListDropboxAsync(baseUri, remote, cancellationToken),
            "azure" => await ListAzureAsync(baseUri, remote, cancellationToken),
            "s3" => await ListS3Async(baseUri, remote, cancellationToken),
            "webdav" => await ListWebDavAsync(baseUri, remote, cancellationToken),
            // FTP ve OneDrive: tek istekle özyinelemeli listeleme YOK (FTP'de dizin dizin
            // dolaşmak, Graph'ta children/delta yürütmek gerekir). Uydurma bir "boş liste"
            // döndürmek "yetim yok" yalanı olurdu.
            _ => null
        };
    }

    private async Task<IReadOnlyCollection<string>> ListDropboxAsync(
        Uri baseUri,
        BackupPruneRemoteOptions remote,
        CancellationToken cancellationToken)
    {
        var token = await ResolveCloudBearerTokenAsync("dropbox", remote, cancellationToken);
        var client = CreateRemoteHttpClient();
        var sonuc = new List<string>();

        var endpoint = $"{DropboxApiHost}/2/files/list_folder";
        var payload = JsonSerializer.Serialize(
            new { path = CloudPathOf(baseUri).TrimEnd('/'), recursive = true }, CloudJsonOptions);

        // Sayfalama: has_more true olduğu sürece continue ile devam edilir. Üst sınır, bozuk bir
        // imleç yüzünden sonsuz dönmemek için.
        for (var sayfa = 0; sayfa < 100; sayfa++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new AppException(
                    "backup.remote_list_failed",
                    (int)response.StatusCode,
                    502,
                    $"Dropbox list_folder -> {(int)response.StatusCode} {Truncate(body, 300)}");
            }

            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("entries", out var entries))
            {
                foreach (var entry in entries.EnumerateArray())
                {
                    if (entry.TryGetProperty(".tag", out var tag) && tag.GetString() == "file"
                        && entry.TryGetProperty("path_display", out var pathValue)
                        && pathValue.GetString() is { Length: > 0 } yol)
                    {
                        sonuc.Add(yol);
                    }
                }
            }

            if (!document.RootElement.TryGetProperty("has_more", out var hasMore) || !hasMore.GetBoolean())
            {
                break;
            }

            endpoint = $"{DropboxApiHost}/2/files/list_folder/continue";
            payload = JsonSerializer.Serialize(
                new { cursor = document.RootElement.GetProperty("cursor").GetString() }, CloudJsonOptions);
        }

        return sonuc;
    }

    private async Task<IReadOnlyCollection<string>> ListAzureAsync(
        Uri baseUri,
        BackupPruneRemoteOptions remote,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(remote.UserName))
        {
            throw new AppException(
                "backup.remote_list_failed", 400, 400, "Azure listeleme icin hesap adi ve anahtari gerekir.");
        }

        // Kap (container) adresi: yolun İLK segmenti kaptır, kalanı önektir.
        var segments = baseUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            throw new AppException("backup.remote_list_failed", 400, 400, "Azure adresinde kap (container) yok.");
        }

        var prefix = string.Join('/', segments.Skip(1).Select(Uri.UnescapeDataString));
        var listUri = new UriBuilder(baseUri)
        {
            Path = "/" + segments[0],
            Query = $"restype=container&comp=list&prefix={Uri.EscapeDataString(prefix)}"
        }.Uri;

        var (authorization, msDate) = AzureBlobRequestSigner.Sign(
            "GET", listUri, remote.UserName, remote.Password, DateTimeOffset.UtcNow, 0);

        var client = CreateRemoteHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, listUri);
        request.Headers.TryAddWithoutValidation("x-ms-version", AzureBlobRequestSigner.ApiVersion);
        request.Headers.TryAddWithoutValidation("x-ms-date", msDate);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new AppException(
                "backup.remote_list_failed",
                (int)response.StatusCode,
                502,
                $"Azure list -> {(int)response.StatusCode} {Truncate(body, 300)}");
        }

        // <EnumerationResults><Blobs><Blob><Name>kap-ici-yol</Name>
        return ParseXmlValues(body, "Blob", "Name")
            .Select(name => "/" + segments[0] + "/" + name)
            .ToArray();
    }

    private async Task<IReadOnlyCollection<string>> ListS3Async(
        Uri baseUri,
        BackupPruneRemoteOptions remote,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(remote.UserName))
        {
            throw new AppException(
                "backup.remote_list_failed", 400, 400, "S3 listeleme icin erisim anahtari gerekir.");
        }

        var segments = baseUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            throw new AppException("backup.remote_list_failed", 400, 400, "S3 adresinde kova (bucket) yok.");
        }

        var prefix = string.Join('/', segments.Skip(1).Select(Uri.UnescapeDataString));
        var listUri = new UriBuilder(baseUri)
        {
            Path = "/" + segments[0],
            Query = $"list-type=2&prefix={Uri.EscapeDataString(prefix)}"
        }.Uri;

        var region = string.IsNullOrWhiteSpace(remote.Region) ? "us-east-1" : remote.Region;
        var (authorization, amzDate, contentSha) = S3RequestSigner.Sign(
            "GET", listUri, remote.UserName, remote.Password, region,
            DateTimeOffset.UtcNow, S3RequestSigner.EmptyPayloadSha256);

        var client = CreateRemoteHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, listUri);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", contentSha);

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new AppException(
                "backup.remote_list_failed",
                (int)response.StatusCode,
                502,
                $"S3 list -> {(int)response.StatusCode} {Truncate(body, 300)}");
        }

        return ParseXmlValues(body, "Contents", "Key")
            .Select(key => "/" + segments[0] + "/" + key)
            .ToArray();
    }

    private async Task<IReadOnlyCollection<string>> ListWebDavAsync(
        Uri baseUri,
        BackupPruneRemoteOptions remote,
        CancellationToken cancellationToken)
    {
        var client = CreateRemoteHttpClient();
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), baseUri);
        // Depth: infinity BAZI sunucularda kapalıdır (RFC 4918 izin verir). Kapalıysa istek
        // hata döner ve çağıran "taranamadı" der — sessizce boş liste DÖNMEZ.
        request.Headers.TryAddWithoutValidation("Depth", "infinity");

        if (!string.IsNullOrWhiteSpace(remote.UserName))
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{remote.UserName}:{remote.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        }

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new AppException(
                "backup.remote_list_failed",
                (int)response.StatusCode,
                502,
                $"WebDAV PROPFIND -> {(int)response.StatusCode} {Truncate(body, 300)}");
        }

        return ParseXmlValues(body, "response", "href")
            .Select(href => Uri.TryCreate(href, UriKind.Absolute, out var mutlak)
                ? Uri.UnescapeDataString(mutlak.AbsolutePath)
                : Uri.UnescapeDataString(href))
            .Where(yol => !yol.EndsWith('/'))
            .ToArray();
    }

    /// <summary>
    /// XML'den <c>&lt;parent&gt;&lt;child&gt;değer&lt;/child&gt;&lt;/parent&gt;</c> değerlerini toplar.
    /// Ad alanı (namespace) YOK SAYILIR: S3, Azure ve WebDAV üç farklı ad alanı kullanır ve
    /// üçünü ayrı ayrı yazmak aynı ayrıştırmanın üç kopyası olurdu.
    /// </summary>
    private static IEnumerable<string> ParseXmlValues(string xml, string parent, string child)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }

        return document.Descendants()
            .Where(element => string.Equals(element.Name.LocalName, parent, StringComparison.OrdinalIgnoreCase))
            .Select(element => element.Descendants()
                .FirstOrDefault(inner => string.Equals(inner.Name.LocalName, child, StringComparison.OrdinalIgnoreCase))
                ?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();
    }

    private static string? ReadJsonString(string body, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty(property, out var value) ? value.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
