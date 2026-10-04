using System.Security.Claims;
using AdaPanel.Core.Common;

namespace AdaPanel.Api.Security;

/// <summary>
/// Kişisel API anahtarının YETENEKLERİNİ REST tarafında da bağlayıcı kılan saf karar noktası.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden gerekli — REST'i anahtara açmanın yan etkisi:</b> <c>AllowDestructive</c> ve
/// <c>AllowTerminal</c> yetenekleri bugüne kadar YALNIZCA MCP araçlarında sorgulanıyordu
/// (<see cref="Mcp.McpCapability"/>). REST uçları anahtarı kabul etmeye başladığı anda, bu denetim
/// olmasaydı "yıkıcı işlemlere izin vermeyen" bir anahtar tek bir HTTP çağrısıyla siteyi silebilir,
/// sunucuda komut çalıştırabilirdi — yani panelin kurduğu yetenek modeli sessizce ANLAMSIZLAŞIRDI.
/// Bir yeteneğin iki kapısı varsa ikisi de aynı kilidi taşımalı.
/// </para>
/// <para>
/// <b>Kural — anahtar varsayılan olarak SALT OKUR:</b> durum değiştiren istek (POST/PUT/PATCH/DELETE)
/// <c>AllowDestructive</c> ister. MCP'de her aracın yıkıcılığı tek tek işaretlenebiliyor çünkü araç
/// sayısı 8; REST'te 50'nin üzerinde controller ve yüzlerce eylem var, hepsini elle işaretlemek
/// unutulacağı için fiilen işaretlememekle aynı kapıya çıkardı. Bu yüzden ayrım HTTP fiiline
/// dayandırıldı: kaba ama ÖLÇÜLEBİLİR ve güvenli yönde hata yapan bir kural. İzleme aracı gibi okuyan
/// entegrasyonlar sade bir anahtarla çalışır; yazan entegrasyon (WHMCS) anahtar oluştururken kutuyu
/// açar ve bunu bilerek yapar.
/// </para>
/// <para>
/// <b>Neden terminal AYRI:</b> komut çalıştırmak paneli değil SUNUCUYU vermektir; anahtar sahibi
/// yönetici olsa bile bu yetki açıkça verilmedikçe kullanılamaz (aynı ayrım MCP'de de var).
/// </para>
/// <para>
/// <b>Kimlik bilgisi yöneten uçlar hiç bu yola girmez:</b> <c>AuthController</c> ve
/// <c>ApiTokensController</c> sınıf düzeyinde ÇEREZ şemasına sabitlendi. Sebep yetki yükseltmesi:
/// aksi hâlde salt-okur bir anahtar, kendine <c>AllowDestructive</c> ve <c>AllowTerminal</c> açık YENİ
/// bir anahtar basarak kendi kısıtını kaldırabilirdi. Bu, burada yol karşılaştırmasıyla değil
/// şema kısıtıyla çözüldü: yol adı değişirse kural sessizce ölmesin.
/// </para>
/// </remarks>
public static class PanelTokenCapabilities
{
    /// <summary>
    /// Komut çalıştırma ucu. Yol karşılaştırması kırılgan olduğu için bir test
    /// <c>TerminalController</c>'ın rotasının bununla aynı kaldığını doğrular.
    /// </summary>
    public const string TerminalPathPrefix = "/api/v1/terminal";

    /// <summary>Reddin makine tarafından okunabilir sebebi (çeviri gerektirmeyen sabit etiket).</summary>
    public const string ReadOnlyReason = "apitoken.read_only";

    /// <summary>Reddin makine tarafından okunabilir sebebi (çeviri gerektirmeyen sabit etiket).</summary>
    public const string TerminalReason = "apitoken.terminal_not_allowed";

    /// <summary>Anahtar yıkıcı/yazma işlemlerine izin veriyor mu?</summary>
    public static bool AllowsDestructive(ClaimsPrincipal user) =>
        user.FindFirst(PanelTokenAuthenticationHandler.AllowDestructiveClaim)?.Value == "true";

    /// <summary>Anahtar sunucuda komut çalıştırmaya izin veriyor mu?</summary>
    public static bool AllowsTerminal(ClaimsPrincipal user) =>
        user.FindFirst(PanelTokenAuthenticationHandler.AllowTerminalClaim)?.Value == "true";

    /// <summary>Yol komut çalıştırma ucuna mı düşüyor?</summary>
    public static bool IsTerminalPath(string? path)
    {
        var normalized = (path ?? string.Empty).TrimEnd('/');
        return normalized.Equals(TerminalPathPrefix, StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith(TerminalPathPrefix + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// İsteğe izin verilip verilmediğine karar verir; verilmiyorsa 403 fırlatır.
    /// </summary>
    /// <remarks>
    /// HttpContext'ten bağımsız: karar (kimlik + fiil + yol) saf bir fonksiyon olduğu için filtre
    /// olmadan da doğrulanabiliyor. Anahtarla doğrulanmamış istekler (çerez) hiç dokunulmadan geçer —
    /// panel arayüzünün davranışı değişmez.
    /// </remarks>
    public static void EnsureAllowed(ClaimsPrincipal user, string? httpMethod, string? path)
    {
        if (!PanelAuthentication.IsTokenAuthenticated(user))
        {
            return;
        }

        // Anahtar YENİDEN ÜRETİLMİYOR: "auth.forbidden" panelde zaten var ve iki dilde çevrili.
        // Sebep Details'e konur ki entegrasyon "neden 403" sorusunu tek istekte yanıtlayabilsin.
        if (IsTerminalPath(path) && !AllowsTerminal(user))
        {
            throw new AppException("auth.forbidden", 403, 403, TerminalReason);
        }

        // Inspection/download bodies do not require destructive capability. Neighboring
        // mutations and every other non-read method retain their existing restriction.
        if (PanelAuthentication.IsStateChanging(httpMethod) && !ReadOnlyPostRoutes.IsMatch(httpMethod, path) && !AllowsDestructive(user))
        {
            throw new AppException("auth.forbidden", 403, 403, ReadOnlyReason);
        }
    }
}
