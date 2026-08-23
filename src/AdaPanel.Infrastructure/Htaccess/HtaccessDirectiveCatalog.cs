namespace AdaPanel.Infrastructure.Htaccess;

/// <summary>
/// Hangi direktifi UYGULUYORUZ, hangisini uygulamıyoruz ve NEDEN.
/// </summary>
/// <remarks>
/// <para>
/// <b>NEDEN AYRI BİR KATALOG:</b> "tanımadığım direktifi sessizce atla" bu deponun en pahalı hata
/// sınıfıdır — kullanıcı <c>.htaccess</c>'ini yapıştırır, panel "kaydedildi" der, site yanlış
/// çalışır ve kimse nedenini bilmez. Burada üç ayrı durum vardır ve üçü de kullanıcıya FARKLI
/// anlatılır:
/// </para>
/// <list type="number">
/// <item><b>Uygulanıyor</b> — katalogda var, motor gerçekten çalıştırıyor.</item>
/// <item><b>Tanınıyor ama uygulanmıyor</b> — Apache direktifi olduğunu biliyoruz, sebebini yazıyoruz.</item>
/// <item><b>Tanınmıyor</b> — Apache'de de olmayabilir; yazım hatası ihtimali de söylenir.</item>
/// </list>
/// </remarks>
internal static class HtaccessDirectiveCatalog
{
    /// <summary>Motorun GERÇEKTEN uyguladığı direktifler.</summary>
    private static readonly HashSet<string> Uygulanan = new(StringComparer.OrdinalIgnoreCase)
    {
        "RewriteEngine", "RewriteBase", "RewriteRule", "RewriteCond",
        "DirectoryIndex",
        "ErrorDocument",
        "Options",
        "Order", "Allow", "Deny", "Require",
        "Header",
        "ExpiresActive", "ExpiresByType", "ExpiresDefault",
        "AuthType", "AuthName", "AuthUserFile",
        "AddType",
        "AddDefaultCharset", "DefaultCharset",
        "SetEnv",
        "Redirect", "RedirectMatch", "RedirectPermanent", "RedirectTemp"
    };

