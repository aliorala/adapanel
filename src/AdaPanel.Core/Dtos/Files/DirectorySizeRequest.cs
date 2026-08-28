namespace AdaPanel.Core.Dtos.Files;

/// <summary>Bir klasörün özyinelemeli boyutunu isteyen çağrının gövdesi.</summary>
/// <remarks>
/// <b>Neden GET değil de gövdeli POST:</b> yol, dosya yöneticisinin ürettiği serbest bir dizedir —
/// içinde boşluk, <c>#</c>, <c>%</c> ve Unicode ad geçebilir. Sorgu dizesinde taşınan böyle bir yol
/// istemci/vekil sunucu/IIS zincirinde bir kez daha çözülüp bozulabilir; aynı sebeple dosya arama
/// (<c>POST /files/search</c>) da gövde kullanır. Uç durum DEĞİŞTİRMEZ, yalnız fiil POST'tur.
/// </remarks>
public sealed class DirectorySizeRequest
{
    /// <summary>Boyutu hesaplanacak KLASÖRÜN sanal yolu (ör. <c>sites/a.test/wp-content</c>).</summary>
    public string Path { get; set; } = string.Empty;
}
