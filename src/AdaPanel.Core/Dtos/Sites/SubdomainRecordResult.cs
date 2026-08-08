namespace AdaPanel.Core.Dtos.Sites;

/// <summary>
/// Alt alan adı için üst bölgeye A kaydı yazma girişiminin sonucu.
/// </summary>
/// <remarks>
/// Ayrı bir tür kullanılmasının sebebi: <b>"yazamadım" ile "zaten vardı" aynı şey değildir</b> ve
/// ikisi de "başardım" değildir. Tek bir <c>bool</c> döndürülseydi, bölgesi olmayan bir alan adında
/// panel sessizce başarılı görünür, kullanıcı DNS kaydının atıldığını sanır ve site açılmayınca
/// nedenini bulamazdı. Her durumun kendi adı var ve arayüzde ayrı ayrı yazılıyor.
/// </remarks>
public sealed class SubdomainRecordResult
{
    /// <summary>Kayıt bu çağrıda oluşturuldu.</summary>
    public bool Created { get; init; }

    /// <summary>Aynı ada sahip bir kayıt zaten vardı; üstüne YAZILMADI.</summary>
    public bool AlreadyExists { get; init; }

    /// <summary>
    /// Var olan kayıt ZATEN DOĞRU: ya panelin kendi yönetilen varsayılan kaydı ya da değeri bu
    /// sunucuyu gösteriyor.
    /// </summary>
    /// <remarks>
    /// Bu ayrım olmadan panel kendi kendine uyarı üretiyordu. Varsayılan bölge şablonu zaten
    /// <c>www</c>, <c>mail</c>, <c>ftp</c>, <c>webmail</c>, <c>ns1</c>, <c>ns2</c> kayıtlarını
    /// oluşturuyor; biri <c>mail.ornek.com</c> sitesi açtığında "kayıt zaten var, dokunmadım"
    /// UYARISI çıkıyordu. Oysa o kayıt panelin kendi doğru kaydı ve yapılacak bir şey yok.
    /// Her şey yolundayken uyarı basmak, gerçek uyarıların da okunmamasına yol açar.
    /// </remarks>
    public bool AlreadyCorrect { get; init; }

    /// <summary>Üst alan adının panelde yönetilen bir DNS bölgesi yok.</summary>
    public bool ZoneMissing { get; init; }

    /// <summary>Var olan kaydın türü/değeri (yalnız <see cref="AlreadyExists"/> iken dolu).</summary>
    public string ExistingRecord { get; init; } = string.Empty;

    /// <summary>Yazılan ya da yazılacak olan adres (yalnız <see cref="Created"/> iken dolu).</summary>
    public string Address { get; init; } = string.Empty;

    /// <summary>
    /// Hiçbiri olmadıysa sebebi. Dört durumun da yanlış olduğu sessiz bir sonuç bırakmamak için var:
    /// "yazamadım ama neden bilmiyorum", kullanıcının hata arayamadığı en kötü çıktıdır.
    /// </summary>
    public string Error { get; init; } = string.Empty;
}