    /// <summary>
    /// Tanınan ama UYGULANMAYAN direktifler ve sebepleri. Sebep kullanıcının okuyacağı metindir;
    /// "desteklenmiyor" demekle yetinmez, ne yapması gerektiğini de söyler.
    /// </summary>
    private static readonly Dictionary<string, string> Uygulanmayan = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RewriteMap"] = "eşleme tabloları sunucu yapılandırmasında tanımlanır; Apache de bunu .htaccess içinde kabul etmez.",
        ["RewriteOptions"] = "basamaklandırma sırasını değiştiren seçenekler (Inherit/InheritBefore) uygulanmıyor; kurallar her zaman kökten içeriye doğru, alt dizin üstü ezerek işletilir.",
        ["RewriteLog"] = "Apache 2.4'te zaten kaldırıldı; kural izleme paneldeki .htaccess raporundan yapılır.",
        ["RewriteLogLevel"] = "Apache 2.4'te zaten kaldırıldı.",
        ["RewriteLock"] = "yalnızca harici RewriteMap programları için gerekliydi; RewriteMap desteklenmiyor.",
        ["Alias"] = "URL'i site kökünün dışındaki bir klasöre bağlar; panelden sanal dizin olarak tanımlanmalı.",
        ["AliasMatch"] = "URL'i site kökünün dışındaki bir klasöre bağlar; panelden sanal dizin olarak tanımlanmalı.",
        ["ScriptAlias"] = "CGI dizini eşlemesi; IIS tarafında işleyici olarak tanımlanmalı.",
        ["AddHandler"] = "işleyici eşlemesi IIS'te sunucu düzeyinde yönetilir (PHP FastCGI); panelin PHP ayarlarını kullanın.",
        ["SetHandler"] = "işleyici eşlemesi IIS'te sunucu düzeyinde yönetilir; panelin PHP ayarlarını kullanın.",
        ["RemoveHandler"] = "işleyici eşlemesi IIS'te sunucu düzeyinde yönetilir.",
        ["Action"] = "CGI eylem eşlemesi; IIS'te işleyici olarak tanımlanmalı.",
        ["php_value"] = "mod_php'ye aittir; IIS'te PHP FastCGI çalışır. Ayarı panelin PHP ayarlarından ya da .user.ini ile yapın.",
        ["php_flag"] = "mod_php'ye aittir; IIS'te PHP FastCGI çalışır. Ayarı panelin PHP ayarlarından ya da .user.ini ile yapın.",
        ["php_admin_value"] = "mod_php'ye aittir ve .htaccess'te zaten yalnızca sunucu yapılandırmasında geçerlidir.",
        ["php_admin_flag"] = "mod_php'ye aittir ve .htaccess'te zaten yalnızca sunucu yapılandırmasında geçerlidir.",
        ["SetEnvIf"] = "koşullu ortam değişkeni değerlendirilmiyor; aynı işi RewriteCond + RewriteRule ile yapabilirsiniz.",
        ["SetEnvIfNoCase"] = "koşullu ortam değişkeni değerlendirilmiyor; aynı işi RewriteCond + RewriteRule ile yapabilirsiniz.",
        ["BrowserMatch"] = "SetEnvIf'in eş anlamlısıdır; koşullu ortam değişkeni değerlendirilmiyor.",
        ["BrowserMatchNoCase"] = "SetEnvIf'in eş anlamlısıdır; koşullu ortam değişkeni değerlendirilmiyor.",
        ["UnsetEnv"] = "ortam değişkeni silme uygulanmıyor.",
        ["PassEnv"] = "sunucu sürecinin ortam değişkenleri siteye aktarılmıyor.",
        ["Satisfy"] = "kimlik doğrulama ile erişim denetiminin 'herhangi biri yeterli' birleşimi uygulanmıyor; ikisi de AYRI AYRI geçilmelidir.",
        ["AuthGroupFile"] = "grup dosyası okunmuyor; 'Require group' yerine 'Require user' kullanın.",
        ["AuthBasicProvider"] = "yalnızca AuthUserFile (htpasswd) sağlayıcısı destekleniyor.",
        ["AuthDigestProvider"] = "Digest kimlik doğrulaması desteklenmiyor; AuthType Basic kullanın (HTTPS altında).",
        ["AuthDigestDomain"] = "Digest kimlik doğrulaması desteklenmiyor.",
        ["AuthDBMUserFile"] = "DBM parola dosyaları okunmuyor; düz htpasswd dosyası kullanın.",
        ["AuthLDAPURL"] = "LDAP sağlayıcısı desteklenmiyor.",
        ["FallbackResource"] = "uygulanmıyor; aynı işi 'RewriteCond %{REQUEST_FILENAME} !-f' + 'RewriteRule ^ index.php [L]' ile yapabilirsiniz.",
        ["DirectorySlash"] = "dizin isteklerine otomatik eğik çizgi eklenmesi motorda değil, IIS tarafında karar veriliyor.",
        ["IndexOptions"] = "dizin listeleme biçimi IIS'in kendi listeleyicisine aittir.",
        ["IndexIgnore"] = "dizin listeleme biçimi IIS'in kendi listeleyicisine aittir.",
        ["HeaderName"] = "dizin listeleme biçimi IIS'in kendi listeleyicisine aittir.",
        ["ReadmeName"] = "dizin listeleme biçimi IIS'in kendi listeleyicisine aittir.",
        ["AddIcon"] = "dizin listeleme biçimi IIS'in kendi listeleyicisine aittir.",
        ["AddIconByType"] = "dizin listeleme biçimi IIS'in kendi listeleyicisine aittir.",
        ["AddIconByEncoding"] = "dizin listeleme biçimi IIS'in kendi listeleyicisine aittir.",
        ["DefaultIcon"] = "dizin listeleme biçimi IIS'in kendi listeleyicisine aittir.",
        ["AddDescription"] = "dizin listeleme biçimi IIS'in kendi listeleyicisine aittir.",
        ["AddOutputFilterByType"] = "çıktı filtreleri (sıkıştırma) uygulanmıyor; sıkıştırmayı IIS tarafından açın.",
        ["AddOutputFilter"] = "çıktı filtreleri uygulanmıyor.",
        ["SetOutputFilter"] = "çıktı filtreleri uygulanmıyor.",
        ["SetInputFilter"] = "girdi filtreleri uygulanmıyor.",
        ["AddEncoding"] = "içerik kodlaması eşlemesi uygulanmıyor.",
        ["AddLanguage"] = "içerik pazarlığı (content negotiation) yapılmıyor.",
        ["AddCharset"] = "içerik pazarlığı (content negotiation) yapılmıyor.",
        ["LanguagePriority"] = "içerik pazarlığı (content negotiation) yapılmıyor.",
        ["ForceLanguagePriority"] = "içerik pazarlığı (content negotiation) yapılmıyor.",
        ["RemoveType"] = "MIME eşlemesinden silme uygulanmıyor; AddType ile üzerine yazın.",
        ["RemoveEncoding"] = "içerik kodlaması eşlemesi uygulanmıyor.",
        ["FileETag"] = "ETag üretimi IIS'e aittir.",
        ["ContentDigest"] = "Content-MD5 başlığı üretilmiyor.",
        ["LimitRequestBody"] = "istek gövdesi sınırı IIS'in istek filtreleme ayarlarındadır.",
        ["SSLRequireSSL"] = "uygulanmıyor; HTTPS zorlamasını 'RewriteCond %{HTTPS} !=on' ile yapın.",
        ["SSLOptions"] = "mod_ssl ayarları uygulanmıyor.",
        ["SSLRequire"] = "mod_ssl ifade dili uygulanmıyor.",
        ["CheckSpelling"] = "yazım düzeltmeli yönlendirme uygulanmıyor.",
        ["ServerSignature"] = "sunucu imzası IIS tarafından üretilmiyor.",
        ["XSendFile"] = "mod_xsendfile desteklenmiyor.",
        ["ExpiresByTypeMatch"] = "Apache'de böyle bir direktif yok; ExpiresByType kullanın.",
        ["ErrorLog"] = ".htaccess içinde Apache tarafından da kabul edilmez; sunucu yapılandırmasına aittir.",
        ["CustomLog"] = ".htaccess içinde Apache tarafından da kabul edilmez; sunucu yapılandırmasına aittir.",
        ["RequestHeader"] = "gelen istek başlıklarının değiştirilmesi uygulanmıyor; yanıt başlıkları için Header kullanın."
    };

    /// <summary>Motorun UYGULADIĞI blok tipleri.</summary>
    private static readonly HashSet<string> UygulananBlok = new(StringComparer.OrdinalIgnoreCase)
    {
        "IfModule", "Files", "FilesMatch", "Directory", "DirectoryMatch", "Limit", "LimitExcept"
    };

    private static readonly Dictionary<string, string> UygulanmayanBlok = new(StringComparer.OrdinalIgnoreCase)
    {
        ["IfDefine"] = "sunucu başlatma bayraklarına (-D) bakar; panelde böyle bir bayrak yok, blok bütünüyle ATLANIR.",
        ["IfVersion"] = "Apache sürüm karşılaştırması yapılamaz, blok bütünüyle ATLANIR.",
        ["Location"] = "Apache de .htaccess içinde kabul etmez, blok bütünüyle ATLANIR.",
        ["LocationMatch"] = "Apache de .htaccess içinde kabul etmez, blok bütünüyle ATLANIR.",
        ["VirtualHost"] = "Apache de .htaccess içinde kabul etmez, blok bütünüyle ATLANIR.",
        ["Proxy"] = "vekil sunucu yapılandırması desteklenmiyor, blok bütünüyle ATLANIR.",
        ["ProxyMatch"] = "vekil sunucu yapılandırması desteklenmiyor, blok bütünüyle ATLANIR.",
        ["RequireAll"] = "gruplanmış Require ifadeleri değerlendirilmiyor; düz 'Require' satırları kullanın.",
        ["RequireAny"] = "gruplanmış Require ifadeleri değerlendirilmiyor; düz 'Require' satırları kullanın.",
        ["RequireNone"] = "gruplanmış Require ifadeleri değerlendirilmiyor; düz 'Require' satırları kullanın.",
        ["If"] = "Apache ifade dili (expr) değerlendirilmiyor, blok bütünüyle ATLANIR.",
        ["Else"] = "Apache ifade dili (expr) değerlendirilmiyor, blok bütünüyle ATLANIR.",
        ["ElseIf"] = "Apache ifade dili (expr) değerlendirilmiyor, blok bütünüyle ATLANIR."
    };

    public static bool IsSupported(string name) => Uygulanan.Contains(name);

    public static bool TryGetUnsupportedReason(string name, out string reason) =>
        Uygulanmayan.TryGetValue(name, out reason!);

    public static bool IsSupportedSection(string name) => UygulananBlok.Contains(name);

    public static bool TryGetUnsupportedSectionReason(string name, out string reason) =>
        UygulanmayanBlok.TryGetValue(name, out reason!);
}
