using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Migration;

/// <summary>
/// Plesk REST API'sine (https://host:8443/api/v2) bağlanıp domain'leri OTOMATİK keşfeder (göç sihirbazını doldurmak
/// için). Kimlik DOĞRULAMA iki yoldan: (1) gizli API anahtarı (X-API-Key; Plesk'te <c>plesk bin secret_key</c> ile
/// üretilir) ya da (2) Plesk yönetici kullanıcı adı + parolası (Basic auth). Yalnız domain LİSTESİ çekilir; dosya/DB
/// parolaları Plesk API'sinden alınamaz → admin bunları sihirbazda girer. Manuel mod her zaman çalışır; keşif kolaylıktır.
/// </summary>
public sealed class PleskDiscoverRequest
{
    [Required]
    [StringLength(255, MinimumLength = 1)]
    public string Host { get; init; } = string.Empty;

    public int Port { get; init; } = 8443;

    /// <summary>Plesk gizli API anahtarı (X-API-Key). Boşsa UserName + Password (Basic auth) kullanılır.</summary>
    [StringLength(512)]
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>Plesk yönetici kullanıcı adı (API anahtarı verilmediyse Basic auth için).</summary>
    [StringLength(128)]
    public string UserName { get; init; } = string.Empty;

    /// <summary>Plesk yönetici parolası (Basic auth için).</summary>
    [StringLength(256)]
    public string Password { get; init; } = string.Empty;

    /// <summary>
    /// Yöneticinin BİLEREK kabul ettiği sunucu sertifikası parmak izi (<c>SHA256:1A:2B:…</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Boş bırakılırsa sertifika TAM doğrulanır.</b> Kaynak Plesk kendi imzalı sertifika
    /// taşıyorsa istek <c>migration.tls.untrusted</c> ile reddedilir ve yanıt hem ret sebebini hem
    /// bu parmak izini söyler; yönetici parmak izini kaynak sunucudan teyit edip aynı isteği bu
    /// alan dolu olarak tekrarlar. Kabul YALNIZ o parmak izine açılır — "her sertifikayı kabul et"
    /// diye bir değer yoktur.
    /// </para>
    /// <para>
    /// Kalıp <c>ssh</c>'ın host key onayından alınmıştır; Plesk Migrator da Linux göçünde parmak
    /// izinin önceden teyit edilmesini ister.
    /// </para>
    /// </remarks>
    [StringLength(128)]
    public string AcceptCertificateFingerprint { get; init; } = string.Empty;
}
