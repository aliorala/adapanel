namespace AdaPanel.Core.Dtos.Common;

/// <summary>
/// Uzun süren bir işlemin (ilk kurulum önerilen yığını, yazılım mağazası kurulumları vb.)
/// adım adım canlı ilerlemesi. Frontend bunu kısa aralıklarla yoklayıp "şu an X yapılıyor"
/// biçiminde gösterir; böylece işlem donmuş gibi görünmez.
/// </summary>
public sealed class OperationProgressDto
{
    /// <summary>Devam eden işlemin başlığı (örn. "Önerilen yazılımlar kuruluyor").</summary>
    public string Title { get; init; } = string.Empty;

    public bool Running { get; init; }

    public bool Completed { get; init; }

    public bool Failed { get; init; }

    public string Error { get; init; } = string.Empty;

    /// <summary>İşlemin başlangıcından bu yana geçen tüm adımlar (sıralı).</summary>
    public OperationProgressStepDto[] Steps { get; init; } = [];

    /// <summary>İşlemin başladığı an (UTC); frontend gerekirse süre gösterebilir.</summary>
    public DateTime? StartedAtUtc { get; init; }
}

public sealed class OperationProgressStepDto
{
    public string Name { get; init; } = string.Empty;

    /// <summary>running | done | failed</summary>
    public string Status { get; init; } = "running";

    /// <summary>İsteğe bağlı ek açıklama (örn. hata özeti).</summary>
    public string Detail { get; init; } = string.Empty;
}
